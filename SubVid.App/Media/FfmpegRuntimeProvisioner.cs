using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SubVid.App.Core;

namespace SubVid.App.Media;

public sealed record FfmpegRuntimePackage(
    string Version,
    Uri DownloadUri,
    long ArchiveSizeBytes,
    string ArchiveSha256,
    string License,
    Uri SourceUri);

public sealed record FfmpegInstallProgress(
    string Phase,
    double Percent,
    string Message,
    long BytesProcessed,
    long TotalBytes);

public sealed record FfmpegRuntimeStatus(
    string State,
    bool Ready,
    bool Managed,
    string Source,
    string? Version,
    string TargetVersion,
    string? FfmpegPath,
    string? FfprobePath,
    string InstallDirectory,
    long DownloadBytes,
    string License,
    string SourceUrl);

public sealed class FfmpegRuntimeException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class FfmpegRuntimeProvisioner : IDisposable
{
    public static readonly FfmpegRuntimePackage DefaultPackage = new(
        "9.0.1",
        new Uri("https://github.com/GyanD/codexffmpeg/releases/download/9.0.1/ffmpeg-9.0.1-essentials_build.zip"),
        111_253_802,
        "fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9",
        "GPL-3.0",
        new Uri("https://github.com/FFmpeg/FFmpeg/commit/bf1b838f2a"));

    private static readonly HashSet<string> AllowedDownloadHosts = new(
        ["gyan.dev", "www.gyan.dev", "github.com", "release-assets.githubusercontent.com"],
        StringComparer.OrdinalIgnoreCase);
    private readonly AppPaths _paths;
    private readonly FfmpegRuntimeSettingsStore _settings;
    private readonly FfmpegRuntimePackage _package;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<string, string, CancellationToken, Task> _validator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FfmpegRuntimeProvisioner(
        AppPaths paths,
        HttpClient? httpClient = null,
        FfmpegRuntimePackage? package = null,
        Func<string, string, CancellationToken, Task>? validator = null)
    {
        _paths = paths;
        _settings = new FfmpegRuntimeSettingsStore(paths);
        _package = package ?? DefaultPackage;
        ValidatePackage(_package);
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = httpClient is null;
        _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("SubVid-App/1.0");
        _validator = validator ?? ValidateExecutablesAsync;
    }

    public string ManagedDirectory => Path.Combine(_paths.RootDirectory, "Tools", "ffmpeg");

    private string MarkerPath => Path.Combine(ManagedDirectory, ".subvid-ffmpeg.json");

    private string LogPath => Path.Combine(_paths.LogsDirectory, "ffmpeg-install.log");

    public FfmpegRuntimeStatus GetStatus()
    {
        var hasFfmpeg = MediaToolLocator.TryLocate(_paths, "ffmpeg", "SUBVID_FFMPEG_PATH", out var ffmpegPath);
        var hasFfprobe = MediaToolLocator.TryLocate(_paths, "ffprobe", "SUBVID_FFPROBE_PATH", out var ffprobePath);
        var ready = hasFfmpeg && hasFfprobe;
        var source = ResolveSource(ready ? ffmpegPath : null, ready ? ffprobePath : null);
        var managed = ready
            && IsSamePath(ffmpegPath, Path.Combine(ManagedDirectory, "ffmpeg.exe"))
            && IsSamePath(ffprobePath, Path.Combine(ManagedDirectory, "ffprobe.exe"));
        var version = managed ? ReadManagedVersion() : null;
        return new FfmpegRuntimeStatus(
            ready ? "READY" : "MISSING",
            ready,
            managed,
            source,
            version,
            _package.Version,
            hasFfmpeg ? ffmpegPath : null,
            hasFfprobe ? ffprobePath : null,
            ManagedDirectory,
            _package.ArchiveSizeBytes,
            _package.License,
            _package.SourceUri.AbsoluteUri);
    }

    public FfmpegRuntimeStatus UseExternalDirectory(string directory)
    {
        var resolved = Path.GetFullPath(directory);
        var ffmpegPath = Path.Combine(resolved, "ffmpeg.exe");
        var ffprobePath = Path.Combine(resolved, "ffprobe.exe");
        _settings.Save(ffmpegPath, ffprobePath);
        WriteLog("FFMPEG_EXTERNAL_SELECTED", resolved);
        return GetStatus();
    }

    public async Task<FfmpegRuntimeStatus> EnsureReadyAsync(
        IProgress<FfmpegInstallProgress>? progress,
        bool force,
        CancellationToken cancellationToken)
    {
        var current = GetStatus();
        if (current.Ready && !force) return current;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            current = GetStatus();
            if (current.Ready && !force) return current;

            EnsureDiskSpace();
            var temporaryRoot = Path.Combine(_paths.RootDirectory, "Temp", "ffmpeg");
            var installParent = Path.GetDirectoryName(ManagedDirectory)
                ?? throw new FfmpegRuntimeException("FFMPEG_INSTALL_FAILED", "Thư mục cài FFmpeg không hợp lệ.");
            Directory.CreateDirectory(temporaryRoot);
            Directory.CreateDirectory(installParent);

            var partialPath = Path.Combine(temporaryRoot, $"ffmpeg-{_package.Version}.zip.partial");
            var stagingPath = Path.Combine(installParent, $".ffmpeg-staging-{Guid.NewGuid():N}");
            try
            {
                progress?.Report(CreateProgress("DOWNLOAD", 0, "Đang kết nối nguồn tải FFmpeg an toàn.", 0));
                await DownloadAndVerifyAsync(partialPath, progress, cancellationToken);
                progress?.Report(CreateProgress("EXTRACT", 82, "Đang giải nén FFmpeg và FFprobe.", _package.ArchiveSizeBytes));
                await ExtractRequiredFilesAsync(partialPath, stagingPath, cancellationToken);
                progress?.Report(CreateProgress("VERIFY", 90, "Đang kiểm tra bộ công cụ video.", _package.ArchiveSizeBytes));
                await _validator(
                    Path.Combine(stagingPath, "ffmpeg.exe"),
                    Path.Combine(stagingPath, "ffprobe.exe"),
                    cancellationToken);
                await WriteMetadataAsync(stagingPath, cancellationToken);
                progress?.Report(CreateProgress("INSTALL", 96, "Đang hoàn tất cài đặt an toàn.", _package.ArchiveSizeBytes));
                SwapManagedDirectory(stagingPath);
                _settings.Clear();
                var installed = GetStatus();
                if (!File.Exists(Path.Combine(ManagedDirectory, "ffmpeg.exe"))
                    || !File.Exists(Path.Combine(ManagedDirectory, "ffprobe.exe"))
                    || !File.Exists(MarkerPath)
                    || !installed.Ready)
                {
                    throw new FfmpegRuntimeException(
                        "FFMPEG_VALIDATION_FAILED",
                        "FFmpeg đã tải nhưng App chưa thể sử dụng bộ công cụ vừa cài.");
                }

                WriteLog("FFMPEG_INSTALL_COMPLETED", _package.Version);
                progress?.Report(CreateProgress("READY", 100, "FFmpeg đã sẵn sàng.", _package.ArchiveSizeBytes));
                return installed;
            }
            catch (OperationCanceledException)
            {
                WriteLog("FFMPEG_INSTALL_CANCELLED", _package.Version);
                throw;
            }
            catch (FfmpegRuntimeException exception)
            {
                WriteLog(exception.Code, exception.Message);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException
                or IOException
                or UnauthorizedAccessException
                or InvalidDataException)
            {
                WriteLog("FFMPEG_INSTALL_FAILED", exception.GetType().Name);
                throw new FfmpegRuntimeException(
                    "FFMPEG_INSTALL_FAILED",
                    "Không thể cài FFmpeg. Hãy kiểm tra mạng, dung lượng đĩa và phần mềm bảo vệ máy.",
                    exception);
            }
            finally
            {
                TryDeleteFile(partialPath);
                TryDeleteDirectory(stagingPath, installParent);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAndVerifyAsync(
        string partialPath,
        IProgress<FfmpegInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _package.DownloadUri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength != _package.ArchiveSizeBytes)
        {
            throw new FfmpegRuntimeException(
                "FFMPEG_ARCHIVE_INVALID",
                "Kích thước gói FFmpeg không đúng với bản đã được xác minh.");
        }
        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null
            || responseUri.Scheme != Uri.UriSchemeHttps
            || !AllowedDownloadHosts.Contains(responseUri.Host))
        {
            throw new FfmpegRuntimeException(
                "FFMPEG_DOWNLOAD_SOURCE_INVALID",
                "Nguồn tải FFmpeg không nằm trong danh sách tin cậy.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            partialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long processed = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            processed += read;
            if (processed > _package.ArchiveSizeBytes)
            {
                throw new FfmpegRuntimeException(
                    "FFMPEG_ARCHIVE_INVALID",
                    "Gói FFmpeg tải về lớn hơn kích thước đã được xác minh.");
            }
            var percent = Math.Min(80, processed * 80d / _package.ArchiveSizeBytes);
            progress?.Report(CreateProgress(
                "DOWNLOAD",
                percent,
                "Đang tải trực tiếp FFmpeg về máy.",
                processed));
        }

        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        var actualHash = hash.GetHashAndReset();
        var expectedHash = Convert.FromHexString(_package.ArchiveSha256);
        if (processed != _package.ArchiveSizeBytes
            || !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new FfmpegRuntimeException(
                "FFMPEG_HASH_INVALID",
                "Gói FFmpeg tải về không đúng checksum và đã bị từ chối.");
        }
    }

    private static async Task ExtractRequiredFilesAsync(
        string archivePath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingPath);
        using var archive = ZipFile.OpenRead(archivePath);
        await ExtractSingleAsync(archive, "ffmpeg.exe", Path.Combine(stagingPath, "ffmpeg.exe"), cancellationToken);
        await ExtractSingleAsync(archive, "ffprobe.exe", Path.Combine(stagingPath, "ffprobe.exe"), cancellationToken);
    }

    private static async Task ExtractSingleAsync(
        ZipArchive archive,
        string fileName,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var matches = archive.Entries.Where(entry =>
            entry.FullName.Replace('\\', '/').EndsWith($"/bin/{fileName}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || matches[0].Length <= 0 || matches[0].Length > 350L * 1024 * 1024)
        {
            throw new FfmpegRuntimeException(
                "FFMPEG_ARCHIVE_INVALID",
                $"Gói cài đặt thiếu {fileName} hợp lệ.");
        }

        await using var source = matches[0].Open();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private async Task WriteMetadataAsync(string stagingPath, CancellationToken cancellationToken)
    {
        var marker = new ManagedFfmpegMarker(
            _package.Version,
            _package.DownloadUri.AbsoluteUri,
            _package.ArchiveSha256,
            _package.License,
            _package.SourceUri.AbsoluteUri,
            DateTime.UtcNow);
        await File.WriteAllTextAsync(
            Path.Combine(stagingPath, ".subvid-ffmpeg.json"),
            JsonSerializer.Serialize(marker, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(stagingPath, "THIRD-PARTY-NOTICE.txt"),
            $"FFmpeg {_package.Version}\nLicense: {_package.License}\nSource: {_package.SourceUri.AbsoluteUri}\nBinary provider: {_package.DownloadUri.GetLeftPart(UriPartial.Authority)}\n",
            cancellationToken);
    }

    private void SwapManagedDirectory(string stagingPath)
    {
        var parent = Path.GetDirectoryName(ManagedDirectory)!;
        var backupPath = Path.Combine(parent, $".ffmpeg-backup-{Guid.NewGuid():N}");
        var movedExisting = false;
        try
        {
            if (Directory.Exists(ManagedDirectory))
            {
                Directory.Move(ManagedDirectory, backupPath);
                movedExisting = true;
            }

            Directory.Move(stagingPath, ManagedDirectory);
        }
        catch
        {
            if (!Directory.Exists(ManagedDirectory) && movedExisting && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, ManagedDirectory);
                movedExisting = false;
            }

            throw;
        }
        finally
        {
            if (movedExisting) TryDeleteDirectory(backupPath, parent);
        }
    }

    private static async Task ValidateExecutablesAsync(
        string ffmpegPath,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        await RunVersionCheckAsync(ffmpegPath, "ffmpeg", cancellationToken);
        await RunVersionCheckAsync(ffprobePath, "ffprobe", cancellationToken);
    }

    private static async Task RunVersionCheckAsync(
        string executablePath,
        string expectedTool,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-version");
        using var process = Process.Start(startInfo)
            ?? throw new FfmpegRuntimeException("FFMPEG_VALIDATION_FAILED", $"Không thể chạy {expectedTool}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new FfmpegRuntimeException("FFMPEG_VALIDATION_FAILED", $"{expectedTool} không phản hồi khi kiểm tra.");
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0
            || !(output + error).Contains(expectedTool + " version", StringComparison.OrdinalIgnoreCase))
        {
            throw new FfmpegRuntimeException("FFMPEG_VALIDATION_FAILED", $"{expectedTool} vừa tải không hoạt động hợp lệ.");
        }
    }

    private string? ReadManagedVersion()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            return JsonSerializer.Deserialize<ManagedFfmpegMarker>(
                File.ReadAllText(MarkerPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Version;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string ResolveSource(string? ffmpegPath, string? ffprobePath)
    {
        if (ffmpegPath is null || ffprobePath is null) return "NONE";
        var configuredFfmpeg = Environment.GetEnvironmentVariable("SUBVID_FFMPEG_PATH");
        var configuredFfprobe = Environment.GetEnvironmentVariable("SUBVID_FFPROBE_PATH");
        if (IsSamePath(ffmpegPath, configuredFfmpeg) || IsSamePath(ffprobePath, configuredFfprobe))
        {
            return "ENVIRONMENT";
        }

        var manual = _settings.TryLoad();
        if (manual is not null
            && IsSamePath(ffmpegPath, manual.FfmpegPath)
            && IsSamePath(ffprobePath, manual.FfprobePath))
        {
            return "CUSTOM";
        }

        return IsSamePath(ffmpegPath, Path.Combine(ManagedDirectory, "ffmpeg.exe"))
            && IsSamePath(ffprobePath, Path.Combine(ManagedDirectory, "ffprobe.exe"))
                ? "MANAGED"
                : "SYSTEM";
    }

    private void EnsureDiskSpace()
    {
        var root = Path.GetPathRoot(_paths.RootDirectory);
        if (root is null) return;
        var drive = new DriveInfo(root);
        var required = Math.Max(600L * 1024 * 1024, _package.ArchiveSizeBytes * 4);
        if (drive.AvailableFreeSpace < required)
        {
            throw new FfmpegRuntimeException(
                "FFMPEG_DISK_SPACE_INSUFFICIENT",
                "Cần tối thiểu 600 MB trống để tải và cài FFmpeg.");
        }
    }

    private FfmpegInstallProgress CreateProgress(
        string phase,
        double percent,
        string message,
        long processed) => new(
            phase,
            Math.Clamp(percent, 0, 100),
            message,
            Math.Clamp(processed, 0, _package.ArchiveSizeBytes),
            _package.ArchiveSizeBytes);

    private void WriteLog(string code, string detail)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:O}\t{code}\t{detail.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never make installation fail.
        }
    }

    private static void ValidatePackage(FfmpegRuntimePackage package)
    {
        if (package.DownloadUri.Scheme != Uri.UriSchemeHttps
            || !AllowedDownloadHosts.Contains(package.DownloadUri.Host)
            || package.ArchiveSizeBytes <= 0
            || package.ArchiveSha256.Length != 64)
        {
            throw new ArgumentException("FFmpeg runtime package metadata is invalid.", nameof(package));
        }

        _ = Convert.FromHexString(package.ArchiveSha256);
    }

    private static bool IsSamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A later installation overwrites the same partial file.
        }
    }

    private static void TryDeleteDirectory(string path, string expectedParent)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var resolvedPath = Path.GetFullPath(path);
            var resolvedParent = Path.GetFullPath(expectedParent);
            if (!string.Equals(Path.GetDirectoryName(resolvedPath), resolvedParent, StringComparison.OrdinalIgnoreCase)
                || !(Path.GetFileName(resolvedPath).StartsWith(".ffmpeg-staging-", StringComparison.Ordinal)
                    || Path.GetFileName(resolvedPath).StartsWith(".ffmpeg-backup-", StringComparison.Ordinal)))
            {
                return;
            }

            Directory.Delete(resolvedPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Staging and backup directories are recoverable leftovers.
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record ManagedFfmpegMarker(
        string Version,
        string DownloadUrl,
        string ArchiveSha256,
        string License,
        string SourceUrl,
        DateTime InstalledAtUtc);
}

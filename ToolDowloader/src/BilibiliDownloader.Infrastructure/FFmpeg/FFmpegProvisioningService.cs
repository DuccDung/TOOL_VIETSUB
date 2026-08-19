using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Infrastructure.FFmpeg;

public sealed class FFmpegProvisioningService : IFFmpegProvisioningService, IDisposable
{
    private readonly IFFmpegDiscoveryService _discoveryService;
    private readonly IFFmpegPackageDownloader _packageDownloader;
    private readonly IFFmpegPackageVerifier _packageVerifier;
    private readonly ISecureArchiveExtractor _archiveExtractor;
    private readonly ISettingsService _settingsService;
    private readonly IFileService _fileService;
    private readonly FFmpegOptions _options;
    private readonly ILogger<FFmpegProvisioningService> _logger;
    private readonly SemaphoreSlim _provisioningGate = new(1, 1);
    private FFmpegProvisioningResultDto? _cached;
    private bool _disposed;

    public FFmpegProvisioningService(
        IFFmpegDiscoveryService discoveryService,
        IFFmpegPackageDownloader packageDownloader,
        IFFmpegPackageVerifier packageVerifier,
        ISecureArchiveExtractor archiveExtractor,
        ISettingsService settingsService,
        IFileService fileService,
        IOptions<FFmpegOptions> options,
        ILogger<FFmpegProvisioningService> logger)
    {
        _discoveryService = discoveryService;
        _packageDownloader = packageDownloader;
        _packageVerifier = packageVerifier;
        _archiveExtractor = archiveExtractor;
        _settingsService = settingsService;
        _fileService = fileService;
        _options = options.Value;
        _logger = logger;
        _settingsService.SettingsChanged += SettingsChanged;
    }

    public async Task<FFmpegProvisioningResultDto?> FindAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && File.Exists(cached.ExecutablePath))
        {
            return cached;
        }

        var discovered = await _discoveryService.FindAvailableAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _cached, discovered);
        return discovered;
    }

    public async Task<FFmpegProvisioningResultDto> EnsureAvailableAsync(
        IProgress<FFmpegProvisioningProgressDto>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entered = false;
        try
        {
            Report(progress, FFmpegProvisioningState.Checking, "Đang kiểm tra FFmpeg...");
            await _provisioningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;

            var available = await FindAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (available is not null)
            {
                await TryPersistResolvedPathAsync(available.ExecutablePath, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _cached, available);
                ReportReady(progress, available);
                return available;
            }

            ValidateOptions();
            var stagingDirectory = CreateStagingDirectory();
            try
            {
                EnsureInstallationDiskSpace(stagingDirectory);
                var archivePath = Path.Combine(stagingDirectory, "ffmpeg-package.zip");
                var downloadProgress = new InlineProgress<FFmpegPackageDownloadProgress>(value =>
                {
                    double? percentage = value.TotalBytes is > 0
                        ? Math.Clamp((double)value.DownloadedBytes / value.TotalBytes.Value * 100, 0, 100)
                        : null;
                    progress?.Report(new FFmpegProvisioningProgressDto
                    {
                        State = FFmpegProvisioningState.Downloading,
                        Message = percentage is null
                            ? "Đang tải FFmpeg..."
                            : $"Đang tải FFmpeg: {percentage:0}%",
                        DownloadedBytes = value.DownloadedBytes,
                        TotalBytes = value.TotalBytes,
                        Percentage = percentage
                    });
                });

                Report(progress, FFmpegProvisioningState.Downloading, "Đang tải FFmpeg...");
                _logger.LogInformation("Downloading managed FFmpeg version {Version}", _options.Version);
                await _packageDownloader
                    .DownloadAsync(_options.DownloadUrl, archivePath, downloadProgress, cancellationToken)
                    .ConfigureAwait(false);

                Report(progress, FFmpegProvisioningState.Verifying, "Đang xác minh SHA-256 của FFmpeg...");
                await _packageVerifier
                    .VerifySha256Async(archivePath, _options.Sha256, cancellationToken)
                    .ConfigureAwait(false);

                var extractionDirectory = Path.Combine(stagingDirectory, "extracted");
                Report(progress, FFmpegProvisioningState.Extracting, "Đang giải nén FFmpeg...");
                await _archiveExtractor
                    .ExtractAsync(
                        archivePath,
                        extractionDirectory,
                        _options.MaximumExtractedBytes,
                        cancellationToken)
                    .ConfigureAwait(false);

                var extractedRoot = GetContainedPath(extractionDirectory, _options.ArchiveRootDirectoryName);
                var stagedExecutable = GetContainedPath(extractedRoot, _options.FfmpegRelativePath);
                var stagedProbe = GetContainedPath(extractedRoot, _options.FfprobeRelativePath);
                if (!Directory.Exists(extractedRoot) || !File.Exists(stagedExecutable) || !File.Exists(stagedProbe))
                {
                    throw new AppException(
                        AppErrorCode.FfmpegExtractionError,
                        "Gói FFmpeg không chứa ffmpeg.exe và ffprobe.exe như dự kiến.");
                }

                Report(progress, FFmpegProvisioningState.Validating, "Đang kiểm tra FFmpeg...");
                var stagedResult = await _discoveryService
                    .ValidateCandidateAsync(stagedExecutable, FFmpegSource.Managed, cancellationToken)
                    .ConfigureAwait(false);
                if (stagedResult is null)
                {
                    throw new AppException(
                        AppErrorCode.FfmpegValidationError,
                        "FFmpeg tải về không thể khởi chạy hoặc không hợp lệ.");
                }

                var activation = Activate(extractedRoot);
                FFmpegProvisioningResultDto? installedResult;
                try
                {
                    var installedExecutable = GetContainedPath(
                        activation.Destination,
                        _options.FfmpegRelativePath);
                    installedResult = await _discoveryService
                        .ValidateCandidateAsync(installedExecutable, FFmpegSource.Managed, cancellationToken)
                        .ConfigureAwait(false);
                    if (installedResult is null)
                    {
                        throw new AppException(
                            AppErrorCode.FfmpegValidationError,
                            "Không thể xác thực FFmpeg sau khi cài đặt.");
                    }

                    activation.Commit();
                }
                catch
                {
                    activation.Rollback();
                    throw;
                }

                var result = installedResult with { WasDownloaded = true };
                await TryPersistResolvedPathAsync(result.ExecutablePath, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _cached, result);
                _logger.LogInformation("Managed FFmpeg {Version} is ready", result.Version);
                ReportReady(progress, result);
                return result;
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }
        catch (OperationCanceledException)
        {
            Report(progress, FFmpegProvisioningState.Cancelled, "Đã hủy chuẩn bị FFmpeg.");
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to provision FFmpeg");
            Report(progress, FFmpegProvisioningState.Failed, exception.Message);
            throw;
        }
        finally
        {
            if (entered)
            {
                _provisioningGate.Release();
            }
        }
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.Version) ||
            _options.Version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            _options.Version.Contains(Path.DirectorySeparatorChar) ||
            _options.Version.Contains(Path.AltDirectorySeparatorChar) ||
            _options.MaximumDownloadBytes <= 0 ||
            _options.MaximumExtractedBytes <= 0 ||
            _options.DownloadTimeoutMinutes is < 1 or > 120 ||
            _options.MaximumRetries is < 0 or > 5)
        {
            throw new AppException(
                AppErrorCode.FfmpegSourceUnavailable,
                "Cấu hình nguồn FFmpeg không hợp lệ.");
        }
    }

    private string CreateStagingDirectory()
    {
        var root = Path.GetFullPath(_fileService.TempDirectory);
        var path = Path.GetFullPath(Path.Combine(root, $"ffmpeg-install-{Guid.NewGuid():N}"));
        EnsureContained(root, path);
        Directory.CreateDirectory(path);
        return path;
    }

    private ActivationTransaction Activate(string extractedRoot)
    {
        var managedRoot = Path.GetFullPath(Path.Combine(_fileService.ToolsDirectory, "ffmpeg"));
        Directory.CreateDirectory(managedRoot);
        var destination = GetContainedPath(managedRoot, _options.Version);
        var backup = GetContainedPath(managedRoot, $"{_options.Version}.backup-{Guid.NewGuid():N}");
        var movedExisting = false;
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                movedExisting = true;
            }

            Directory.Move(extractedRoot, destination);
            return new ActivationTransaction(destination, movedExisting ? backup : null);
        }
        catch
        {
            if (!Directory.Exists(destination) && movedExisting && Directory.Exists(backup))
            {
                Directory.Move(backup, destination);
            }

            throw;
        }
    }

    private void EnsureInstallationDiskSpace(string stagingDirectory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(stagingDirectory));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new AppException(AppErrorCode.DiskFull, "Không xác định được ổ đĩa cài FFmpeg.");
        }

        var required = checked(_options.MaximumDownloadBytes + _options.MaximumExtractedBytes + (64L * 1024 * 1024));
        if (new DriveInfo(root).AvailableFreeSpace < required)
        {
            throw new AppException(
                AppErrorCode.DiskFull,
                "Ổ đĩa không đủ dung lượng để tải và giải nén FFmpeg.");
        }
    }

    private async Task TryPersistResolvedPathAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(
                    settings.FfmpegPath,
                    executablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            settings.FfmpegPath = executablePath;
            await _settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "FFmpeg is ready but its managed path could not be persisted");
        }
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        EnsureContained(fullRoot, candidate);
        return candidate;
    }

    private static void EnsureContained(string root, string candidate)
    {
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(
                AppErrorCode.FfmpegExtractionError,
                "Đường dẫn cài đặt FFmpeg không an toàn.");
        }
    }

    private static void Report(
        IProgress<FFmpegProvisioningProgressDto>? progress,
        FFmpegProvisioningState state,
        string message) => progress?.Report(
            new FFmpegProvisioningProgressDto
            {
                State = state,
                Message = message
            });

    private static void ReportReady(
        IProgress<FFmpegProvisioningProgressDto>? progress,
        FFmpegProvisioningResultDto result) => Report(
            progress,
            FFmpegProvisioningState.Ready,
            $"FFmpeg {result.Version} đã sẵn sàng ({result.Source}).");

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Stale installation directories are harmless and can be removed at the next startup.
        }
    }

    private void SettingsChanged(object? sender, BilibiliDownloader.Domain.Entities.AppSettings settings) =>
        Volatile.Write(ref _cached, null);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _settingsService.SettingsChanged -= SettingsChanged;
        _provisioningGate.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class ActivationTransaction
    {
        private readonly string? _backup;
        private bool _completed;

        public ActivationTransaction(string destination, string? backup)
        {
            Destination = destination;
            _backup = backup;
        }

        public string Destination { get; }

        public void Commit()
        {
            if (_completed)
            {
                return;
            }

            if (_backup is not null)
            {
                TryDeleteDirectory(_backup);
            }

            _completed = true;
        }

        public void Rollback()
        {
            if (_completed)
            {
                return;
            }

            TryDeleteDirectory(Destination);
            if (_backup is not null && Directory.Exists(_backup) && !Directory.Exists(Destination))
            {
                Directory.Move(_backup, Destination);
            }

            _completed = true;
        }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using SubVid.App.Core;

namespace SubVid.App.LocalAi;

public sealed class VieNeuRuntimeProvisioner : IDisposable
{
    internal const string RuntimeVersion = "vieneu-3.2.5-2026.08.15.1";
    private const long UvArchiveSize = 19_013_455;
    private const string UvArchiveSha256 = "b23350c79e8ad0192b8124af13a0f17e8d4e4549524785e1aef389ae5a06990e";
    private const long UvExecutableSize = 48_024_064;
    private const string UvExecutableSha256 = "68a22cbab1674647bcda32120b214e6480f875414e3333f49f87ae99b4b0e0fa";
    private static readonly Uri UvArchiveUri = new(
        "https://github.com/astral-sh/uv/releases/download/0.12.3/uv-x86_64-pc-windows-msvc.zip");
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Func<string, TimeSpan, bool> _runtimeProbe;
    private readonly object _probeSync = new();
    private bool _existingRuntimeProbed;
    private bool _existingRuntimeValid;

    public VieNeuRuntimeProvisioner(AppPaths paths)
        : this(paths, ProbeRequiredModules)
    {
    }

    internal VieNeuRuntimeProvisioner(
        AppPaths paths,
        Func<string, TimeSpan, bool> runtimeProbe)
    {
        _paths = paths;
        _runtimeProbe = runtimeProbe;
        _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("SubVid-App/1.0");
    }

    public string PythonPath => Path.Combine(
        _paths.VieNeuRuntimeDirectory,
        ".venv",
        "Scripts",
        "python.exe");

    internal string MarkerPath => Path.Combine(
        _paths.VieNeuRuntimeDirectory,
        ".subvid-vieneu-runtime-version");

    public bool HasExistingRuntime
    {
        get
        {
            try
            {
                return File.Exists(PythonPath)
                    || Directory.EnumerateFileSystemEntries(_paths.VieNeuRuntimeDirectory).Any();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public bool IsReady
    {
        get
        {
            try
            {
                if (!File.Exists(PythonPath))
                {
                    return false;
                }

                if (IsRuntimeVersionCurrent())
                {
                    return true;
                }

                if (!HasValidExistingRuntime())
                {
                    return false;
                }

                WriteRuntimeVersionMarker();
                VoiceInstallLog.Write(
                    _paths,
                    LocalVoiceEngines.VieNeu,
                    null,
                    "RUNTIME_ADOPTED",
                    "Runtime VieNeu cũ đã được xác thực và nhận lại mà không cài lại dependency.");
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                VoiceInstallLog.Write(
                    _paths,
                    LocalVoiceEngines.VieNeu,
                    null,
                    "RUNTIME_ADOPTION_FAILED",
                    "Runtime VieNeu hợp lệ nhưng không thể cập nhật marker phiên bản.",
                    exception);
                return false;
            }
        }
    }

    public async Task EnsureReadyAsync(
        IProgress<LocalRuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(PythonPath) && IsRuntimeVersionCurrent())
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            progress?.Report(new LocalRuntimeProgress(
                "VIENEU_CHECK",
                1,
                "Đang kiểm tra runtime VieNeu đã có trên máy."));
            if (IsRuntimeVersionCurrent() && File.Exists(PythonPath))
            {
                return;
            }

            if (HasValidExistingRuntime(forceProbe: true))
            {
                try
                {
                    WriteRuntimeVersionMarker();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new LocalModelException(
                        "VIENEU_MARKER_WRITE_FAILED",
                        "Runtime VieNeu đã hợp lệ nhưng ứng dụng không thể cập nhật trạng thái cài đặt trong thư mục AI.",
                        exception);
                }

                progress?.Report(new LocalRuntimeProgress(
                    "VIENEU_RUNTIME_ADOPTED",
                    100,
                    "Đã nhận lại runtime VieNeu hiện có, không cần cài lại."));
                VoiceInstallLog.Write(
                    _paths,
                    LocalVoiceEngines.VieNeu,
                    null,
                    "RUNTIME_ADOPTED",
                    "Runtime VieNeu cũ đã được xác thực và nhận lại mà không cài lại dependency.");
                return;
            }

            EnsureDiskSpace();
            Directory.CreateDirectory(_paths.VieNeuRuntimeDirectory);
            var uvPath = await EnsureUvAsync(progress, cancellationToken);

            if (!File.Exists(PythonPath))
            {
                progress?.Report(new LocalRuntimeProgress(
                    "VIENEU_PYTHON",
                    12,
                    $"Đang tạo runtime VieNeu tại {_paths.VieNeuRuntimeDirectory}."));
                await RunUvAsync(
                    uvPath,
                    ["venv", "--python", "3.11", "--managed-python", Path.Combine(_paths.VieNeuRuntimeDirectory, ".venv")],
                    cancellationToken);
            }
            else
            {
                progress?.Report(new LocalRuntimeProgress(
                    "VIENEU_REPAIR",
                    12,
                    "Đã tìm thấy runtime VieNeu nhưng dependency chưa hợp lệ; đang sửa cài đặt hiện có."));
            }

            progress?.Report(new LocalRuntimeProgress(
                "VIENEU_PACKAGES",
                35,
                "Đang cài VieNeu và dependency Perth."));
            await RunUvAsync(
                uvPath,
                [
                    "pip", "install",
                    "--python", PythonPath,
                    "--only-binary", ":all:",
                    "--no-binary", "perth",
                    "--exclude-newer", "2026-08-14",
                    "vieneu==3.2.5",
                ],
                cancellationToken);
            progress?.Report(new LocalRuntimeProgress(
                "VIENEU_VALIDATE",
                82,
                "Đang kiểm tra runtime VieNeu độc lập."));
            await ValidateAsync(cancellationToken);
            await WriteRuntimeVersionMarkerAsync(cancellationToken);
            lock (_probeSync)
            {
                _existingRuntimeProbed = true;
                _existingRuntimeValid = true;
            }
            progress?.Report(new LocalRuntimeProgress(
                "VIENEU_RUNTIME_READY",
                100,
                "Runtime VieNeu đã sẵn sàng."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LocalModelException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new LocalModelException(
                "VIENEU_RUNTIME_INSTALL_FAILED",
                "Không thể cài runtime VieNeu tại thư mục AI đã chọn.",
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> EnsureUvAsync(
        IProgress<LocalRuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var languageUv = Path.Combine(_paths.ToolsDirectory, "python", "uv.exe");
        if (IsVerifiedFile(languageUv, UvExecutableSize, UvExecutableSha256))
        {
            return languageUv;
        }

        var sharedRuntimeRoot = Path.Combine(_paths.AiRootDirectory, "Runtimes");
        Directory.CreateDirectory(sharedRuntimeRoot);
        var uvPath = Path.Combine(sharedRuntimeRoot, "uv.exe");
        if (IsVerifiedFile(uvPath, UvExecutableSize, UvExecutableSha256))
        {
            return uvPath;
        }

        progress?.Report(new LocalRuntimeProgress("VIENEU_UV", 2, "Đang tải trình quản lý runtime VieNeu."));
        var archivePath = Path.Combine(_paths.AiTempDirectory, "uv-vieneu.zip.partial");
        try
        {
            using var response = await _httpClient.GetAsync(
                UvArchiveUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                archivePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            var archive = new FileInfo(archivePath);
            if (archive.Length != UvArchiveSize
                || !IsVerifiedHash(archivePath, UvArchiveSha256))
            {
                throw new LocalModelException("VIENEU_RUNTIME_INSTALL_FAILED", "Checksum UV cho VieNeu không hợp lệ.");
            }

            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.Entries.SingleOrDefault(item =>
                string.Equals(Path.GetFileName(item.FullName), "uv.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new LocalModelException("VIENEU_RUNTIME_INSTALL_FAILED", "Gói UV cho VieNeu không hợp lệ.");
            var partialExecutable = uvPath + ".partial";
            await using (var entryStream = entry.Open())
            await using (var output = new FileStream(partialExecutable, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await entryStream.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            if (!IsVerifiedFile(partialExecutable, UvExecutableSize, UvExecutableSha256))
            {
                throw new LocalModelException("VIENEU_RUNTIME_INSTALL_FAILED", "UV VieNeu sau giải nén không hợp lệ.");
            }

            File.Move(partialExecutable, uvPath, overwrite: true);
            return uvPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LocalModelException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
        {
            throw new LocalModelException(
                "VIENEU_RUNTIME_INSTALL_FAILED",
                "Không thể tải UV cho runtime VieNeu. Hãy kiểm tra kết nối mạng.",
                exception);
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            var partialExecutable = uvPath + ".partial";
            if (File.Exists(partialExecutable)) File.Delete(partialExecutable);
        }
    }

    private async Task RunUvAsync(
        string uvPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = uvPath,
            WorkingDirectory = _paths.VieNeuRuntimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["UV_CACHE_DIR"] = Path.Combine(_paths.AiCacheDirectory, "UV");
        startInfo.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(_paths.ToolsDirectory, "python", "cpython");
        startInfo.Environment["UV_PYTHON_NO_REGISTRY"] = "1";
        startInfo.Environment["UV_NO_PROGRESS"] = "1";
        startInfo.Environment["TEMP"] = _paths.AiTempDirectory;
        startInfo.Environment["TMP"] = _paths.AiTempDirectory;
        using var process = Process.Start(startInfo)
            ?? throw new LocalModelException("VIENEU_RUNTIME_INSTALL_FAILED", "Không thể chạy trình cài VieNeu.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        _ = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new LocalModelException(
                "VIENEU_DEPENDENCY_FAILED",
                "Cài dependency VieNeu thất bại: " + LastLine(error));
        }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("import vieneu, perth, soundfile, onnxruntime");
        using var process = Process.Start(startInfo)
            ?? throw new LocalModelException("VIENEU_RUNTIME_INSTALL_FAILED", "Không thể kiểm tra runtime VieNeu.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new LocalModelException(
                "VIENEU_RUNTIME_INSTALL_FAILED",
                "Runtime VieNeu chưa hợp lệ: " + LastLine(error));
        }
    }

    private bool IsRuntimeVersionCurrent() => File.Exists(MarkerPath)
        && string.Equals(
            File.ReadAllText(MarkerPath).Trim(),
            RuntimeVersion,
            StringComparison.Ordinal);

    private bool HasValidExistingRuntime(bool forceProbe = false)
    {
        if (!File.Exists(PythonPath))
        {
            return false;
        }

        lock (_probeSync)
        {
            if (_existingRuntimeProbed && !forceProbe)
            {
                return _existingRuntimeValid;
            }

            _existingRuntimeValid = _runtimeProbe(PythonPath, TimeSpan.FromSeconds(15));
            _existingRuntimeProbed = true;
            return _existingRuntimeValid;
        }
    }

    private void WriteRuntimeVersionMarker()
    {
        Directory.CreateDirectory(_paths.VieNeuRuntimeDirectory);
        var temporaryPath = MarkerPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, RuntimeVersion);
            File.Move(temporaryPath, MarkerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task WriteRuntimeVersionMarkerAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.VieNeuRuntimeDirectory);
        var temporaryPath = MarkerPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, RuntimeVersion, cancellationToken);
            File.Move(temporaryPath, MarkerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool ProbeRequiredModules(string pythonPath, TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import vieneu, perth, soundfile, onnxruntime");
            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue)))
            {
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private void EnsureDiskSpace()
    {
        var root = Path.GetPathRoot(_paths.AiRootDirectory);
        if (root is null || new DriveInfo(root).AvailableFreeSpace >= AiStorageService.MinimumFreeBytes) return;
        throw new LocalModelException(
            "AI_STORAGE_SPACE_INSUFFICIENT",
            $"Thư mục AI {_paths.AiRootDirectory} cần tối thiểu 6 GB trống để cài VieNeu.");
    }

    private static string LastLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() is { } line
            ? line[..Math.Min(line.Length, 300)]
            : "không có thông tin lỗi.";

    private static bool IsVerifiedFile(string path, long expectedSize, string expectedHash)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length == expectedSize && IsVerifiedHash(path, expectedHash);
    }

    private static bool IsVerifiedHash(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(stream),
            Convert.FromHexString(expectedHash));
    }

    public void Dispose()
    {
        _gate.Dispose();
        _httpClient.Dispose();
    }
}

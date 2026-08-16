using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.LocalAi;

public sealed record LocalRuntimeProgress(string Phase, double Percent, string Message);

public sealed class LocalAiRuntimeProvisioner : IDisposable
{
    private const string RuntimeVersion = "2026.08.15.2";
    private const string UvVersion = "0.12.3";
    private const long UvArchiveSize = 19_013_455;
    private const string UvArchiveSha256 = "b23350c79e8ad0192b8124af13a0f17e8d4e4549524785e1aef389ae5a06990e";
    private const long UvExecutableSize = 48_024_064;
    private const string UvExecutableSha256 = "68a22cbab1674647bcda32120b214e6480f875414e3333f49f87ae99b4b0e0fa";
    private static readonly Uri UvArchiveUri = new(
        "https://github.com/astral-sh/uv/releases/download/0.12.3/uv-x86_64-pc-windows-msvc.zip");
    private readonly AppPaths _paths;
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalAiRuntimeProvisioner(AppPaths paths)
    {
        _paths = paths;
        _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("TOOL-VIETSUB-APP/1.0");
    }

    public string PythonPath => Path.Combine(
        _paths.ToolsDirectory,
        "python",
        ".venv",
        "Scripts",
        "python.exe");

    private string RuntimeVersionMarkerPath => Path.Combine(
        _paths.ToolsDirectory,
        "python",
        ".tool-vietsub-runtime-version");

    public bool IsReady => File.Exists(PythonPath)
        && File.Exists(Path.Combine(Path.GetDirectoryName(PythonPath)!, "argos-translate.exe"))
        && File.Exists(Path.Combine(Path.GetDirectoryName(PythonPath)!, "piper.exe"))
        && (IsRuntimeVersionCurrent() || HasRequiredModules());

    public async Task EnsureReadyAsync(
        IProgress<LocalRuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsReady) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady) return;
            EnsureDiskSpace();
            var runtimeRoot = Path.Combine(_paths.ToolsDirectory, "python");
            Directory.CreateDirectory(runtimeRoot);
            var uvPath = Path.Combine(runtimeRoot, "uv.exe");
            if (!IsVerifiedFile(uvPath, UvExecutableSize, UvExecutableSha256))
            {
                await InstallUvAsync(runtimeRoot, uvPath, progress, cancellationToken);
            }

            progress?.Report(new LocalRuntimeProgress("PYTHON", 45, "Đang cài Python 3.11 local."));
            await RunUvAsync(
                uvPath,
                ["venv", "--python", "3.11", "--managed-python", Path.GetDirectoryName(Path.GetDirectoryName(PythonPath)!)!],
                runtimeRoot,
                cancellationToken);
            progress?.Report(new LocalRuntimeProgress("PACKAGES", 65, "Đang cài bộ dịch và giọng đọc local."));
            await RunUvAsync(
                uvPath,
                [
                    "pip", "install",
                    "--python", PythonPath,
                    "--only-binary", ":all:",
                    "--exclude-newer", "2026-08-14",
                    "argostranslate==1.11.0",
                    "ctranslate2==4.8.1",
                    "sentencepiece==0.2.2",
                    "piper-tts==1.6.0",
                    "torch==2.13.0",
                    "transformers==4.57.6",
                ],
                runtimeRoot,
                cancellationToken);
            await ValidateAsync(cancellationToken);
            await File.WriteAllTextAsync(RuntimeVersionMarkerPath, RuntimeVersion, cancellationToken);
            progress?.Report(new LocalRuntimeProgress("READY", 100, "Runtime AI local đã sẵn sàng."));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InstallUvAsync(
        string runtimeRoot,
        string uvPath,
        IProgress<LocalRuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(runtimeRoot, $"uv-{UvVersion}.zip");
        var partialPath = archivePath + ".partial";
        try
        {
            using var response = await _httpClient.GetAsync(
                UvArchiveUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
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
                progress?.Report(new LocalRuntimeProgress(
                    "UV",
                    Math.Min(40, processed * 40d / UvArchiveSize),
                    "Đang tải trình quản lý runtime đã ký checksum."));
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (processed != UvArchiveSize
                || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(UvArchiveSha256)))
            {
                throw new LocalModelException("RUNTIME_HASH_INVALID", "Checksum runtime local không hợp lệ.");
            }

            destination.Close();
            File.Move(partialPath, archivePath, overwrite: true);
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.Entries.SingleOrDefault(item =>
                string.Equals(Path.GetFileName(item.FullName), "uv.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new LocalModelException("RUNTIME_ARCHIVE_INVALID", "Gói runtime local thiếu uv.exe.");
            await using var entryStream = entry.Open();
            await using var output = new FileStream(uvPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await entryStream.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Close();
            if (!IsVerifiedFile(uvPath, UvExecutableSize, UvExecutableSha256))
            {
                throw new LocalModelException("RUNTIME_HASH_INVALID", "Checksum uv.exe không hợp lệ.");
            }
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
                "RUNTIME_INSTALL_FAILED",
                "Không thể cài runtime AI local. Hãy kiểm tra mạng và dung lượng đĩa.",
                exception);
        }
        finally
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    private async Task RunUvAsync(
        string uvPath,
        IReadOnlyList<string> arguments,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = uvPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["UV_CACHE_DIR"] = Path.Combine(runtimeRoot, "cache");
        startInfo.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(runtimeRoot, "cpython");
        startInfo.Environment["UV_PYTHON_NO_REGISTRY"] = "1";
        startInfo.Environment["UV_NO_PROGRESS"] = "1";
        using var process = Process.Start(startInfo)
            ?? throw new LocalModelException("RUNTIME_START_FAILED", "Không thể khởi động trình cài runtime local.");
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
                "RUNTIME_INSTALL_FAILED",
                "Cài runtime AI local thất bại: " + LastLine(error));
        }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("import argostranslate, ctranslate2, sentencepiece, piper, torch, transformers");
        using var process = Process.Start(startInfo)
            ?? throw new LocalModelException("RUNTIME_VALIDATION_FAILED", "Không thể kiểm tra runtime AI local.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new LocalModelException("RUNTIME_VALIDATION_FAILED", "Runtime AI local chưa cài đủ thư viện.");
        }
    }

    private bool IsRuntimeVersionCurrent()
    {
        try
        {
            return File.Exists(RuntimeVersionMarkerPath)
                && string.Equals(
                    File.ReadAllText(RuntimeVersionMarkerPath).Trim(),
                    RuntimeVersion,
                    StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool HasRequiredModules()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = PythonPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import argostranslate, ctranslate2, sentencepiece, piper, torch, transformers");
            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(15_000))
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
        var root = Path.GetPathRoot(_paths.ToolsDirectory);
        if (root is null) return;
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
        {
            throw new LocalModelException("RUNTIME_DISK_SPACE_INSUFFICIENT", "Cần tối thiểu 2 GB trống để cài runtime dịch local.");
        }
    }

    private static string LastLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() is { } line
            ? line[..Math.Min(300, line.Length)]
            : "không có thông tin lỗi.";

    private static bool IsVerifiedFile(string path, long expectedSize, string expectedHash)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expectedSize) return false;
        using var stream = file.OpenRead();
        var actual = SHA256.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash));
    }

    public void Dispose()
    {
        _gate.Dispose();
        _httpClient.Dispose();
    }
}

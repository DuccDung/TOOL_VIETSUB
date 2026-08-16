using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SubVid.App.Core;

namespace SubVid.App.LocalAi;

public sealed class LocalWorkerProcess
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TResponse> RunAsync<TResponse>(
        string pythonPath,
        string scriptPath,
        object request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        string? requestDirectory = null)
    {
        if (!File.Exists(pythonPath))
        {
            throw new LocalModelException(
                "LOCAL_PYTHON_MISSING",
                "Chưa cài runtime local cho dịch và tạo giọng. Hãy cài runtime AI local rồi thử lại.");
        }

        if (!File.Exists(scriptPath))
        {
            throw new LocalModelException("LOCAL_WORKER_MISSING", "Thiếu worker AI local trong bộ cài App.");
        }

        var resolvedRequestDirectory = string.IsNullOrWhiteSpace(requestDirectory)
            ? Path.Combine(Path.GetTempPath(), "SUBVID_WORKERS")
            : Path.GetFullPath(requestDirectory);
        Directory.CreateDirectory(resolvedRequestDirectory);
        var requestPath = Path.Combine(resolvedRequestDirectory, $"{Guid.NewGuid():N}.request.json");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(request, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);
        try
        {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add("utf8");
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(requestPath);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new LocalModelException("LOCAL_WORKER_START_FAILED", "Không thể khởi động worker AI local.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(timeoutCancellation.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeoutCancellation.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
            var output = await standardOutput;
            var error = await standardError;
            if (process.ExitCode != 0)
            {
                throw new LocalModelException(
                    "LOCAL_WORKER_FAILED",
                    SanitizeError(error));
            }

            try
            {
                return JsonSerializer.Deserialize<TResponse>(output, JsonOptions)
                    ?? throw new JsonException("Worker returned null.");
            }
            catch (JsonException exception)
            {
                throw new LocalModelException(
                    "LOCAL_WORKER_RESPONSE_INVALID",
                    "Worker AI local trả về dữ liệu không hợp lệ.",
                    exception);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new LocalModelException(
                "LOCAL_WORKER_TIMEOUT",
                "Worker AI local chạy quá thời gian cho phép.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        }
        finally
        {
            if (File.Exists(requestPath))
            {
                File.Delete(requestPath);
            }
        }
    }

    private static string SanitizeError(string error)
    {
        var line = (error ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return string.IsNullOrWhiteSpace(line)
            ? "Worker AI local xử lý thất bại. Xem nhật ký job để biết chi tiết."
            : $"Worker AI local xử lý thất bại: {line[..Math.Min(line.Length, 300)]}";
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
    }
}

public sealed class LocalWorkerRuntimeLocator(AppPaths paths)
{
    public string RequirePython()
    {
        var configured = Environment.GetEnvironmentVariable("SUBVID_PYTHON_PATH");
        var candidates = new[]
        {
            configured,
            Path.Combine(paths.ToolsDirectory, "python", ".venv", "Scripts", "python.exe"),
            Path.Combine(paths.ToolsDirectory, "python", "python.exe"),
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new LocalModelException(
                "LOCAL_PYTHON_MISSING",
                "Chưa cài runtime Python local cho bộ dịch và giọng đọc.");
    }

    public string RequireVieNeuPython()
    {
        var configured = Environment.GetEnvironmentVariable("SUBVID_VIENEU_PYTHON_PATH");
        var candidates = new[]
        {
            configured,
            Path.Combine(paths.VieNeuRuntimeDirectory, ".venv", "Scripts", "python.exe"),
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new LocalModelException(
                "VIENEU_RUNTIME_MISSING",
                "Chưa cài runtime VieNeu tại thư mục AI đã chọn.");
    }

    public string RequireWorker(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "workers", fileName));
        return File.Exists(path)
            ? path
            : throw new LocalModelException("LOCAL_WORKER_MISSING", $"Thiếu worker {fileName} trong bộ cài App.");
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SubVid.App.Jobs;

namespace SubVid.App.Media;

public interface IFfmpegProgressRunner
{
    Task RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        double durationSeconds,
        Func<double, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        string? workingDirectory = null);
}

public sealed class FfmpegProgressRunner : IFfmpegProgressRunner
{
    public const int WindowsCommandLineLimit = 32_767;
    public const int SafeWindowsCommandLineLimit = 30_000;

    public async Task RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        double durationSeconds,
        Func<double, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            throw new LocalJobException(
                "FFMPEG_NOT_FOUND",
                "Không tìm thấy FFmpeg. Hãy kiểm tra lại cấu hình công cụ media.",
                retryable: false);
        }

        var resolvedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Path.GetFullPath(workingDirectory);
        if (resolvedWorkingDirectory is not null && !Directory.Exists(resolvedWorkingDirectory))
        {
            throw new LocalJobException(
                "FFMPEG_WORKING_DIRECTORY_INVALID",
                "Thư mục làm việc của FFmpeg không tồn tại.",
                retryable: false);
        }

        var effectiveArguments = new List<string>(arguments.Count + 3)
        {
            "-progress",
            "pipe:1",
            "-nostats",
        };
        effectiveArguments.AddRange(arguments);
        EnsureCommandLineLength(ffmpegPath, effectiveArguments);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            WorkingDirectory = resolvedWorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in effectiveArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new LocalJobException("FFMPEG_START_FAILED", "Không thể khởi động FFmpeg.");
            }
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Priority is a responsiveness hint; FFmpeg can continue when the
                // host policy does not allow changing it.
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 206)
        {
            throw new LocalJobException(
                "FFMPEG_COMMAND_TOO_LONG",
                "Dự án có quá nhiều tệp media để xử lý trong một lệnh FFmpeg.",
                retryable: false);
        }
        catch (Win32Exception)
        {
            throw new LocalJobException(
                "FFMPEG_START_FAILED",
                "Windows không thể khởi động FFmpeg. Hãy kiểm tra quyền truy cập và cấu hình công cụ media.");
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
                    && durationSeconds > 0)
                {
                    var percent = Math.Clamp(microseconds / 1_000_000d / durationSeconds * 100d, 0, 99.5);
                    await reportProgress(percent);
                }
                else if (line == "progress=end")
                {
                    await reportProgress(100);
                }
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (InvalidOperationException)
            {
                // Process exited while cancellation was being handled.
            }

            throw;
        }

        var standardError = await errorTask;
        if (process.ExitCode != 0)
        {
#if DEBUG
            Debug.WriteLine(standardError);
#endif
            throw new LocalJobException(
                "FFMPEG_PROCESS_FAILED",
                "FFmpeg không thể xử lý video: " + GetLastErrorLine(standardError));
        }
    }

    public static int EstimateWindowsCommandLineLength(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var length = GetEscapedWindowsArgumentLength(executable);
        foreach (var argument in arguments)
        {
            length += 1 + GetEscapedWindowsArgumentLength(argument);
        }

        return length;
    }

    private static void EnsureCommandLineLength(
        string executable,
        IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows()) return;

        var estimatedLength = EstimateWindowsCommandLineLength(executable, arguments);
        if (estimatedLength < SafeWindowsCommandLineLimit) return;

        throw new LocalJobException(
            "FFMPEG_COMMAND_TOO_LONG",
            "Dự án có quá nhiều tệp media để xử lý trong một lệnh FFmpeg.",
            retryable: false);
    }

    private static int GetEscapedWindowsArgumentLength(string argument)
    {
        if (argument.Length > 0
            && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument.Length;
        }

        var length = 2;
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                length += (backslashCount * 2) + 2;
                backslashCount = 0;
                continue;
            }

            length += backslashCount + 1;
            backslashCount = 0;
        }

        return length + (backslashCount * 2);
    }

    private static string GetLastErrorLine(string error)
    {
        var line = (error ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            return "không có thông tin lỗi từ tiến trình.";
        }

        var sanitized = new string(line.Where(character => !char.IsControl(character)).ToArray());
        return sanitized[..Math.Min(sanitized.Length, 500)];
    }
}

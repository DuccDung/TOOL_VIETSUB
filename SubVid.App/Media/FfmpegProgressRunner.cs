using System.Diagnostics;
using System.Globalization;
using SubVid.App.Jobs;

namespace SubVid.App.Media;

public sealed class FfmpegProgressRunner
{
    public async Task RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        double durationSeconds,
        Func<double, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-nostats");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new LocalJobException("FFMPEG_START_FAILED", "Không thể khởi động FFmpeg.");
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

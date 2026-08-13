using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Media;

public sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public async Task<ExternalProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Không thể khởi động công cụ media.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the checks.
            }

            throw;
        }

        return new ExternalProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }
}

public interface IMediaInspector
{
    Task<MediaMetadata> InspectAsync(string filePath, CancellationToken cancellationToken);
}

public sealed class MediaInspectionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class FfprobeMediaInspector : IMediaInspector
{
    private readonly IExternalProcessRunner _processRunner;
    private readonly string _ffprobePath;

    public FfprobeMediaInspector(AppPaths paths, IExternalProcessRunner? processRunner = null)
    {
        _processRunner = processRunner ?? new ExternalProcessRunner();
        _ffprobePath = MediaToolLocator.Locate(paths, "ffprobe", "TOOL_VIETSUB_FFPROBE_PATH");
    }

    public async Task<MediaMetadata> InspectAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new MediaInspectionException("MEDIA_FILE_NOT_FOUND", "Không tìm thấy video nguồn.");
        }

        var result = await _processRunner.RunAsync(
            _ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", filePath],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new MediaInspectionException(
                "MEDIA_PROBE_FAILED",
                "Video bị hỏng hoặc định dạng không được FFprobe hỗ trợ.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return Parse(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new MediaInspectionException(
                "MEDIA_METADATA_INVALID",
                $"FFprobe trả về metadata không hợp lệ: {exception.Message}");
        }
    }

    internal static MediaMetadata Parse(JsonElement root)
    {
        var streams = root.TryGetProperty("streams", out var streamsElement)
            && streamsElement.ValueKind == JsonValueKind.Array
            ? streamsElement.EnumerateArray().ToArray()
            : [];
        var video = streams.FirstOrDefault(item => GetString(item, "codec_type") == "video");
        var audioStreams = streams.Where(item => GetString(item, "codec_type") == "audio").ToArray();
        var audio = audioStreams.FirstOrDefault();
        var format = root.TryGetProperty("format", out var formatElement)
            ? formatElement
            : default;
        var duration = GetDouble(format, "duration")
            ?? streams.Select(item => GetDouble(item, "duration") ?? 0).DefaultIfEmpty().Max();
        var averageFps = ParseRatio(GetString(video, "avg_frame_rate"));
        var realFps = ParseRatio(GetString(video, "r_frame_rate"));
        var rotation = 0;
        if (video.ValueKind == JsonValueKind.Object
            && video.TryGetProperty("tags", out var tags)
            && int.TryParse(GetString(tags, "rotate"), out var tagRotation))
        {
            rotation = tagRotation;
        }

        if (video.ValueKind == JsonValueKind.Object
            && video.TryGetProperty("side_data_list", out var sideData)
            && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotationElement)
                    && rotationElement.TryGetInt32(out var sideRotation))
                {
                    rotation = sideRotation;
                }
            }
        }

        return new MediaMetadata
        {
            DurationSeconds = Math.Max(0, duration),
            Width = GetInt(video, "width") ?? 0,
            Height = GetInt(video, "height") ?? 0,
            FramesPerSecond = averageFps,
            VideoCodec = GetString(video, "codec_name"),
            AudioCodec = GetString(audio, "codec_name"),
            AudioTrackCount = audioStreams.Length,
            AudioSampleRate = GetInt(audio, "sample_rate"),
            AudioChannels = GetInt(audio, "channels"),
            BitRate = GetLong(format, "bit_rate"),
            RotationDegrees = rotation,
            HasVideo = video.ValueKind == JsonValueKind.Object,
            HasAudio = audioStreams.Length > 0,
            IsVariableFrameRate = averageFps > 0 && realFps > 0 && Math.Abs(averageFps - realFps) > 0.01,
            Container = GetString(format, "format_name") ?? string.Empty,
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static int? GetInt(JsonElement element, string propertyName) =>
        int.TryParse(GetString(element, propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? GetLong(JsonElement element, string propertyName) =>
        long.TryParse(GetString(element, propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double? GetDouble(JsonElement element, string propertyName) =>
        double.TryParse(GetString(element, propertyName), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double ParseRatio(string? ratio)
    {
        if (string.IsNullOrWhiteSpace(ratio))
        {
            return 0;
        }

        var parts = ratio.Split('/', 2);
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator))
        {
            return 0;
        }

        if (parts.Length == 1)
        {
            return numerator;
        }

        return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0
            ? numerator / denominator
            : 0;
    }
}

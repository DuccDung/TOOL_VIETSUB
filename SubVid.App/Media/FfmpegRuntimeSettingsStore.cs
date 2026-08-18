using System.Text.Json;
using SubVid.App.Core;

namespace SubVid.App.Media;

public sealed record FfmpegRuntimeSettings(
    string FfmpegPath,
    string FfprobePath,
    DateTime UpdatedAtUtc);

public sealed class FfmpegRuntimeSettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private string SettingsPath => Path.Combine(paths.RootDirectory, "ffmpeg.settings.json");

    public FfmpegRuntimeSettings? TryLoad()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var settings = JsonSerializer.Deserialize<FfmpegRuntimeSettings>(
                File.ReadAllText(SettingsPath),
                JsonOptions);
            return settings is not null
                && File.Exists(settings.FfmpegPath)
                && File.Exists(settings.FfprobePath)
                    ? settings
                    : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(string ffmpegPath, string ffprobePath)
    {
        var resolvedFfmpeg = Path.GetFullPath(ffmpegPath);
        var resolvedFfprobe = Path.GetFullPath(ffprobePath);
        if (!File.Exists(resolvedFfmpeg) || !File.Exists(resolvedFfprobe))
        {
            throw new FfmpegRuntimeException(
                "FFMPEG_SELECTION_INVALID",
                "Thư mục đã chọn phải chứa cả ffmpeg.exe và ffprobe.exe.");
        }

        var temporaryPath = SettingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(paths.RootDirectory);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new FfmpegRuntimeSettings(resolvedFfmpeg, resolvedFfprobe, DateTime.UtcNow),
                    JsonOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FfmpegRuntimeException(
                "FFMPEG_SETTINGS_SAVE_FAILED",
                "Không thể lưu vị trí FFmpeg đã chọn.",
                exception);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FfmpegRuntimeException(
                "FFMPEG_SETTINGS_SAVE_FAILED",
                "Không thể bỏ vị trí FFmpeg tùy chọn.",
                exception);
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
            // A stale temporary settings file is harmless and can be overwritten later.
        }
    }
}

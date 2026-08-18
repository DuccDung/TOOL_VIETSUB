using SubVid.App.Core;

namespace SubVid.App.Media;

public static class MediaToolLocator
{
    public static bool TryLocate(
        AppPaths paths,
        string executableName,
        string environmentVariable,
        out string path)
    {
        var platformName = OperatingSystem.IsWindows()
            ? executableName + ".exe"
            : executableName;
        var configuredPath = Environment.GetEnvironmentVariable(environmentVariable);
        var manualSettings = new FfmpegRuntimeSettingsStore(paths).TryLoad();
        var manualPath = string.Equals(executableName, "ffmpeg", StringComparison.OrdinalIgnoreCase)
            ? manualSettings?.FfmpegPath
            : string.Equals(executableName, "ffprobe", StringComparison.OrdinalIgnoreCase)
                ? manualSettings?.FfprobePath
                : null;
        var managedRoot = Path.Combine(paths.RootDirectory, "Tools", "ffmpeg");
        var candidates = new[]
        {
            configuredPath,
            manualPath,
            Path.Combine(managedRoot, platformName),
            Path.Combine(paths.ToolsDirectory, "ffmpeg", platformName),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", platformName),
        };
        var existing = candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
        if (existing is not null)
        {
            path = Path.GetFullPath(existing);
            return true;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), platformName);
            if (!File.Exists(candidate)) continue;
            path = Path.GetFullPath(candidate);
            return true;
        }

        path = string.Empty;
        return false;
    }

    public static string Locate(
        AppPaths paths,
        string executableName,
        string environmentVariable)
    {
        if (TryLocate(paths, executableName, environmentVariable, out var path))
        {
            return path;
        }

        throw new MediaInspectionException(
            $"{executableName.ToUpperInvariant()}_NOT_FOUND",
            $"Không tìm thấy {executableName}. Hãy cài bộ Công cụ video trong App hoặc cấu hình {environmentVariable}.");
    }
}

using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Media;

public static class MediaToolLocator
{
    public static string Locate(
        AppPaths paths,
        string executableName,
        string environmentVariable)
    {
        var platformName = OperatingSystem.IsWindows()
            ? executableName + ".exe"
            : executableName;
        var configuredPath = Environment.GetEnvironmentVariable(environmentVariable);
        var candidates = new[]
        {
            configuredPath,
            Path.Combine(paths.ToolsDirectory, "ffmpeg", platformName),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", platformName),
        };
        var existing = candidates.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (existing is not null)
        {
            return Path.GetFullPath(existing);
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), platformName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new MediaInspectionException(
            $"{executableName.ToUpperInvariant()}_NOT_FOUND",
            $"Không tìm thấy {executableName}. Hãy cấu hình {environmentVariable} hoặc cài bộ FFmpeg.");
    }
}

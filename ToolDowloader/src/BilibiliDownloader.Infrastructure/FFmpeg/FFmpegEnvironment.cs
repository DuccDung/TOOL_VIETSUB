namespace BilibiliDownloader.Infrastructure.FFmpeg;

public interface IFFmpegEnvironment
{
    string ApplicationBaseDirectory { get; }
    IReadOnlyList<string> GetPathDirectories();
}

public sealed class FFmpegEnvironment : IFFmpegEnvironment
{
    public string ApplicationBaseDirectory => AppContext.BaseDirectory;

    public IReadOnlyList<string> GetPathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(path => path.Trim('"'))
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

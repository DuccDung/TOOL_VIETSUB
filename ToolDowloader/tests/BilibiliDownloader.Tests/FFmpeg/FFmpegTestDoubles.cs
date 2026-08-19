using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Infrastructure.FFmpeg;

namespace BilibiliDownloader.Tests.FFmpeg;

internal sealed class TestFileService(string root) : IFileService
{
    public string DataDirectory => Ensure("Data");
    public string LogsDirectory => Ensure("Logs");
    public string TempDirectory => Ensure("Temp");
    public string ToolsDirectory => Ensure("Tools");
    public string DefaultDownloadDirectory => Ensure("Downloads");

    public string SanitizeFileName(string? fileName, int maximumLength = 140) =>
        string.IsNullOrWhiteSpace(fileName) ? "video" : fileName[..Math.Min(fileName.Length, maximumLength)];

    public string CreateUniqueOutputPath(string outputDirectory, string title, string extension) =>
        Path.Combine(outputDirectory, $"{SanitizeFileName(title)}.{extension.TrimStart('.')}");

    public string CreateJobTempDirectory(Guid jobId)
    {
        var path = Path.Combine(TempDirectory, $"job_{jobId:N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public Task CleanupJobTempDirectoryAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(TempDirectory, $"job_{jobId:N}");
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Task.CompletedTask;
    }

    public Task CleanupStaleTempDirectoriesAsync(
        TimeSpan maximumAge,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void OpenFile(string filePath) => throw new NotSupportedException();
    public void OpenFolder(string path) => throw new NotSupportedException();

    private string Ensure(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}

internal sealed class TestSettingsService(AppSettings settings) : ISettingsService
{
    private AppSettings _settings = settings;

    public event EventHandler<AppSettings>? SettingsChanged;

    public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_settings);
    }

    public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings = value;
        SettingsChanged?.Invoke(this, value);
        return Task.CompletedTask;
    }
}

internal sealed class TestFFmpegEnvironment(
    string applicationBaseDirectory,
    IReadOnlyList<string>? pathDirectories = null) : IFFmpegEnvironment
{
    public string ApplicationBaseDirectory { get; } = applicationBaseDirectory;
    public IReadOnlyList<string> GetPathDirectories() => pathDirectories ?? [];
}

internal sealed class TestFFmpegRunner(
    Func<string, IReadOnlyList<string>, CancellationToken, Task<FFmpegRunResult>>? callback = null)
    : IFFmpegProcessRunner
{
    public int Calls { get; private set; }

    public Task<FFmpegRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Calls++;
        return callback?.Invoke(executablePath, arguments, cancellationToken) ??
            Task.FromResult(new FFmpegRunResult(0, "ffmpeg version 9.0.1-test", string.Empty));
    }
}

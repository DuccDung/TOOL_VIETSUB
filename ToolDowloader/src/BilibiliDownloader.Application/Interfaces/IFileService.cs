namespace BilibiliDownloader.Application.Interfaces;

public interface IFileService
{
    string DataDirectory { get; }
    string LogsDirectory { get; }
    string TempDirectory { get; }
    string ToolsDirectory { get; }
    string DefaultDownloadDirectory { get; }

    string SanitizeFileName(string? fileName, int maximumLength = 140);
    string CreateUniqueOutputPath(string outputDirectory, string title, string extension);
    string CreateJobTempDirectory(Guid jobId);
    Task CleanupJobTempDirectoryAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task CleanupStaleTempDirectoriesAsync(TimeSpan maximumAge, CancellationToken cancellationToken = default);
    void OpenFile(string filePath);
    void OpenFolder(string path);
}

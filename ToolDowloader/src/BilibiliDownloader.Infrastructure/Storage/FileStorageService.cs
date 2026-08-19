using System.Diagnostics;
using System.Globalization;
using System.Text;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BilibiliDownloader.Infrastructure.Storage;

public sealed class FileStorageService(ILogger<FileStorageService> logger) : IFileService
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();
    private readonly string _applicationDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BilibiliDownloader");

    public string DataDirectory => EnsureDirectory(Path.Combine(_applicationDirectory, "Data"));
    public string LogsDirectory => EnsureDirectory(Path.Combine(_applicationDirectory, "Logs"));
    public string TempDirectory => EnsureDirectory(Path.Combine(_applicationDirectory, "Temp"));
    public string ToolsDirectory => EnsureDirectory(Path.Combine(_applicationDirectory, "Tools"));
    public string DefaultDownloadDirectory => EnsureDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "Bilibili Downloader"));

    public string SanitizeFileName(string? fileName, int maximumLength = 140)
    {
        var input = string.IsNullOrWhiteSpace(fileName) ? "video" : fileName.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(input.Length);
        var lastWasReplacement = false;
        foreach (var character in input)
        {
            var invalid = char.IsControl(character) || InvalidFileNameCharacters.Contains(character);
            if (invalid)
            {
                if (!lastWasReplacement)
                {
                    builder.Append('_');
                }

                lastWasReplacement = true;
                continue;
            }

            builder.Append(character);
            lastWasReplacement = false;
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "video";
        }

        if (sanitized.Length > maximumLength)
        {
            sanitized = sanitized[..maximumLength].TrimEnd('.', ' ');
        }

        if (ReservedNames.Contains(sanitized))
        {
            sanitized = $"_{sanitized}";
        }

        return sanitized;
    }

    public string CreateUniqueOutputPath(string outputDirectory, string title, string extension)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new AppException(AppErrorCode.DownloadError, "Thư mục output không hợp lệ.");
        }

        extension = extension.TrimStart('.').ToLowerInvariant();
        if (!string.Equals(extension, "mp4", StringComparison.Ordinal))
        {
            throw new AppException(AppErrorCode.DownloadError, "Chỉ hỗ trợ định dạng MP4.");
        }

        var root = EnsureDirectory(Path.GetFullPath(outputDirectory));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var safeName = SanitizeFileName(title);

        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var name = suffix == 0 ? safeName : $"{safeName} ({suffix.ToString(CultureInfo.InvariantCulture)})";
            var candidate = Path.GetFullPath(Path.Combine(root, $"{name}.{extension}"));
            if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(AppErrorCode.DownloadError, "Output path nằm ngoài thư mục được phép.");
            }

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new AppException(AppErrorCode.DownloadError, "Không thể tạo tên file output duy nhất.");
    }

    public string CreateJobTempDirectory(Guid jobId)
    {
        var root = Path.GetFullPath(TempDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(TempDirectory, $"job_{jobId:N}"));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(AppErrorCode.DownloadError, "Temporary path không hợp lệ.");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    public Task CleanupJobTempDirectoryAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(TempDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(TempDirectory, $"job_{jobId:N}"));
        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            logger.LogDebug("Cleaned temporary directory for job {JobId}", jobId);
        }

        return Task.CompletedTask;
    }

    public Task CleanupStaleTempDirectoriesAsync(TimeSpan maximumAge, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - maximumAge;
        var temporaryDirectories = new DirectoryInfo(TempDirectory)
            .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
            .Where(directory =>
                directory.Name.StartsWith("job_", StringComparison.OrdinalIgnoreCase) ||
                directory.Name.StartsWith("ffmpeg-install-", StringComparison.OrdinalIgnoreCase));
        foreach (var directory in temporaryDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.LastWriteTimeUtc >= cutoff)
            {
                continue;
            }

            try
            {
                directory.Delete(recursive: true);
                logger.LogInformation("Cleaned stale temporary directory {DirectoryName}", directory.Name);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Unable to clean stale temporary directory {DirectoryName}", directory.Name);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Unable to clean stale temporary directory {DirectoryName}", directory.Name);
            }
        }

        return Task.CompletedTask;
    }

    public void OpenFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new AppException(AppErrorCode.DownloadError, "File không còn tồn tại.");
        }

        Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
        if (directory is null || !Directory.Exists(directory))
        {
            throw new AppException(AppErrorCode.DownloadError, "Thư mục không còn tồn tại.");
        }

        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

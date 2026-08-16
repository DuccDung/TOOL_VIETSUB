using System.Security.Cryptography;
using SubVid.App.Core;

namespace SubVid.App.Media;

public enum MediaImportMode
{
    Link,
    Copy,
}

public sealed record MediaImportProgress(
    long BytesProcessed,
    long TotalBytes,
    double Percent,
    double MegabytesPerSecond);

public sealed class MediaImportService
{
    private static readonly HashSet<string> SupportedExtensions =
        new([".mp4", ".mkv", ".mov", ".webm"], StringComparer.OrdinalIgnoreCase);
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _projects;
    private readonly IMediaInspector _inspector;
    private readonly long _maximumFileSizeBytes;

    public MediaImportService(
        AppPaths paths,
        ProjectWorkspaceService projects,
        IMediaInspector inspector,
        long maximumFileSizeBytes = 50L * 1024 * 1024 * 1024)
    {
        _paths = paths;
        _projects = projects;
        _inspector = inspector;
        if (maximumFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileSizeBytes));
        }

        _maximumFileSizeBytes = maximumFileSizeBytes;
    }

    public async Task<LocalMediaReference> ImportAsync(
        ProjectManifest project,
        string sourcePath,
        MediaImportMode mode,
        decimal? maxVideoMinutes,
        IProgress<MediaImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (project.SourceVideo is not null)
        {
            throw new MediaInspectionException(
                "MEDIA_SOURCE_ALREADY_IMPORTED",
                "Dự án đã có video nguồn. Hãy tạo dự án mới để bảo vệ dữ liệu phụ đề và âm thanh hiện tại.");
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var sourceInfo = new FileInfo(fullSourcePath);
        if (!sourceInfo.Exists)
        {
            throw new MediaInspectionException("MEDIA_FILE_NOT_FOUND", "Không tìm thấy video đã chọn.");
        }

        if (!SupportedExtensions.Contains(sourceInfo.Extension))
        {
            throw new MediaInspectionException("MEDIA_EXTENSION_UNSUPPORTED", "App chỉ hỗ trợ MP4, MKV, MOV và WEBM.");
        }

        if (sourceInfo.Length <= 0)
        {
            throw new MediaInspectionException("MEDIA_FILE_EMPTY", "Video đã chọn không có dữ liệu.");
        }


        if (sourceInfo.Length > _maximumFileSizeBytes)
        {
            throw new MediaInspectionException(
                "MEDIA_FILE_TOO_LARGE",
                $"Video vượt giới hạn dung lượng {FormatGigabytes(_maximumFileSizeBytes):0.##} GB của App.");
        }

        var metadata = await _inspector.InspectAsync(fullSourcePath, cancellationToken);
        if (!metadata.HasVideo || metadata.DurationSeconds <= 0)
        {
            throw new MediaInspectionException("MEDIA_VIDEO_STREAM_MISSING", "Tệp không chứa luồng video hợp lệ.");
        }

        if (maxVideoMinutes is not null
            && metadata.DurationSeconds > (double)maxVideoMinutes.Value * 60 + 0.5)
        {
            throw new MediaInspectionException(
                "MEDIA_DURATION_LIMIT_EXCEEDED",
                $"Video dài hơn giới hạn {maxVideoMinutes:0.##} phút của gói hiện tại.");
        }

        string? relativePath = null;
        string effectivePath = fullSourcePath;
        string sha256;
        if (mode == MediaImportMode.Copy)
        {
            relativePath = Path.Combine("source", "original" + sourceInfo.Extension.ToLowerInvariant());
            effectivePath = _paths.GetProjectPath(project.ProjectId, relativePath);
            EnsureFreeDiskSpace(effectivePath, sourceInfo.Length);
            sha256 = await CopyAndHashAsync(
                fullSourcePath,
                effectivePath,
                sourceInfo.Length,
                progress,
                cancellationToken);
            File.SetAttributes(effectivePath, File.GetAttributes(effectivePath) | FileAttributes.ReadOnly);
        }
        else
        {
            sha256 = await HashAsync(fullSourcePath, sourceInfo.Length, progress, cancellationToken);
        }

        var media = new LocalMediaReference
        {
            Role = "SOURCE_VIDEO",
            ImportMode = mode.ToString().ToUpperInvariant(),
            OriginalPath = fullSourcePath,
            WorkspaceRelativePath = relativePath,
            FileName = sourceInfo.Name,
            SizeBytes = sourceInfo.Length,
            Sha256 = sha256,
            SourceLastWriteAtUtc = sourceInfo.LastWriteTimeUtc,
            Metadata = metadata,
        };
        project.SourceVideo = media;
        project.Status = ProjectStates.Ready;
        await _projects.SaveAsync(project, cancellationToken);
        return media;
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        long totalBytes,
        IProgress<MediaImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partialPath = destinationPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            var processed = 0L;
            var startedAt = DateTime.UtcNow;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                hash.AppendData(buffer, 0, bytesRead);
                processed += bytesRead;
                Report(progress, processed, totalBytes, startedAt);
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            if (processed != totalBytes)
            {
                throw new IOException("Kích thước video thay đổi trong lúc sao chép.");
            }

            destination.Close();
            if (File.Exists(destinationPath))
            {
                File.SetAttributes(destinationPath, FileAttributes.Normal);
            }

            File.Move(partialPath, destinationPath, overwrite: true);
            Report(progress, processed, totalBytes, startedAt);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            throw;
        }
    }

    private static async Task<string> HashAsync(
        string sourcePath,
        long totalBytes,
        IProgress<MediaImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var processed = 0L;
        var startedAt = DateTime.UtcNow;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
            processed += bytesRead;
            Report(progress, processed, totalBytes, startedAt);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Report(
        IProgress<MediaImportProgress>? progress,
        long processed,
        long total,
        DateTime startedAt)
    {
        if (progress is null)
        {
            return;
        }

        var elapsed = Math.Max(0.001, (DateTime.UtcNow - startedAt).TotalSeconds);
        progress.Report(new MediaImportProgress(
            processed,
            total,
            total <= 0 ? 0 : Math.Clamp(processed * 100d / total, 0, 100),
            processed / 1024d / 1024d / elapsed));
    }

    private static void EnsureFreeDiskSpace(string destinationPath, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        const long safetyMargin = 512L * 1024 * 1024;
        if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes + safetyMargin)
        {
            throw new MediaInspectionException(
                "MEDIA_DISK_SPACE_INSUFFICIENT",
                "Ổ đĩa không đủ dung lượng để sao chép video và tạo file xử lý tạm.");
        }
    }

    private static double FormatGigabytes(long bytes) => bytes / 1024d / 1024d / 1024d;
}

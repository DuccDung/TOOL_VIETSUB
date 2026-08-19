using System.Buffers;
using System.IO.Compression;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Infrastructure.FFmpeg;

public interface ISecureArchiveExtractor
{
    Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        long maximumExtractedBytes,
        CancellationToken cancellationToken);
}

public sealed class SecureZipExtractor : ISecureArchiveExtractor
{
    private const int BufferSize = 128 * 1024;

    public async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        long maximumExtractedBytes,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destinationDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        try
        {
            using var archive = ZipFile.OpenRead(Path.GetFullPath(archivePath));
            long extractedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectSymbolicLink(entry);
                var normalizedName = entry.FullName
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var outputPath = Path.GetFullPath(Path.Combine(root, normalizedName));
                if (!outputPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AppException(
                        AppErrorCode.FfmpegExtractionError,
                        "Archive FFmpeg chứa đường dẫn không an toàn.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }

                if (entry.Length < 0 || entry.Length > maximumExtractedBytes - extractedBytes)
                {
                    throw new AppException(
                        AppErrorCode.FfmpegExtractionError,
                        "Nội dung giải nén FFmpeg vượt quá giới hạn cho phép.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var input = entry.Open();
                await using var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        extractedBytes += read;
                        if (extractedBytes > maximumExtractedBytes)
                        {
                            throw new AppException(
                                AppErrorCode.FfmpegExtractionError,
                                "Nội dung giải nén FFmpeg vượt quá giới hạn cho phép.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new AppException(
                AppErrorCode.FfmpegExtractionError,
                "Không thể giải nén gói FFmpeg.",
                exception);
        }
    }

    private static void RejectSymbolicLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        if (unixMode == unixSymbolicLink)
        {
            throw new AppException(
                AppErrorCode.FfmpegExtractionError,
                "Archive FFmpeg chứa symbolic link không được phép.");
        }
    }
}

using System.Security.Cryptography;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Infrastructure.FFmpeg;

public interface IFFmpegPackageVerifier
{
    Task VerifySha256Async(string filePath, string expectedSha256, CancellationToken cancellationToken);
}

public sealed class FFmpegPackageVerifier : IFFmpegPackageVerifier
{
    public async Task VerifySha256Async(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedSha256.Trim());
        }
        catch (FormatException exception)
        {
            throw new AppException(
                AppErrorCode.FfmpegChecksumMismatch,
                "Checksum FFmpeg được cấu hình không hợp lệ.",
                exception);
        }

        if (expected.Length != SHA256.HashSizeInBytes)
        {
            throw new AppException(
                AppErrorCode.FfmpegChecksumMismatch,
                "Checksum FFmpeg phải là SHA-256.");
        }

        await using var stream = new FileStream(
            Path.GetFullPath(filePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new AppException(
                AppErrorCode.FfmpegChecksumMismatch,
                "Gói FFmpeg tải về không khớp SHA-256 và đã bị từ chối.");
        }
    }
}

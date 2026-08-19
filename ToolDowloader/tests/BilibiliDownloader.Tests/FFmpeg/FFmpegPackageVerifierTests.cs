using System.Security.Cryptography;
using System.Text;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Infrastructure.FFmpeg;
using BilibiliDownloader.Tests.Storage;

namespace BilibiliDownloader.Tests.FFmpeg;

public sealed class FFmpegPackageVerifierTests
{
    [Fact]
    public async Task VerifySha256Async_MatchingHash_Completes()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "package.zip");
        var bytes = Encoding.UTF8.GetBytes("trusted package");
        await File.WriteAllBytesAsync(path, bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes));

        await new FFmpegPackageVerifier().VerifySha256Async(path, expected, CancellationToken.None);
    }

    [Fact]
    public async Task VerifySha256Async_MismatchedHash_ThrowsClearError()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "package.zip");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        var exception = await Assert.ThrowsAsync<AppException>(() => new FFmpegPackageVerifier()
            .VerifySha256Async(path, new string('0', 64), CancellationToken.None));

        Assert.Equal("FFMPEG_CHECKSUM_MISMATCH", exception.PublicCode);
    }
}

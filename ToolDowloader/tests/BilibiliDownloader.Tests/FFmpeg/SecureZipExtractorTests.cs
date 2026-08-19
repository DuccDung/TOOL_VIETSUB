using System.IO.Compression;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Infrastructure.FFmpeg;
using BilibiliDownloader.Tests.Storage;

namespace BilibiliDownloader.Tests.FFmpeg;

public sealed class SecureZipExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ValidArchive_ExtractsFile()
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, "package.zip");
        CreateArchive(archivePath, "ffmpeg/bin/ffmpeg.exe", [1, 2, 3]);
        var destination = Path.Combine(directory.Path, "output");

        await new SecureZipExtractor().ExtractAsync(
            archivePath,
            destination,
            1024,
            CancellationToken.None);

        Assert.Equal(
            new byte[] { 1, 2, 3 },
            await File.ReadAllBytesAsync(Path.Combine(destination, "ffmpeg", "bin", "ffmpeg.exe")));
    }

    [Fact]
    public async Task ExtractAsync_PathTraversal_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, "package.zip");
        CreateArchive(archivePath, "../outside.exe", [1]);
        var destination = Path.Combine(directory.Path, "output");

        var exception = await Assert.ThrowsAsync<AppException>(() => new SecureZipExtractor().ExtractAsync(
            archivePath,
            destination,
            1024,
            CancellationToken.None));

        Assert.Equal("FFMPEG_EXTRACTION_ERROR", exception.PublicCode);
        Assert.False(File.Exists(Path.Combine(directory.Path, "outside.exe")));
    }

    [Fact]
    public async Task ExtractAsync_ExpandedContentTooLarge_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, "package.zip");
        CreateArchive(archivePath, "ffmpeg.exe", new byte[2048]);

        var exception = await Assert.ThrowsAsync<AppException>(() => new SecureZipExtractor().ExtractAsync(
            archivePath,
            Path.Combine(directory.Path, "output"),
            1024,
            CancellationToken.None));

        Assert.Equal("FFMPEG_EXTRACTION_ERROR", exception.PublicCode);
    }

    private static void CreateArchive(string archivePath, string entryName, byte[] contents)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var output = entry.Open();
        output.Write(contents);
    }
}

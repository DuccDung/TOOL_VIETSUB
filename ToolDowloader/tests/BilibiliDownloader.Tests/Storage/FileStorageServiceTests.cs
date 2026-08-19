using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BilibiliDownloader.Tests.Storage;

public sealed class FileStorageServiceTests
{
    private readonly FileStorageService _service = new(NullLogger<FileStorageService>.Instance);

    [Fact]
    public void SanitizeFileName_InvalidCharacters_ReplacesCharacters()
    {
        var result = _service.SanitizeFileName("Hello: World / Test?");

        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('?', result);
        Assert.Contains("Hello", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeFileName_EmptyTitle_ReturnsFallback()
    {
        Assert.Equal("video", _service.SanitizeFileName("   "));
    }

    [Fact]
    public void SanitizeFileName_VeryLongTitle_IsLimited()
    {
        var result = _service.SanitizeFileName(new string('a', 500), 100);

        Assert.Equal(100, result.Length);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("LPT1")]
    public void SanitizeFileName_ReservedWindowsName_PrefixesName(string value)
    {
        Assert.StartsWith("_", _service.SanitizeFileName(value), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUniqueOutputPath_ExistingFile_AddsSuffix()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "Video.mp4"), "existing");

        var result = _service.CreateUniqueOutputPath(directory.Path, "Video", "mp4");

        Assert.Equal(Path.Combine(directory.Path, "Video (1).mp4"), result);
    }

    [Fact]
    public void CreateUniqueOutputPath_UnsupportedExtension_Throws()
    {
        using var directory = new TemporaryDirectory();
        Assert.Throws<AppException>(() => _service.CreateUniqueOutputPath(directory.Path, "Video", "exe"));
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"BilibiliDownloaderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

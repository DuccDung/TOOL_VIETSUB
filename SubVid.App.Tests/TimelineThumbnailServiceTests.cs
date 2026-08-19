using SubVid.App.Core;
using SubVid.App.Media;

namespace SubVid.App.Tests;

public sealed class TimelineThumbnailServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "subvid-thumbnail-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetTimestamp_UsesStableCanonicalGrid()
    {
        Assert.Equal(0.3125, TimelineThumbnailService.GetTimestamp(100, 0), 6);
        Assert.Equal(50.3125, TimelineThumbnailService.GetTimestamp(100, 80), 6);
        Assert.Equal(99.6875, TimelineThumbnailService.GetTimestamp(100, 159), 6);
    }

    [Theory]
    [InlineData("/thumbnail/v1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/000.jpg", 0)]
    [InlineData("/thumbnail/v1/ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789/159.jpg", 159)]
    public void TryParseThumbnailPath_AcceptsOnlyVersionedCanonicalPaths(
        string path,
        int expectedIndex)
    {
        Assert.True(TimelineThumbnailService.TryParseThumbnailPath(
            path,
            out var sourceSha256,
            out var index));
        Assert.Equal(expectedIndex, index);
        Assert.Equal(sourceSha256.ToLowerInvariant(), sourceSha256);
    }

    [Theory]
    [InlineData("/thumbnail/v2/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/000.jpg")]
    [InlineData("/thumbnail/v1/not-a-hash/000.jpg")]
    [InlineData("/thumbnail/v1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/160.jpg")]
    [InlineData("/thumbnail/v1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/../000.jpg")]
    public void TryParseThumbnailPath_RejectsInvalidOrUnsafePaths(string path)
    {
        Assert.False(TimelineThumbnailService.TryParseThumbnailPath(path, out _, out _));
    }

    [Fact]
    public async Task CachePath_StaysInsideVersionedGlobalCache()
    {
        var paths = new AppPaths(_root);
        await using var service = new TimelineThumbnailService(paths, () => null);
        var sourceSha256 = new string('b', 64);

        var path = service.GetCachePath(sourceSha256, 42);

        Assert.Equal(
            paths.GetCachePath(
                "timeline-thumbnails",
                "v1",
                sourceSha256,
                "042.jpg"),
            path);
        Assert.Equal(
            $"https://media.subvid.local/thumbnail/v1/{sourceSha256}/042.jpg",
            TimelineThumbnailService.GetThumbnailUrl(sourceSha256, 42));
        Assert.Throws<InvalidOperationException>(() => paths.GetCachePath("..", "escape.jpg"));
    }

    [Fact]
    public async Task Request_ImmediatelyPublishesAUsableCachedThumbnail()
    {
        var paths = new AppPaths(_root);
        await using var service = new TimelineThumbnailService(paths, () => null);
        var projectId = Guid.NewGuid();
        var sourceSha256 = new string('c', 64);
        var sourcePath = paths.GetProjectPath(projectId, "source", "video.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllBytesAsync(sourcePath, [0]);
        var cachePath = service.GetCachePath(sourceSha256, 12);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllBytesAsync(cachePath, new byte[256]);
        TimelineThumbnailReady? published = null;
        service.ThumbnailReady += (_, thumbnail) => published = thumbnail;

        service.Request(projectId, sourcePath, sourceSha256, 60, [12]);

        Assert.NotNull(published);
        Assert.Equal(12, published.Index);
        Assert.Equal(sourceSha256, published.SourceSha256);
        Assert.Equal(TimelineThumbnailService.GetThumbnailUrl(sourceSha256, 12), published.Url);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

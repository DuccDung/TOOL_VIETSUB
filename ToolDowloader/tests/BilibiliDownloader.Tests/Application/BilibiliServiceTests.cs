using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Application.Services;
using BilibiliDownloader.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BilibiliDownloader.Tests.Application;

public sealed class BilibiliServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_InvalidUrl_DoesNotCallResolver()
    {
        var resolver = new FakeResolver();
        var service = new BilibiliService(
            new FakeParser(BilibiliUrlInfo.Invalid("invalid")),
            resolver,
            NullLogger<BilibiliService>.Instance);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            service.AnalyzeAsync("bad", CancellationToken.None));

        Assert.Equal("INVALID_URL", exception.PublicCode);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_ValidUrl_ReturnsResolverResult()
    {
        var expected = new BilibiliVideoDto
        {
            Id = "BV1BLBeB9E9c",
            SourceUrl = "https://www.bilibili.com/video/BV1BLBeB9E9c/",
            Title = "Video",
            Author = "Author",
            ThumbnailUrl = "https://i0.hdslb.com/image.jpg",
            Duration = TimeSpan.FromMinutes(1)
        };
        var resolver = new FakeResolver(expected);
        var service = new BilibiliService(
            new FakeParser(new BilibiliUrlInfo(expected.Id, true, NormalizedUrl: expected.SourceUrl)),
            resolver,
            NullLogger<BilibiliService>.Instance);

        var result = await service.AnalyzeAsync(expected.SourceUrl, CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal(1, resolver.CallCount);
    }

    private sealed class FakeParser(BilibiliUrlInfo result) : IBilibiliUrlParser
    {
        public BilibiliUrlInfo Parse(string url) => result;
    }

    private sealed class FakeResolver(BilibiliVideoDto? result = null) : IBilibiliResolver
    {
        public int CallCount { get; private set; }

        public Task<BilibiliVideoDto> ResolveAsync(
            BilibiliUrlInfo urlInfo,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result ?? throw new InvalidOperationException("Resolver should not be called."));
        }
    }
}

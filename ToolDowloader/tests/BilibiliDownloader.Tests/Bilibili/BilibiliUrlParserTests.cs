using BilibiliDownloader.Infrastructure.Bilibili;

namespace BilibiliDownloader.Tests.Bilibili;

public sealed class BilibiliUrlParserTests
{
    private readonly BilibiliUrlParser _parser = new();

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1BLBeB9E9c/")]
    [InlineData("https://bilibili.com/video/BV1BLBeB9E9c")]
    [InlineData("  https://www.bilibili.com/video/BV1BLBeB9E9c/?from=search  ")]
    public void Parse_ValidVideoUrl_ReturnsNormalizedInfo(string url)
    {
        var result = _parser.Parse(url);

        Assert.True(result.IsValid);
        Assert.Equal("BV1BLBeB9E9c", result.VideoId);
        Assert.Equal("https://www.bilibili.com/video/BV1BLBeB9E9c/", result.NormalizedUrl);
    }

    [Fact]
    public void Parse_MultipartUrl_ReturnsPageNumber()
    {
        var result = _parser.Parse("https://www.bilibili.com/video/BV1BLBeB9E9c?p=3");

        Assert.True(result.IsValid);
        Assert.Equal(3, result.PageNumber);
        Assert.EndsWith("?p=3", result.NormalizedUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("http://www.bilibili.com/video/BV1BLBeB9E9c")]
    [InlineData("https://evil.example/video/BV1BLBeB9E9c")]
    [InlineData("https://www.bilibili.com@evil.example/video/BV1BLBeB9E9c")]
    [InlineData("https://www.bilibili.com/read/BV1BLBeB9E9c")]
    [InlineData("https://www.bilibili.com/video/not-a-bvid")]
    [InlineData("https://b23.tv/abc123")]
    [InlineData("https://www.bilibili.com:8443/video/BV1BLBeB9E9c")]
    public void Parse_InvalidOrUnsupportedUrl_ReturnsInvalid(string url)
    {
        var result = _parser.Parse(url);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Error!);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("10000")]
    public void Parse_InvalidPage_ReturnsInvalid(string page)
    {
        var result = _parser.Parse($"https://www.bilibili.com/video/BV1BLBeB9E9c?p={page}");

        Assert.False(result.IsValid);
    }
}

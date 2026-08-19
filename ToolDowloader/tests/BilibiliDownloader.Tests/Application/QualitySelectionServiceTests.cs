using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Services;
using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Tests.Application;

public sealed class QualitySelectionServiceTests
{
    private readonly QualitySelectionService _service = new();

    [Fact]
    public void SelectBest_P1080Available_Returns1080()
    {
        var result = _service.SelectBest(CreateStreams(1080, 720, 480), VideoQuality.P1080);

        Assert.Equal(1080, result.Height);
    }

    [Fact]
    public void SelectBest_P720Available_Returns720()
    {
        var result = _service.SelectBest(CreateStreams(1080, 720, 480), VideoQuality.P720);

        Assert.Equal(720, result.Height);
    }

    [Fact]
    public void SelectBest_PreferredUnavailable_FallsBackBelowTarget()
    {
        var result = _service.SelectBest(CreateStreams(1080, 480, 360), VideoQuality.P720);

        Assert.Equal(480, result.Height);
    }

    [Fact]
    public void SelectBest_BestAvailable_ReturnsHighest()
    {
        var result = _service.SelectBest(CreateStreams(480, 2160, 1080), VideoQuality.BestAvailable);

        Assert.Equal(2160, result.Height);
    }

    private static BilibiliStreamDto[] CreateStreams(params int[] heights) => heights.Select(height => new BilibiliStreamDto
    {
        Id = height.ToString(System.Globalization.CultureInfo.InvariantCulture),
        QualityId = height,
        Width = height * 16 / 9,
        Height = height,
        Quality = $"{height}P",
        VideoUrl = "https://media.example/video"
    }).ToArray();
}

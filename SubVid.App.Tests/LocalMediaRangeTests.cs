using SubVid.App.Playback;

namespace SubVid.App.Tests;

public sealed class LocalMediaRangeTests
{
    [Theory]
    [InlineData(null, 0, 999)]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=500-", 500, 999)]
    [InlineData("bytes=-100", 900, 999)]
    [InlineData("bytes=900-2000", 900, 999)]
    public void TryParse_AcceptsSingleValidRange(string? header, long start, long end)
    {
        Assert.True(LocalMediaRange.TryParse(header, 1000, out var range));
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
    }

    [Theory]
    [InlineData("items=0-10")]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=100-50")]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("bytes=-0")]
    public void TryParse_RejectsInvalidOrMultipleRange(string header)
    {
        Assert.False(LocalMediaRange.TryParse(header, 1000, out _));
    }

    [Fact]
    public async Task BoundedReadStream_NeverReadsOutsideSelectedRange()
    {
        var source = new MemoryStream(Enumerable.Range(0, 100).Select(value => (byte)value).ToArray());
        await using var bounded = new BoundedReadStream(source, 20, 10);
        var buffer = new byte[32];

        var read = await bounded.ReadAsync(buffer);
        var afterEnd = await bounded.ReadAsync(buffer);

        Assert.Equal(10, read);
        Assert.Equal(0, afterEnd);
        Assert.Equal(Enumerable.Range(20, 10).Select(value => (byte)value), buffer[..read]);
    }
}

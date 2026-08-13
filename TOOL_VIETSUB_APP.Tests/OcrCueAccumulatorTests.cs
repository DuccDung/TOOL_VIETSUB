using TOOL_VIETSUB_APP.LocalAi;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class OcrCueAccumulatorTests
{
    [Fact]
    public void Add_MergesSmallOcrChangesAndSplitsDifferentSubtitle()
    {
        var accumulator = new OcrCueAccumulator(500);

        accumulator.Add(0, "Hello world", 0.9f);
        accumulator.Add(500, "Hello wor1d", 0.85f);
        accumulator.Add(1000, "Second line", 0.92f);
        accumulator.Add(1500, "Second line", 0.91f);
        accumulator.Complete();

        Assert.Equal(2, accumulator.Completed.Count);
        Assert.Equal(0, accumulator.Completed[0].StartMilliseconds);
        Assert.Equal(1000, accumulator.Completed[0].EndMilliseconds);
        Assert.Equal(1000, accumulator.Completed[1].StartMilliseconds);
        Assert.Equal(2000, accumulator.Completed[1].EndMilliseconds);
    }

    [Fact]
    public void Add_DropsSingleLowConfidenceFlash()
    {
        var accumulator = new OcrCueAccumulator(500);

        accumulator.Add(0, "Noise", 0.5f);
        accumulator.Add(500, string.Empty, 0);
        accumulator.Complete();

        Assert.Empty(accumulator.Completed);
    }
}

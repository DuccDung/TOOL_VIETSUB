using SubVid.App.Core;
using SubVid.App.Jobs;

namespace SubVid.App.Tests;

public sealed class VoiceTimelineFitPolicyTests
{
    [Fact]
    public void Analyze_WhenWaveIsShorter_KeepsNaturalSpeedAndPadsSilence()
    {
        var result = VoiceTimelineFitPolicy.Analyze(0.7, 2.0, 12);

        Assert.Equal(VoiceTimingStatuses.Padded, result.Status);
        Assert.Equal(1, result.AppliedTempo);
        Assert.Equal(1.3, result.PaddingSeconds, 6);
        Assert.True(result.RequiredTempo < 1);
    }

    [Fact]
    public void Analyze_WhenDurationsMatch_KeepsNaturalSpeed()
    {
        var result = VoiceTimelineFitPolicy.Analyze(2.0, 2.0, 12);

        Assert.Equal(VoiceTimingStatuses.Natural, result.Status);
        Assert.Equal(1, result.AppliedTempo);
        Assert.Equal(0, result.PaddingSeconds);
    }

    [Theory]
    [InlineData(2.1, 2.0, 1.05)]
    [InlineData(2.4, 2.0, 1.20)]
    public void Analyze_WhenWaveFitsWithinLimit_AppliesOnlyRequiredCompression(
        double sourceDuration,
        double targetDuration,
        double expectedTempo)
    {
        var result = VoiceTimelineFitPolicy.Analyze(sourceDuration, targetDuration, 20);

        Assert.Equal(VoiceTimingStatuses.Compressed, result.Status);
        Assert.Equal(expectedTempo, result.AppliedTempo!.Value, 6);
        Assert.InRange(result.AppliedTempo.Value, 1, 1.20);
    }

    [Fact]
    public void Analyze_WhenWaveNeedsUnsafeSpeed_AppliesSafeTempoAndAllowsOverflow()
    {
        var result = VoiceTimelineFitPolicy.Analyze(3.0, 2.0, 30);

        Assert.Equal(VoiceTimingStatuses.ReviewRequired, result.Status);
        Assert.Equal(VoiceTimingSeverities.Warning, result.Severity);
        Assert.Equal(VoiceTimelineFitPolicy.DefaultMaximumAutomaticTempo, result.AppliedTempo);
        Assert.Equal("ALLOW_OVERFLOW", result.ResolutionAction);
        Assert.Equal(2.5, result.RenderDurationSeconds, 6);
        Assert.Equal(1.5, result.RequiredTempo, 6);
        Assert.Equal(24, result.SuggestedMaximumCharacters);
    }

    [Fact]
    public void Analyze_WhenWaveFitsInBorrowedGap_DoesNotChangePlaybackSpeed()
    {
        var result = VoiceTimelineFitPolicy.Analyze(new VoiceTimelineFitInput(
            RawDurationSeconds: 1.3,
            PlayableDurationSeconds: 1.3,
            TargetDurationSeconds: 1,
            EffectiveWindowSeconds: 1.5,
            TranslatedCharacterCount: 20,
            BorrowedGapSeconds: 0.5));

        Assert.Equal(VoiceTimingStatuses.GapFitted, result.Status);
        Assert.Equal(1, result.AppliedTempo);
        Assert.Equal(0.3, result.BorrowedGapSeconds, 6);
        Assert.Equal(1.3, result.RenderDurationSeconds, 6);
    }

    [Fact]
    public void Analyze_WhenTempoIsAbovePreferredButBelowHardLimit_ReturnsWarning()
    {
        var result = VoiceTimelineFitPolicy.Analyze(new VoiceTimelineFitInput(
            RawDurationSeconds: 1.18,
            PlayableDurationSeconds: 1.18,
            TargetDurationSeconds: 1,
            EffectiveWindowSeconds: 1,
            TranslatedCharacterCount: 20));

        Assert.Equal(VoiceTimingStatuses.Compressed, result.Status);
        Assert.Equal(VoiceTimingSeverities.Warning, result.Severity);
        Assert.Equal("PREFER_SHORTER_TEXT", result.ResolutionAction);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void Analyze_WhenDurationIsInvalid_RejectsIt(double sourceDuration, double targetDuration)
    {
        var result = VoiceTimelineFitPolicy.Analyze(sourceDuration, targetDuration, 10);

        Assert.Equal(VoiceTimingStatuses.Invalid, result.Status);
        Assert.Null(result.AppliedTempo);
    }

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.1, 1.1)]
    [InlineData(2.0, 1.2)]
    public void NormalizeMaximumTempo_StaysInsideSafeRange(double input, double expected)
    {
        Assert.Equal(expected, VoiceTimelineFitPolicy.NormalizeMaximumTempo(input), 6);
    }
}

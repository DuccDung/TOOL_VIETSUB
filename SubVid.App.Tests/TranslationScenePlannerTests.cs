using SubVid.App.Core;
using SubVid.App.Translation;

namespace SubVid.App.Tests;

public sealed class TranslationScenePlannerTests
{
    [Fact]
    public void Plan_PreservesEveryTargetOnceAndKeepsContextInsideScene()
    {
        var cues = Enumerable.Range(0, 12).Select(index => new SubtitleCue
        {
            StartMilliseconds = index < 7 ? index * 1000 : 20_000 + (index - 7) * 1000,
            EndMilliseconds = index < 7 ? index * 1000 + 800 : 20_800 + (index - 7) * 1000,
            OriginalText = $"Sentence {index}",
        }).ToArray();
        var targets = cues.Where((_, index) => index is 1 or 2 or 3 or 4 or 8 or 9)
            .Select(cue => cue.CueId)
            .ToHashSet();
        cues[0].TranslatedText = "Bản dịch ngữ cảnh đã duyệt";
        cues[0].TranslationLocked = true;

        var plans = TranslationScenePlanner.Plan(
            cues,
            targets,
            maximumTargetCues: 3,
            contextCueCount: 2,
            sceneGapMilliseconds: 5000,
            maximumCharactersPerSecond: 18);

        var plannedTargets = plans.SelectMany(plan => plan.Cues)
            .Where(cue => cue.IsTarget)
            .Select(cue => cue.CueId)
            .ToArray();
        Assert.Equal(targets.Count, plannedTargets.Length);
        Assert.Equal(targets, plannedTargets.ToHashSet());
        Assert.All(plans, plan => Assert.InRange(plan.Cues.Count(cue => cue.IsTarget), 1, 3));
        Assert.DoesNotContain(plans, plan =>
            plan.Cues.Any(cue => cue.StartMilliseconds < 10_000)
            && plan.Cues.Any(cue => cue.StartMilliseconds >= 20_000));
        Assert.All(plans.SelectMany(plan => plan.Cues), cue =>
            Assert.True(cue.SuggestedMaximumCharacters >= 8));
        Assert.Contains(plans.SelectMany(plan => plan.Cues), cue =>
            !cue.IsTarget && cue.CandidateTranslation == "Bản dịch ngữ cảnh đã duyệt");
    }

    [Fact]
    public void Plan_WhenVoiceTimingRequiresReview_UsesMeasuredCharacterBudget()
    {
        var cue = new SubtitleCue
        {
            StartMilliseconds = 0,
            EndMilliseconds = 500,
            OriginalText = "A long sentence",
            TranslatedText = "Đây là một câu quá dài",
            VoiceTiming = new VoiceTimingAnalysis
            {
                Status = VoiceTimingStatuses.ReviewRequired,
                SuggestedMaximumCharacters = 5,
            },
        };

        var plan = Assert.Single(TranslationScenePlanner.Plan(
            [cue],
            new HashSet<Guid> { cue.CueId },
            maximumTargetCues: 3,
            contextCueCount: 0,
            sceneGapMilliseconds: 5_000,
            maximumCharactersPerSecond: 18));

        Assert.Equal(5, Assert.Single(plan.Cues).SuggestedMaximumCharacters);
    }
}

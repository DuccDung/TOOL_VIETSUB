using SubVid.App.Core;
using SubVid.App.Jobs;

namespace SubVid.App.Tests;

public sealed class VoicePhrasePlannerTests
{
    [Fact]
    public void Plan_GroupsNearbyCuesFromSameSpeakerAndKeepsStableId()
    {
        var project = CreateProject();
        var first = Cue(0, 1_000, "Xin chào", "speaker_1");
        var second = Cue(1_300, 2_100, "Bạn khỏe không?", "speaker_1");

        var firstRun = VoicePhrasePlanner.Plan(project, [first, second], 500, 8);
        var secondRun = VoicePhrasePlanner.Plan(project, [first, second], 500, 8);

        var phrase = Assert.Single(firstRun);
        Assert.Equal(2, phrase.Cues.Count);
        Assert.Equal(phrase.PhraseId, Assert.Single(secondRun).PhraseId);
    }

    [Fact]
    public void Plan_DoesNotGroupDifferentSpeakersOrLargeGaps()
    {
        var project = CreateProject();
        var cues = new[]
        {
            Cue(0, 1_000, "Câu một", "speaker_1"),
            Cue(1_200, 2_000, "Câu hai", "speaker_2"),
            Cue(3_000, 4_000, "Câu ba", "speaker_2"),
        };

        var phrases = VoicePhrasePlanner.Plan(project, cues, 500, 8);

        Assert.Equal(3, phrases.Count);
    }

    [Fact]
    public void Plan_AutoMode_UsesSentenceBoundaryAndDoesNotInventPunctuation()
    {
        var project = CreateProject();
        var first = Cue(0, 1_000, "Xin chào.", "speaker_1");
        var second = Cue(1_100, 2_000, "Tôi là Nam", "speaker_1");
        var third = Cue(2_100, 3_000, "rất vui được gặp bạn", "speaker_1");

        var phrases = VoicePhrasePlanner.Plan(project, [first, second, third], 500, 8);

        Assert.Equal(2, phrases.Count);
        Assert.Equal("Xin chào.", phrases[0].SynthesisText);
        Assert.Equal("Tôi là Nam rất vui được gặp bạn", phrases[1].SynthesisText);
    }

    [Fact]
    public void Plan_ManualJoinAndBreak_ProduceRequestedCueGroups()
    {
        var project = CreateProject();
        var cue101 = Cue(0, 1_000, "Câu một.", "speaker_1");
        var cue102 = Cue(1_100, 2_000, "Câu hai", "speaker_1");
        var cue103 = Cue(2_100, 3_000, "Câu ba", "speaker_1");
        project.VoicePhraseBoundaries =
        [
            new VoicePhraseBoundaryOverride
            {
                PreviousCueId = cue101.CueId,
                NextCueId = cue102.CueId,
                Mode = VoicePhraseBoundaryModes.Join,
            },
            new VoicePhraseBoundaryOverride
            {
                PreviousCueId = cue102.CueId,
                NextCueId = cue103.CueId,
                Mode = VoicePhraseBoundaryModes.Break,
            },
        ];

        var phrases = VoicePhrasePlanner.Plan(project, [cue101, cue102, cue103], 500, 8);

        Assert.Equal(2, phrases.Count);
        Assert.Equal([cue101.CueId, cue102.CueId], phrases[0].Cues.Select(cue => cue.CueId));
        Assert.Equal([cue103.CueId], phrases[1].Cues.Select(cue => cue.CueId));
        Assert.Equal("Câu một. Câu hai", phrases[0].SynthesisText);
    }

    [Fact]
    public void Plan_ManualJoin_DoesNotBypassSpeakerConstraint()
    {
        var project = CreateProject();
        var first = Cue(0, 1_000, "Câu một", "speaker_1");
        var second = Cue(1_100, 2_000, "Câu hai", "speaker_2");
        project.VoicePhraseBoundaries.Add(new VoicePhraseBoundaryOverride
        {
            PreviousCueId = first.CueId,
            NextCueId = second.CueId,
            Mode = VoicePhraseBoundaryModes.Join,
        });

        var phrases = VoicePhrasePlanner.Plan(project, [first, second], 500, 8);

        Assert.Equal(2, phrases.Count);
        Assert.Contains("speaker", VoicePhrasePlanner.GetManualJoinConstraint(project, first, second, 8));
    }

    [Fact]
    public void Plan_DoesNotJoinAcrossCueWithoutTranslation()
    {
        var project = CreateProject();
        var first = Cue(0, 1_000, "Câu một", "speaker_1");
        var missing = Cue(1_100, 2_000, string.Empty, "speaker_1");
        var third = Cue(2_100, 3_000, "Câu ba", "speaker_1");

        var phrases = VoicePhrasePlanner.Plan(project, [first, missing, third], 2_000, 8);

        Assert.Equal(2, phrases.Count);
        Assert.All(phrases, phrase => Assert.Single(phrase.Cues));
    }

    private static ProjectManifest CreateProject() => new()
    {
        ProjectId = Guid.NewGuid(),
        OwnerUserId = Guid.NewGuid(),
        Name = "Phrase test",
    };

    private static SubtitleCue Cue(long start, long end, string text, string speaker) => new()
    {
        StartMilliseconds = start,
        EndMilliseconds = end,
        TranslatedText = text,
        Speaker = speaker,
    };
}

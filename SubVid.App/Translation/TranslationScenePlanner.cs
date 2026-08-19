using SubVid.App.Core;

namespace SubVid.App.Translation;

public sealed record PlannedTranslationScene(
    int SceneNumber,
    IReadOnlyList<TranslationCueInput> Cues);

public static class TranslationScenePlanner
{
    public static IReadOnlyList<PlannedTranslationScene> Plan(
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlySet<Guid> targetCueIds,
        int maximumTargetCues,
        int contextCueCount,
        int sceneGapMilliseconds,
        double maximumCharactersPerSecond)
    {
        if (cues.Count == 0 || targetCueIds.Count == 0)
        {
            return [];
        }

        maximumTargetCues = Math.Clamp(maximumTargetCues, 1, 30);
        contextCueCount = Math.Clamp(contextCueCount, 0, 10);
        sceneGapMilliseconds = Math.Clamp(sceneGapMilliseconds, 1000, 60_000);
        maximumCharactersPerSecond = double.IsFinite(maximumCharactersPerSecond)
            ? Math.Clamp(maximumCharactersPerSecond, 8, 30)
            : 18;

        var ranges = FindSceneRanges(cues, sceneGapMilliseconds);
        var plans = new List<PlannedTranslationScene>();
        foreach (var (sceneStart, sceneEnd) in ranges)
        {
            var targets = Enumerable.Range(sceneStart, sceneEnd - sceneStart + 1)
                .Where(index => targetCueIds.Contains(cues[index].CueId))
                .ToArray();
            for (var offset = 0; offset < targets.Length; offset += maximumTargetCues)
            {
                var targetChunk = targets.Skip(offset).Take(maximumTargetCues).ToArray();
                var first = Math.Max(sceneStart, targetChunk[0] - contextCueCount);
                var last = Math.Min(sceneEnd, targetChunk[^1] + contextCueCount);
                var chunkIds = targetChunk.Select(index => cues[index].CueId).ToHashSet();
                var inputs = Enumerable.Range(first, last - first + 1)
                    .Select(index => ToInput(cues[index], chunkIds.Contains(cues[index].CueId), maximumCharactersPerSecond))
                    .ToArray();
                plans.Add(new PlannedTranslationScene(plans.Count + 1, inputs));
            }
        }

        return plans;
    }

    private static IReadOnlyList<(int Start, int End)> FindSceneRanges(
        IReadOnlyList<SubtitleCue> cues,
        int gapMilliseconds)
    {
        var ranges = new List<(int Start, int End)>();
        var start = 0;
        for (var index = 1; index < cues.Count; index++)
        {
            var gap = cues[index].StartMilliseconds - cues[index - 1].EndMilliseconds;
            if (gap <= gapMilliseconds)
            {
                continue;
            }

            ranges.Add((start, index - 1));
            start = index;
        }

        ranges.Add((start, cues.Count - 1));
        return ranges;
    }

    private static TranslationCueInput ToInput(
        SubtitleCue cue,
        bool isTarget,
        double maximumCharactersPerSecond)
    {
        var durationSeconds = Math.Max(0.25, (cue.EndMilliseconds - cue.StartMilliseconds) / 1000d);
        var durationMaximum = Math.Max(8, (int)Math.Ceiling(durationSeconds * maximumCharactersPerSecond));
        var suggestedMaximum = cue.VoiceTiming is
            {
                Status: VoiceTimingStatuses.ReviewRequired,
                SuggestedMaximumCharacters: > 0,
            }
            ? Math.Max(3, Math.Min(durationMaximum, cue.VoiceTiming.SuggestedMaximumCharacters.Value))
            : durationMaximum;
        return new TranslationCueInput(
            cue.CueId,
            cue.StartMilliseconds,
            cue.EndMilliseconds,
            string.IsNullOrWhiteSpace(cue.Speaker) ? "speaker_unknown" : cue.Speaker.Trim(),
            cue.OriginalText.Trim(),
            isTarget,
            suggestedMaximum,
            cue.TranslationLocked && !string.IsNullOrWhiteSpace(cue.TranslatedText)
                ? cue.TranslatedText.Trim()
                : null);
    }
}

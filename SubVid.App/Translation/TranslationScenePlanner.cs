using System.Text;
using SubVid.App.Core;

namespace SubVid.App.Translation;

public sealed record PlannedTranslationScene(
    int SceneNumber,
    int ChapterNumber,
    long ChapterStartMilliseconds,
    long ChapterEndMilliseconds,
    IReadOnlyList<TranslationCueInput> Cues);

public static class TranslationScenePlanner
{
    public const int DefaultMaximumChapterDurationMilliseconds = 10 * 60 * 1000;
    public const int DefaultMaximumSceneSourceCharacters = 6000;

    public static IReadOnlyList<PlannedTranslationScene> Plan(
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlySet<Guid> targetCueIds,
        int maximumTargetCues,
        int contextCueCount,
        int sceneGapMilliseconds,
        double maximumCharactersPerSecond,
        int maximumChapterDurationMilliseconds = DefaultMaximumChapterDurationMilliseconds,
        int maximumSceneSourceCharacters = DefaultMaximumSceneSourceCharacters)
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
        maximumChapterDurationMilliseconds = Math.Clamp(
            maximumChapterDurationMilliseconds,
            60_000,
            30 * 60 * 1000);
        maximumSceneSourceCharacters = Math.Clamp(maximumSceneSourceCharacters, 1000, 20_000);

        var ranges = FindChapterRanges(cues, sceneGapMilliseconds, maximumChapterDurationMilliseconds);
        var plans = new List<PlannedTranslationScene>();
        for (var chapterIndex = 0; chapterIndex < ranges.Count; chapterIndex++)
        {
            var (chapterStart, chapterEnd) = ranges[chapterIndex];
            var targets = Enumerable.Range(chapterStart, chapterEnd - chapterStart + 1)
                .Where(index => targetCueIds.Contains(cues[index].CueId))
                .ToArray();
            foreach (var targetChunk in PackTargets(
                cues,
                targets,
                maximumTargetCues,
                maximumSceneSourceCharacters))
            {
                var first = Math.Max(chapterStart, targetChunk[0] - contextCueCount);
                var last = Math.Min(chapterEnd, targetChunk[^1] + contextCueCount);
                var chunkIds = targetChunk.Select(index => cues[index].CueId).ToHashSet();
                var inputs = Enumerable.Range(first, last - first + 1)
                    .Select(index => ToInput(cues[index], chunkIds.Contains(cues[index].CueId), maximumCharactersPerSecond))
                    .ToArray();
                plans.Add(new PlannedTranslationScene(
                    plans.Count + 1,
                    chapterIndex + 1,
                    cues[chapterStart].StartMilliseconds,
                    cues[chapterEnd].EndMilliseconds,
                    inputs));
            }
        }

        return plans;
    }

    public static string BuildChapterContext(
        PlannedTranslationScene scene,
        IReadOnlyList<SubtitleCue> allCues,
        int maximumCharacters = 2200)
    {
        maximumCharacters = Math.Clamp(maximumCharacters, 400, 4000);
        var chapterCues = allCues
            .Where(cue => cue.StartMilliseconds >= scene.ChapterStartMilliseconds
                && cue.EndMilliseconds <= scene.ChapterEndMilliseconds)
            .ToArray();
        if (chapterCues.Length == 0)
        {
            return string.Empty;
        }

        var targetIds = scene.Cues.Where(cue => cue.IsTarget).Select(cue => cue.CueId).ToHashSet();
        var firstTargetTime = scene.Cues.Where(cue => cue.IsTarget)
            .Select(cue => cue.StartMilliseconds)
            .DefaultIfEmpty(scene.ChapterStartMilliseconds)
            .Min();
        var previousTranslations = chapterCues
            .Where(cue => cue.EndMilliseconds <= firstTargetTime
                && !string.IsNullOrWhiteSpace(cue.TranslatedText)
                && !targetIds.Contains(cue.CueId))
            .TakeLast(6)
            .ToArray();
        var sampleIndices = BuildSampleIndices(chapterCues.Length, 8);
        var builder = new StringBuilder();
        builder.Append("Chapter ").Append(scene.ChapterNumber)
            .Append(" from ").Append(FormatTime(scene.ChapterStartMilliseconds))
            .Append(" to ").Append(FormatTime(scene.ChapterEndMilliseconds)).AppendLine(".");
        builder.AppendLine("Representative source dialogue:");
        foreach (var index in sampleIndices)
        {
            AppendLimited(builder, $"- {chapterCues[index].Speaker}: {chapterCues[index].OriginalText}\n", maximumCharacters);
        }

        if (previousTranslations.Length > 0 && builder.Length < maximumCharacters)
        {
            AppendLimited(builder, "Recent Vietnamese continuity:\n", maximumCharacters);
            foreach (var cue in previousTranslations)
            {
                AppendLimited(
                    builder,
                    $"- {cue.Speaker}: {cue.OriginalText} => {cue.TranslatedText}\n",
                    maximumCharacters);
            }
        }

        return builder.ToString().Trim();
    }

    private static IReadOnlyList<(int Start, int End)> FindChapterRanges(
        IReadOnlyList<SubtitleCue> cues,
        int gapMilliseconds,
        int maximumDurationMilliseconds)
    {
        var ranges = new List<(int Start, int End)>();
        var start = 0;
        for (var index = 1; index < cues.Count; index++)
        {
            var gap = cues[index].StartMilliseconds - cues[index - 1].EndMilliseconds;
            var chapterDuration = cues[index].EndMilliseconds - cues[start].StartMilliseconds;
            if (gap <= gapMilliseconds && chapterDuration <= maximumDurationMilliseconds)
            {
                continue;
            }

            ranges.Add((start, index - 1));
            start = index;
        }

        ranges.Add((start, cues.Count - 1));
        return ranges;
    }

    private static IReadOnlyList<int[]> PackTargets(
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlyList<int> targets,
        int maximumTargetCues,
        int maximumSourceCharacters)
    {
        var chunks = new List<int[]>();
        var current = new List<int>();
        var characters = 0;
        foreach (var index in targets)
        {
            var cueCharacters = Math.Max(1, cues[index].OriginalText?.Length ?? 0);
            if (current.Count > 0
                && (current.Count >= maximumTargetCues
                    || characters + cueCharacters > maximumSourceCharacters))
            {
                chunks.Add(current.ToArray());
                current.Clear();
                characters = 0;
            }

            current.Add(index);
            characters += cueCharacters;
        }

        if (current.Count > 0)
        {
            chunks.Add(current.ToArray());
        }

        return chunks;
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
            !isTarget && !string.IsNullOrWhiteSpace(cue.TranslatedText)
                ? cue.TranslatedText.Trim()
                : null);
    }

    private static IReadOnlyList<int> BuildSampleIndices(int count, int maximum)
    {
        if (count <= maximum)
        {
            return Enumerable.Range(0, count).ToArray();
        }

        return Enumerable.Range(0, maximum)
            .Select(index => (int)Math.Round(index * (count - 1d) / (maximum - 1d)))
            .Distinct()
            .ToArray();
    }

    private static void AppendLimited(StringBuilder builder, string value, int maximum)
    {
        if (builder.Length >= maximum)
        {
            return;
        }

        var remaining = maximum - builder.Length;
        builder.Append(value.Length <= remaining ? value : value[..remaining]);
    }

    private static string FormatTime(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss");
}

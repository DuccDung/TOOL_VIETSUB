using System.Security.Cryptography;
using System.Text;
using SubVid.App.Core;
using SubVid.App.LocalAi;

namespace SubVid.App.Jobs;

public sealed record VoicePhrasePlan(
    string PhraseId,
    Guid RequestId,
    IReadOnlyList<SubtitleCue> Cues,
    long StartMilliseconds,
    long EndMilliseconds,
    string Speaker,
    string VoiceId,
    string SynthesisText);

/// <summary>
/// Builds stable dialogue groups for phrase-level synthesis. Automatic punctuation and gap
/// rules can be overridden at an adjacent cue boundary without weakening voice and safety
/// constraints.
/// </summary>
public static class VoicePhrasePlanner
{
    public const int MaximumPhraseCharacters = 4_500;
    public const int MaximumManualJoinGapMilliseconds = 2_000;

    public static IReadOnlyList<VoicePhrasePlan> Plan(
        ProjectManifest project,
        IEnumerable<SubtitleCue> source,
        int maximumGapMilliseconds,
        double maximumDurationSeconds)
    {
        var cues = source
            .OrderBy(cue => cue.StartMilliseconds)
            .ThenBy(cue => cue.EndMilliseconds)
            .ToArray();
        if (cues.Length == 0)
        {
            return [];
        }

        var maximumGap = Math.Clamp(maximumGapMilliseconds, 0, 2_000);
        var maximumDurationMilliseconds = (long)Math.Round(
            Math.Clamp(
                double.IsFinite(maximumDurationSeconds) ? maximumDurationSeconds : 8,
                1,
                20) * 1_000);
        var result = new List<VoicePhrasePlan>();
        var current = new List<SubtitleCue>();
        foreach (var cue in cues)
        {
            if (string.IsNullOrWhiteSpace(cue.TranslatedText))
            {
                if (current.Count > 0)
                {
                    result.Add(Create(project, current));
                    current = [];
                }

                continue;
            }

            if (current.Count > 0 && !CanAppend(project, current, cue, maximumGap, maximumDurationMilliseconds))
            {
                result.Add(Create(project, current));
                current = [];
            }

            current.Add(cue);
        }

        if (current.Count > 0)
        {
            result.Add(Create(project, current));
        }

        return result;
    }

    private static bool CanAppend(
        ProjectManifest project,
        IReadOnlyList<SubtitleCue> current,
        SubtitleCue next,
        int maximumGapMilliseconds,
        long maximumDurationMilliseconds)
    {
        var previous = current[^1];
        var gap = next.StartMilliseconds - previous.EndMilliseconds;
        var boundaryMode = GetBoundaryMode(project, previous.CueId, next.CueId);
        if (boundaryMode == VoicePhraseBoundaryModes.Break)
        {
            return false;
        }

        if (!CanJoinUnderHardConstraints(
                project,
                current,
                next,
                gap,
                maximumDurationMilliseconds,
                out _))
        {
            return false;
        }

        if (boundaryMode == VoicePhraseBoundaryModes.Join)
        {
            return true;
        }

        if (gap > maximumGapMilliseconds || EndsCompleteSentence(previous.TranslatedText))
        {
            return false;
        }

        return true;
    }

    public static string GetBoundaryMode(ProjectManifest project, Guid previousCueId, Guid nextCueId) =>
        VoicePhraseBoundaryModes.Normalize(project.VoicePhraseBoundaries
            .LastOrDefault(boundary => boundary.PreviousCueId == previousCueId
                && boundary.NextCueId == nextCueId)
            ?.Mode);

    public static string? GetManualJoinConstraint(
        ProjectManifest project,
        SubtitleCue previous,
        SubtitleCue next,
        double maximumDurationSeconds)
    {
        var maximumDurationMilliseconds = (long)Math.Round(
            Math.Clamp(
                double.IsFinite(maximumDurationSeconds) ? maximumDurationSeconds : 8,
                1,
                20) * 1_000);
        var gap = next.StartMilliseconds - previous.EndMilliseconds;
        return CanJoinUnderHardConstraints(
            project,
            [previous],
            next,
            gap,
            maximumDurationMilliseconds,
            out var reason)
                ? null
                : reason;
    }

    private static bool CanJoinUnderHardConstraints(
        ProjectManifest project,
        IReadOnlyList<SubtitleCue> current,
        SubtitleCue next,
        long gap,
        long maximumDurationMilliseconds,
        out string? reason)
    {
        var previous = current[^1];
        if (string.IsNullOrWhiteSpace(previous.TranslatedText)
            || string.IsNullOrWhiteSpace(next.TranslatedText))
        {
            reason = "Hai cue phải có bản dịch trước khi tạo cụm giọng.";
            return false;
        }

        if (gap < 0)
        {
            reason = "Hai cue đang chồng thời gian nên không thể tạo một cụm giọng liên tục.";
            return false;
        }

        if (gap > MaximumManualJoinGapMilliseconds)
        {
            reason = $"Khoảng nghỉ dài hơn {MaximumManualJoinGapMilliseconds / 1000d:0.#} giây.";
            return false;
        }

        if (next.EndMilliseconds - current[0].StartMilliseconds > maximumDurationMilliseconds)
        {
            reason = "Cụm thoại sẽ vượt giới hạn thời lượng an toàn.";
            return false;
        }

        var projectedCharacters = current.Sum(cue => cue.TranslatedText.Trim().Length)
            + current.Count
            + next.TranslatedText.Trim().Length;
        if (projectedCharacters > MaximumPhraseCharacters)
        {
            reason = $"Cụm thoại sẽ vượt {MaximumPhraseCharacters:N0} ký tự.";
            return false;
        }

        if (!string.Equals(
                previous.Speaker?.Trim(),
                next.Speaker?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "Hai cue thuộc speaker khác nhau.";
            return false;
        }

        var previousVoice = LocalVoiceCatalog.Resolve(project, previous);
        var nextVoice = LocalVoiceCatalog.Resolve(project, next);
        if (!string.Equals(previousVoice.VoiceId, nextVoice.VoiceId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Hai cue đang dùng giọng đọc khác nhau.";
            return false;
        }

        reason = null;
        return true;
    }

    private static VoicePhrasePlan Create(ProjectManifest project, IReadOnlyList<SubtitleCue> cues)
    {
        var voice = LocalVoiceCatalog.Resolve(project, cues[0]);
        var identity = string.Join('|', cues.Select(cue => cue.CueId.ToString("N")));
        var phraseId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..32];
        return new VoicePhrasePlan(
            phraseId,
            Guid.ParseExact(phraseId, "N"),
            cues.ToArray(),
            cues[0].StartMilliseconds,
            cues[^1].EndMilliseconds,
            cues[0].Speaker,
            voice.VoiceId,
            JoinForSynthesis(cues));
    }

    private static string JoinForSynthesis(IReadOnlyList<SubtitleCue> cues) =>
        string.Join(' ', cues.Select(cue => cue.TranslatedText.Trim()));

    private static bool EndsCompleteSentence(string value)
    {
        var span = value.AsSpan().TrimEnd();
        while (span.Length > 0 && span[^1] is '"' or '\'' or '’' or '”' or ')' or ']' or '}')
        {
            span = span[..^1].TrimEnd();
        }

        return span.Length > 0 && span[^1] is '.' or '?' or '!' or '…';
    }
}

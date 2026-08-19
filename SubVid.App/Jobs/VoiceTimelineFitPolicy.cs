using SubVid.App.Core;

namespace SubVid.App.Jobs;

public sealed record VoiceTimelineFitInput(
    double RawDurationSeconds,
    double PlayableDurationSeconds,
    double TargetDurationSeconds,
    double EffectiveWindowSeconds,
    int TranslatedCharacterCount,
    double MaximumAutomaticTempo = VoiceTimelineFitPolicy.DefaultMaximumAutomaticTempo,
    double PreferredAutomaticTempo = VoiceTimelineFitPolicy.DefaultPreferredAutomaticTempo,
    double LeadingSilenceSeconds = 0,
    double TrailingSilenceSeconds = 0,
    double TrimStartSeconds = 0,
    double? TrimEndSeconds = null,
    double BorrowedGapSeconds = 0,
    int BaseTtsSpeed = 0,
    string? PhraseId = null,
    DateTime? AnalyzedAtUtc = null);

public static class VoiceTimelineFitPolicy
{
    public const double DefaultPreferredAutomaticTempo = 1.12;
    public const double DefaultMaximumAutomaticTempo = 1.20;
    public const double MinimumMaximumAutomaticTempo = 1.0;
    public const double MaximumMaximumAutomaticTempo = 1.20;
    private const double DurationToleranceSeconds = 0.01;

    public static double NormalizeMaximumTempo(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinimumMaximumAutomaticTempo, MaximumMaximumAutomaticTempo)
            : DefaultMaximumAutomaticTempo;

    public static double NormalizePreferredTempo(double value, double maximumTempo)
    {
        var maximum = NormalizeMaximumTempo(maximumTempo);
        return double.IsFinite(value)
            ? Math.Clamp(value, MinimumMaximumAutomaticTempo, Math.Min(DefaultPreferredAutomaticTempo, maximum))
            : Math.Min(DefaultPreferredAutomaticTempo, maximum);
    }

    public static VoiceTimingAnalysis Analyze(
        double sourceDurationSeconds,
        double targetDurationSeconds,
        int translatedCharacterCount,
        double maximumAutomaticTempo = DefaultMaximumAutomaticTempo,
        DateTime? analyzedAtUtc = null) =>
        Analyze(new VoiceTimelineFitInput(
            sourceDurationSeconds,
            sourceDurationSeconds,
            targetDurationSeconds,
            targetDurationSeconds,
            translatedCharacterCount,
            maximumAutomaticTempo,
            Math.Min(DefaultPreferredAutomaticTempo, NormalizeMaximumTempo(maximumAutomaticTempo)),
            TrimEndSeconds: sourceDurationSeconds,
            AnalyzedAtUtc: analyzedAtUtc));

    public static VoiceTimingAnalysis Analyze(VoiceTimelineFitInput input)
    {
        var analyzedAt = input.AnalyzedAtUtc ?? DateTime.UtcNow;
        var maximumTempo = NormalizeMaximumTempo(input.MaximumAutomaticTempo);
        var preferredTempo = NormalizePreferredTempo(input.PreferredAutomaticTempo, maximumTempo);
        var rawDuration = input.RawDurationSeconds;
        var sourceDuration = input.PlayableDurationSeconds;
        var targetDuration = input.TargetDurationSeconds;
        var effectiveWindow = Math.Max(targetDuration, input.EffectiveWindowSeconds);
        if (!double.IsFinite(rawDuration)
            || !double.IsFinite(sourceDuration)
            || !double.IsFinite(targetDuration)
            || !double.IsFinite(effectiveWindow)
            || rawDuration <= 0
            || sourceDuration <= 0
            || targetDuration <= 0
            || effectiveWindow <= 0)
        {
            return CreateBase(input, analyzedAt) with
            {
                Status = VoiceTimingStatuses.Invalid,
                Severity = VoiceTimingSeverities.Error,
                Message = "Thời lượng WAV hoặc timeline không hợp lệ.",
                ResolutionAction = "REVIEW_DURATION",
            };
        }

        if (sourceDuration <= targetDuration + DurationToleranceSeconds)
        {
            var padding = Math.Max(0, targetDuration - sourceDuration);
            var padded = padding > DurationToleranceSeconds;
            return CreateBase(input, analyzedAt) with
            {
                RequiredTempo = sourceDuration / targetDuration,
                AppliedTempo = 1,
                PaddingSeconds = padding,
                EffectiveWindowSeconds = targetDuration,
                RenderDurationSeconds = targetDuration,
                BorrowedGapSeconds = 0,
                Status = padded ? VoiceTimingStatuses.Padded : VoiceTimingStatuses.Natural,
                Severity = VoiceTimingSeverities.Info,
                Message = padded
                    ? $"Giữ giọng tự nhiên 1.0x và nghỉ {padding:0.##} giây ở cuối."
                    : "Giọng khớp timeline ở tốc độ tự nhiên 1.0x.",
                ResolutionAction = padded ? "PAD_END" : "NONE",
            };
        }

        if (sourceDuration <= effectiveWindow + DurationToleranceSeconds)
        {
            var borrowed = Math.Min(
                Math.Max(0, sourceDuration - targetDuration),
                Math.Max(0, input.BorrowedGapSeconds));
            return CreateBase(input, analyzedAt) with
            {
                RequiredTempo = sourceDuration / effectiveWindow,
                AppliedTempo = 1,
                PaddingSeconds = 0,
                EffectiveWindowSeconds = effectiveWindow,
                RenderDurationSeconds = sourceDuration,
                BorrowedGapSeconds = borrowed,
                Status = VoiceTimingStatuses.GapFitted,
                Severity = VoiceTimingSeverities.Info,
                Message = $"Giữ giọng tự nhiên 1.0x và dùng thêm {borrowed:0.##} giây khoảng trống an toàn.",
                ResolutionAction = "BORROW_GAP",
            };
        }

        var requiredTempo = sourceDuration / effectiveWindow;
        if (requiredTempo <= maximumTempo + 0.000001)
        {
            var abovePreferred = requiredTempo > preferredTempo + 0.000001;
            return CreateBase(input, analyzedAt) with
            {
                RequiredTempo = requiredTempo,
                AppliedTempo = requiredTempo,
                EffectiveWindowSeconds = effectiveWindow,
                RenderDurationSeconds = effectiveWindow,
                BorrowedGapSeconds = Math.Max(0, input.BorrowedGapSeconds),
                Status = VoiceTimingStatuses.Compressed,
                Severity = abovePreferred
                    ? VoiceTimingSeverities.Warning
                    : VoiceTimingSeverities.Info,
                Message = abovePreferred
                    ? $"Cần tăng {requiredTempo:0.##}x. Mức này an toàn nhưng vượt ngưỡng tự nhiên ưu tiên {preferredTempo:0.##}x."
                    : $"Tăng nhẹ tốc độ lên {requiredTempo:0.##}x để khớp timeline.",
                ResolutionAction = abovePreferred ? "PREFER_SHORTER_TEXT" : "TIMELINE_TEMPO",
            };
        }

        int? safeCharacterCount = input.TranslatedCharacterCount > 0
            ? Math.Max(1, (int)Math.Floor(input.TranslatedCharacterCount * maximumTempo / requiredTempo))
            : null;
        return CreateBase(input, analyzedAt) with
        {
            RequiredTempo = requiredTempo,
            AppliedTempo = maximumTempo,
            EffectiveWindowSeconds = effectiveWindow,
            // Preserve the full sentence after applying the safest automatic
            // tempo. It may extend beyond the cue window, but is not truncated.
            RenderDurationSeconds = sourceDuration / maximumTempo,
            BorrowedGapSeconds = Math.Max(0, input.BorrowedGapSeconds),
            Status = VoiceTimingStatuses.ReviewRequired,
            Severity = VoiceTimingSeverities.Warning,
            Message = $"Câu cần tốc độ {requiredTempo:0.##}x, vượt giới hạn an toàn {maximumTempo:0.##}x. Đã tạo giọng ở {maximumTempo:0.##}x và giữ nguyên nội dung; audio có thể dài hơn khung phụ đề.",
            SuggestedMaximumCharacters = safeCharacterCount,
            ResolutionAction = "ALLOW_OVERFLOW",
        };
    }

    private static VoiceTimingAnalysis CreateBase(VoiceTimelineFitInput input, DateTime analyzedAt) => new()
    {
        RawDurationSeconds = double.IsFinite(input.RawDurationSeconds) ? Math.Max(0, input.RawDurationSeconds) : 0,
        SourceDurationSeconds = double.IsFinite(input.PlayableDurationSeconds) ? Math.Max(0, input.PlayableDurationSeconds) : 0,
        TargetDurationSeconds = double.IsFinite(input.TargetDurationSeconds) ? Math.Max(0, input.TargetDurationSeconds) : 0,
        EffectiveWindowSeconds = double.IsFinite(input.EffectiveWindowSeconds) ? Math.Max(0, input.EffectiveWindowSeconds) : 0,
        LeadingSilenceSeconds = double.IsFinite(input.LeadingSilenceSeconds) ? Math.Max(0, input.LeadingSilenceSeconds) : 0,
        TrailingSilenceSeconds = double.IsFinite(input.TrailingSilenceSeconds) ? Math.Max(0, input.TrailingSilenceSeconds) : 0,
        TrimStartSeconds = double.IsFinite(input.TrimStartSeconds) ? Math.Max(0, input.TrimStartSeconds) : 0,
        TrimEndSeconds = double.IsFinite(input.TrimEndSeconds ?? input.RawDurationSeconds)
            ? Math.Max(0, input.TrimEndSeconds ?? input.RawDurationSeconds)
            : 0,
        BorrowedGapSeconds = double.IsFinite(input.BorrowedGapSeconds) ? Math.Max(0, input.BorrowedGapSeconds) : 0,
        BaseTtsSpeed = Math.Clamp(input.BaseTtsSpeed, -3, 3),
        AppliedTtsSpeed = Math.Clamp(input.BaseTtsSpeed, -3, 3),
        PhraseId = input.PhraseId,
        AnalyzedAtUtc = analyzedAt,
    };
}

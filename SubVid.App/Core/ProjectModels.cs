using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubVid.App.Core;

public static class ProjectStates
{
    public const string Draft = "DRAFT";
    public const string Ready = "READY";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}

public sealed class ProjectManifest
{
    public int SchemaVersion { get; set; } = 1;

    public Guid ProjectId { get; set; }

    public Guid OwnerUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = ProjectStates.Draft;

    public string? SourceLanguageCode { get; set; }

    public string TargetLanguageCode { get; set; } = "vi";

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? LastOpenedAtUtc { get; set; }

    public bool LastCleanShutdown { get; set; } = true;

    [JsonIgnore]
    public bool RecoveryRequired { get; set; }

    public bool ServerSynchronized { get; set; }

    public LocalMediaReference? SourceVideo { get; set; }

    public List<LocalMediaReference> AudioTracks { get; set; } = [];

    public List<SubtitleDocument> SubtitleTracks { get; set; } = [];

    public List<VoicePhraseBoundaryOverride> VoicePhraseBoundaries { get; set; } = [];

    public ProjectSettings Settings { get; set; } = new();

    public ProjectTranslationContext TranslationContext { get; set; } = new();

    public List<TranslationGlossaryEntry> TranslationGlossary { get; set; } = [];

    public List<TranslationMemoryEntry> TranslationMemory { get; set; } = [];

    public List<LocalJob> Jobs { get; set; } = [];
}

public sealed class ProjectSettings
{
    public string SpeechModel { get; set; } = "whisper-balanced";

    public bool OcrEnabled { get; set; }

    public string OcrLanguageCode { get; set; } = "auto";

    public double OcrCropTopRatio { get; set; } = 0.60;

    public int OcrSampleIntervalMilliseconds { get; set; } = 500;

    public string TranslationTarget { get; set; } = "vi";

    public string TranslationModelId { get; set; } = "auto";

    public string TranslationProvider { get; set; } = TranslationProviders.Local;

    public string TranslationQualityMode { get; set; } = TranslationQualityModes.Balanced;

    public bool TranslationReviewEnabled { get; set; } = true;

    public bool TranslationFallbackToLocal { get; set; }

    public int TranslationContextCueCount { get; set; } = 3;

    public int TranslationSceneMaxCues { get; set; } = 12;

    public int TranslationSceneGapMilliseconds { get; set; } = 8000;

    public double TranslationMaxCharactersPerSecond { get; set; } = 18;

    public string? VoiceId { get; set; }

    public int VoiceSpeed { get; set; }

    public double VoiceTimelinePreferredTempo { get; set; } = 1.12;

    public double VoiceTimelineMaximumTempo { get; set; } = 1.20;

    public int VoiceTimelineMaximumBorrowMilliseconds { get; set; } = 600;

    public int VoiceTimelineMinimumGapMilliseconds { get; set; } = 90;

    public bool VoiceTrimSilenceEnabled { get; set; } = true;

    public int VoicePhraseGapMilliseconds { get; set; } = 500;

    public double VoicePhraseMaximumDurationSeconds { get; set; } = 8;

    public bool VoicePhraseSynthesisEnabled { get; set; } = true;

    public Dictionary<string, string> SpeakerVoiceIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool OriginalAudioEnabled { get; set; } = true;

    public double OriginalAudioVolumePercent { get; set; } = 85;

    public bool VietnameseVoiceEnabled { get; set; } = true;

    public double VietnameseVoiceVolumePercent { get; set; } = 100;

    public bool VietnameseSubtitlesEnabled { get; set; } = true;

    public string ExportContainer { get; set; } = "mp4";

    public string ExportVideoCodec { get; set; } = "h264";

    public bool FlipHorizontal { get; set; }

    public bool FlipVertical { get; set; }

    public bool RemoveOriginalSubtitles { get; set; }

    public string OriginalSubtitleRemovalMode { get; set; } = "blur";

    public double OriginalSubtitleRegionX { get; set; } = 0.05;

    public double OriginalSubtitleRegionY { get; set; } = 0.70;

    public double OriginalSubtitleRegionWidth { get; set; } = 0.90;

    public double OriginalSubtitleRegionHeight { get; set; } = 0.16;

    public List<SubtitleRemovalRegionSettings> OriginalSubtitleRemovalRegions { get; set; } = [];

    public SubtitleStyleSettings SubtitleStyle { get; set; } = new();
}

public sealed class SubtitleRemovalRegionSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public double X { get; set; } = 0.05;

    public double Y { get; set; } = 0.70;

    public double Width { get; set; } = 0.90;

    public double Height { get; set; } = 0.16;
}

public static class TranslationProviders
{
    public const string Local = "local";
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";
    public const string DeepSeek = "deepseek";
    public const string Groq = "groq";

    public static string Normalize(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        OpenAi => OpenAi,
        Gemini => Gemini,
        DeepSeek => DeepSeek,
        Groq => Groq,
        _ => Local,
    };

    public static bool IsCloud(string? provider) => Normalize(provider) is OpenAi or Gemini or DeepSeek or Groq;
}

public static class TranslationQualityModes
{
    public const string Fast = "fast";
    public const string Balanced = "balanced";
    public const string High = "high";

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Fast => Fast,
        High => High,
        _ => Balanced,
    };
}

public sealed class ProjectTranslationContext
{
    public string Summary { get; set; } = string.Empty;

    public string CharacterInstructions { get; set; } = string.Empty;

    public string StyleInstructions { get; set; } =
        "Tiếng Việt tự nhiên, rõ nghĩa, phù hợp lời thoại và không tự ý thêm thông tin.";
}

public sealed class TranslationGlossaryEntry
{
    public Guid EntryId { get; set; } = Guid.NewGuid();

    public string SourceText { get; set; } = string.Empty;

    public string TargetText { get; set; } = string.Empty;

    public string? Note { get; set; }
}

public sealed class TranslationMemoryEntry
{
    public Guid EntryId { get; set; } = Guid.NewGuid();

    public string SourceLanguageCode { get; set; } = string.Empty;

    public string TargetLanguageCode { get; set; } = "vi";

    public string SourceText { get; set; } = string.Empty;

    public string TranslatedText { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SubtitleStyleSettings
{
    public string PresetId { get; set; } = "readable";

    public string FontFamily { get; set; } = "Arial";

    public double FontSizePercent { get; set; } = 4.2;

    public bool Bold { get; set; } = true;

    public string TextColor { get; set; } = "#FFFFFF";

    public string OutlineColor { get; set; } = "#000000";

    public double OutlineSize { get; set; } = 1.2;

    public double ShadowSize { get; set; }

    public string BackgroundMode { get; set; } = "box";

    public string BackgroundColor { get; set; } = "#020617";

    public double BackgroundOpacity { get; set; } = 68;

    public string HorizontalAlignment { get; set; } = "center";

    public string VerticalPosition { get; set; } = "bottom";

    public double PositionXPercent { get; set; } = 50;

    public double PositionYPercent { get; set; } = 94;

    public double MaxWidthPercent { get; set; } = 90;

    public int MaxLines { get; set; } = 2;
}

public static class SubtitleStyleRules
{
    public static readonly IReadOnlySet<string> Presets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "readable", "outline", "tiktok", "cinematic", "yellow", "minimal", "custom",
    };

    public static readonly IReadOnlySet<string> Fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Arial", "Segoe UI", "Tahoma", "Verdana", "Times New Roman",
    };

    public static bool TryValidate(SubtitleStyleSettings? style, out string error)
    {
        error = string.Empty;
        if (style is null)
        {
            error = "Thiết lập kiểu phụ đề bị thiếu.";
            return false;
        }

        if (!Presets.Contains(style.PresetId)
            || !Fonts.Contains(style.FontFamily)
            || style.BackgroundMode is not ("none" or "box")
            || style.HorizontalAlignment is not ("left" or "center" or "right")
            || style.VerticalPosition is not ("top" or "middle" or "bottom" or "custom")
            || !IsHexColor(style.TextColor)
            || !IsHexColor(style.OutlineColor)
            || !IsHexColor(style.BackgroundColor)
            || !InRange(style.FontSizePercent, 1.5, 10)
            || !InRange(style.OutlineSize, 0, 8)
            || !InRange(style.ShadowSize, 0, 8)
            || !InRange(style.BackgroundOpacity, 0, 100)
            || !InRange(style.PositionXPercent, 0, 100)
            || !InRange(style.PositionYPercent, 0, 100)
            || !InRange(style.MaxWidthPercent, 35, 100)
            || style.MaxLines is < 1 or > 3)
        {
            error = "Thiết lập kiểu phụ đề nằm ngoài giới hạn cho phép.";
            return false;
        }

        return true;
    }

    public static SubtitleStyleSettings Normalize(SubtitleStyleSettings? style)
    {
        var source = style ?? new SubtitleStyleSettings();
        return new SubtitleStyleSettings
        {
            PresetId = Presets.Contains(source.PresetId) ? source.PresetId.ToLowerInvariant() : "readable",
            FontFamily = Fonts.Contains(source.FontFamily) ? source.FontFamily : "Arial",
            FontSizePercent = Clamp(source.FontSizePercent, 1.5, 10, 4.2),
            Bold = source.Bold,
            TextColor = NormalizeColor(source.TextColor, "#FFFFFF"),
            OutlineColor = NormalizeColor(source.OutlineColor, "#000000"),
            OutlineSize = Clamp(source.OutlineSize, 0, 8, 1.2),
            ShadowSize = Clamp(source.ShadowSize, 0, 8, 0),
            BackgroundMode = source.BackgroundMode is "none" or "box" ? source.BackgroundMode : "box",
            BackgroundColor = NormalizeColor(source.BackgroundColor, "#020617"),
            BackgroundOpacity = Clamp(source.BackgroundOpacity, 0, 100, 68),
            HorizontalAlignment = source.HorizontalAlignment is "left" or "center" or "right"
                ? source.HorizontalAlignment
                : "center",
            VerticalPosition = source.VerticalPosition is "top" or "middle" or "bottom" or "custom"
                ? source.VerticalPosition
                : "bottom",
            PositionXPercent = Clamp(source.PositionXPercent, 0, 100, 50),
            PositionYPercent = Clamp(source.PositionYPercent, 0, 100, 94),
            MaxWidthPercent = Clamp(source.MaxWidthPercent, 35, 100, 90),
            MaxLines = Math.Clamp(source.MaxLines, 1, 3),
        };
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static bool IsHexColor(string? value) =>
        value is { Length: 7 }
        && value[0] == '#'
        && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static string NormalizeColor(string? value, string fallback) =>
        IsHexColor(value) ? value!.ToUpperInvariant() : fallback;
}

public sealed class LocalMediaReference
{
    public Guid MediaId { get; set; } = Guid.NewGuid();

    public Guid? CueId { get; set; }

    public List<Guid> CueIds { get; set; } = [];

    public string? VoicePhraseId { get; set; }

    public string Role { get; set; } = "SOURCE_VIDEO";

    public string ImportMode { get; set; } = "LINK";

    public string OriginalPath { get; set; } = string.Empty;

    public string? WorkspaceRelativePath { get; set; }

    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string? ContentFingerprint { get; set; }

    public bool IsStale { get; set; }

    public DateTime SourceLastWriteAtUtc { get; set; }

    public MediaMetadata Metadata { get; set; } = new();
}

public sealed class MediaMetadata
{
    public double DurationSeconds { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public double FramesPerSecond { get; set; }

    public string? VideoCodec { get; set; }

    public string? AudioCodec { get; set; }

    public int AudioTrackCount { get; set; }

    public int? AudioSampleRate { get; set; }

    public int? AudioChannels { get; set; }

    public long? BitRate { get; set; }

    public int RotationDegrees { get; set; }

    public bool HasVideo { get; set; }

    public bool HasAudio { get; set; }

    public bool IsVariableFrameRate { get; set; }

    public string Container { get; set; } = string.Empty;
}

public sealed class SubtitleDocument
{
    public Guid TrackId { get; set; } = Guid.NewGuid();

    public string LanguageCode { get; set; } = "vi";

    public string Source { get; set; } = "TRANSCRIPTION";

    public List<SubtitleCue> Cues { get; set; } = [];
}

public sealed class SubtitleCue
{
    public Guid CueId { get; set; } = Guid.NewGuid();

    public long StartMilliseconds { get; set; }

    public long EndMilliseconds { get; set; }

    public string Speaker { get; set; } = "speaker_1";

    public string? VoiceId { get; set; }

    public string OriginalText { get; set; } = string.Empty;

    public string TranslatedText { get; set; } = string.Empty;

    public string? TranslationModelId { get; set; }

    public string? TranslationModelVersion { get; set; }

    public string? TranslationSourceFingerprint { get; set; }

    public string? TranslationQualityStatus { get; set; }

    public double? TranslationConfidence { get; set; }

    public List<string> TranslationWarnings { get; set; } = [];

    public DateTime? TranslationReviewedAtUtc { get; set; }

    public bool OriginalLocked { get; set; }

    public bool TranslationLocked { get; set; }

    public VoiceTimingAnalysis? VoiceTiming { get; set; }
}

public static class VoicePhraseBoundaryModes
{
    public const string Auto = "AUTO";
    public const string Join = "JOIN";
    public const string Break = "BREAK";

    public static string Normalize(string? mode) => mode?.Trim().ToUpperInvariant() switch
    {
        Join => Join,
        Break => Break,
        _ => Auto,
    };
}

public sealed class VoicePhraseBoundaryOverride
{
    public Guid PreviousCueId { get; set; }

    public Guid NextCueId { get; set; }

    public string Mode { get; set; } = VoicePhraseBoundaryModes.Break;
}

public static class VoiceTimingStatuses
{
    public const string Natural = "NATURAL";
    public const string Padded = "PADDED";
    public const string GapFitted = "GAP_FITTED";
    public const string Compressed = "COMPRESSED";
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public const string Invalid = "INVALID";
}

public static class VoiceTimingSeverities
{
    public const string Info = "INFO";
    public const string Warning = "WARNING";
    public const string Error = "ERROR";
}

public sealed record class VoiceTimingAnalysis
{
    public double RawDurationSeconds { get; set; }

    public double SourceDurationSeconds { get; set; }

    public double TargetDurationSeconds { get; set; }

    public double EffectiveWindowSeconds { get; set; }

    public double RenderDurationSeconds { get; set; }

    public double LeadingSilenceSeconds { get; set; }

    public double TrailingSilenceSeconds { get; set; }

    public double TrimStartSeconds { get; set; }

    public double TrimEndSeconds { get; set; }

    public double BorrowedGapSeconds { get; set; }

    public double RequiredTempo { get; set; }

    public double? AppliedTempo { get; set; }

    public double PaddingSeconds { get; set; }

    public int BaseTtsSpeed { get; set; }

    public int AppliedTtsSpeed { get; set; }

    public string? PhraseId { get; set; }

    public string ResolutionAction { get; set; } = "NONE";

    public string Status { get; set; } = VoiceTimingStatuses.Invalid;

    public string Severity { get; set; } = VoiceTimingSeverities.Error;

    public string Message { get; set; } = string.Empty;

    public int? SuggestedMaximumCharacters { get; set; }

    public DateTime AnalyzedAtUtc { get; set; } = DateTime.UtcNow;
}

[JsonConverter(typeof(LocalJobStatusJsonConverter))]
public enum LocalJobStatus
{
    Pending,
    Running,
    Paused,
    Interrupted,
    Completed,
    Failed,
    Cancelled,
}

public sealed class LocalJobStatusJsonConverter()
    : JsonStringEnumConverter<LocalJobStatus>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

public sealed class LocalJob
{
    public Guid JobId { get; set; } = Guid.NewGuid();

    public string JobType { get; set; } = "FULL_PIPELINE";

    public LocalJobStatus Status { get; set; } = LocalJobStatus.Pending;

    public double ProgressPercent { get; set; }

    public int AttemptCount { get; set; }

    public int MaxAttempts { get; set; } = 3;

    public string? CurrentStep { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public Guid? QuotaReservationId { get; set; }

    public DateTime? QuotaReservationExpiresAtUtc { get; set; }

    public decimal? QuotaEstimatedMinutes { get; set; }

    public string? QuotaSettlementStatus { get; set; }

    public string? QuotaSettlementError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public List<LocalJobStep> Steps { get; set; } = [];

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TranslationJobMetrics? TranslationMetrics { get; set; }

    public VoiceSynthesisJobMetrics? VoiceMetrics { get; set; }

    public List<CloudUsageSettlement> CloudSettlements { get; set; } = [];
}

public sealed class CloudUsageSettlement
{
    public Guid RequestId { get; set; } = Guid.NewGuid();

    public Guid? ReservationId { get; set; }

    public string ProviderCode { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public string UnitCode { get; set; } = "LLM_TOKEN";

    public string Status { get; set; } = "AUTHORIZING";

    public long EstimatedInputUnits { get; set; }

    public long EstimatedOutputUnits { get; set; }

    public long ActualInputUnits { get; set; }

    public long ActualOutputUnits { get; set; }

    public long CachedInputUnits { get; set; }

    public int ApiRequests { get; set; }

    public int RetryRequests { get; set; }

    public bool UsageWasEstimated { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class VoiceSynthesisJobMetrics
{
    public int TotalCharacters { get; set; }

    public int SubmittedCharacters { get; set; }

    public int ApiRequests { get; set; }

    public int RetryRequests { get; set; }

    public int CacheHitCues { get; set; }

    public int CompletedCues { get; set; }

    public int TotalCues { get; set; }

    public int TimingWarningCues { get; set; }
}

public sealed class TranslationJobMetrics
{
    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long CachedInputTokens { get; set; }

    public int ApiRequests { get; set; }

    public int RetryRequests { get; set; }

    public int CacheHitScenes { get; set; }

    public int TranslatedScenes { get; set; }

    public int ReviewedCues { get; set; }

    public int AutoRepairedCues { get; set; }

    public int SkippedCues { get; set; }

    public int CompletedCues { get; set; }

    public int TotalPendingCues { get; set; }
}

public sealed class LocalJobStep
{
    public string Code { get; set; } = string.Empty;

    public LocalJobStatus Status { get; set; } = LocalJobStatus.Pending;

    public double ProgressPercent { get; set; }

    public int AttemptCount { get; set; }

    public string? OutputRelativePath { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}

public sealed record ProjectSummary(
    Guid ProjectId,
    string Name,
    string Status,
    DateTime UpdatedAtUtc,
    bool NeedsRecovery,
    string? SourceFileName,
    double? DurationSeconds);

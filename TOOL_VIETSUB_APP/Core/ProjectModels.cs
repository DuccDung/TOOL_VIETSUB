using System.Text.Json;
using System.Text.Json.Serialization;

namespace TOOL_VIETSUB_APP.Core;

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

    public ProjectSettings Settings { get; set; } = new();

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

    public string? VoiceId { get; set; }

    public bool OriginalAudioEnabled { get; set; } = true;

    public double OriginalAudioVolumePercent { get; set; } = 85;

    public bool VietnameseVoiceEnabled { get; set; } = true;

    public double VietnameseVoiceVolumePercent { get; set; } = 100;

    public string ExportContainer { get; set; } = "mp4";

    public string ExportVideoCodec { get; set; } = "h264";

    public bool RemoveOriginalSubtitles { get; set; }

    public string OriginalSubtitleRemovalMode { get; set; } = "blur";

    public double OriginalSubtitleRegionX { get; set; } = 0.05;

    public double OriginalSubtitleRegionY { get; set; } = 0.70;

    public double OriginalSubtitleRegionWidth { get; set; } = 0.90;

    public double OriginalSubtitleRegionHeight { get; set; } = 0.16;

    public SubtitleStyleSettings SubtitleStyle { get; set; } = new();
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

    public string Role { get; set; } = "SOURCE_VIDEO";

    public string ImportMode { get; set; } = "LINK";

    public string OriginalPath { get; set; } = string.Empty;

    public string? WorkspaceRelativePath { get; set; }

    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string? ContentFingerprint { get; set; }

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

    public string OriginalText { get; set; } = string.Empty;

    public string TranslatedText { get; set; } = string.Empty;

    public string? TranslationModelId { get; set; }

    public string? TranslationModelVersion { get; set; }

    public string? TranslationSourceFingerprint { get; set; }

    public string? TranslationQualityStatus { get; set; }

    public bool OriginalLocked { get; set; }

    public bool TranslationLocked { get; set; }
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

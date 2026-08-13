namespace TOOL_VIETSUB_APP.Core;

public sealed record DesktopVideoInfo(
    string FileName,
    string Extension,
    long SizeBytes,
    double DurationSeconds,
    int Width,
    int Height,
    double FramesPerSecond,
    string? VideoCodec,
    string? AudioCodec,
    int AudioTrackCount,
    bool HasAudio,
    string ImportMode,
    string Sha256,
    string PlaybackUrl);

public sealed record DesktopProjectState(
    Guid ProjectId,
    string Name,
    string Status,
    bool NeedsRecovery,
    bool ServerSynchronized,
    DateTime UpdatedAtUtc,
    string SourceLanguageCode,
    string TargetLanguageCode,
    DesktopProjectSettings Settings,
    DesktopVideoInfo? Video,
    string? VoicePlaybackUrl,
    IReadOnlyList<DesktopSubtitleCue> Subtitles,
    IReadOnlyList<LocalJob> Jobs);

public sealed record DesktopProjectSettings(
    string SpeechModel,
    string OcrLanguageCode,
    string TranslationModelId,
    bool OriginalAudioEnabled,
    double OriginalAudioVolumePercent,
    bool VietnameseVoiceEnabled,
    double VietnameseVoiceVolumePercent,
    bool RemoveOriginalSubtitles,
    string OriginalSubtitleRemovalMode,
    double OriginalSubtitleRegionX,
    double OriginalSubtitleRegionY,
    double OriginalSubtitleRegionWidth,
    double OriginalSubtitleRegionHeight,
    DesktopSubtitleStyleSettings SubtitleStyle);

public sealed record DesktopSubtitleStyleSettings(
    string PresetId,
    string FontFamily,
    double FontSizePercent,
    bool Bold,
    string TextColor,
    string OutlineColor,
    double OutlineSize,
    double ShadowSize,
    string BackgroundMode,
    string BackgroundColor,
    double BackgroundOpacity,
    string HorizontalAlignment,
    string VerticalPosition,
    double PositionXPercent,
    double PositionYPercent,
    double MaxWidthPercent,
    int MaxLines);

public sealed record DesktopSubtitleCue(
    Guid CueId,
    int Id,
    double Start,
    double End,
    string Original,
    string Translated,
    string Status,
    bool OverlapsPrevious,
    bool HasVoice);

public sealed record WorkspaceOperationResult<T>(
    bool Succeeded,
    T? Value = default,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static WorkspaceOperationResult<T> Success(T value) => new(true, value);

    public static WorkspaceOperationResult<T> Failure(string code, string message) =>
        new(false, ErrorCode: code, ErrorMessage: message);
}

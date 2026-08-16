namespace SubVid.App.Core;

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
    DesktopAiStorageInfo AiStorage,
    DesktopVideoInfo? Video,
    string? VoicePlaybackUrl,
    IReadOnlyList<DesktopSubtitleCue> Subtitles,
    IReadOnlyList<LocalJob> Jobs);

public sealed record DesktopAiStorageInfo(
    string RootPath,
    long FreeBytes,
    bool UsesLegacyLocation,
    string RecommendedPath,
    string? PendingMigrationPath);

public sealed record DesktopProjectSettings(
    string SpeechModel,
    string OcrLanguageCode,
    string TranslationModelId,
    DesktopTranslationSettings Translation,
    DesktopVoiceSettings Voice,
    bool OriginalAudioEnabled,
    double OriginalAudioVolumePercent,
    bool VietnameseVoiceEnabled,
    double VietnameseVoiceVolumePercent,
    bool VietnameseSubtitlesEnabled,
    bool FlipHorizontal,
    bool FlipVertical,
    bool RemoveOriginalSubtitles,
    string OriginalSubtitleRemovalMode,
    double OriginalSubtitleRegionX,
    double OriginalSubtitleRegionY,
    double OriginalSubtitleRegionWidth,
    double OriginalSubtitleRegionHeight,
    IReadOnlyList<DesktopSubtitleRemovalRegion> OriginalSubtitleRemovalRegions,
    DesktopSubtitleStyleSettings SubtitleStyle);

public sealed record DesktopSubtitleRemovalRegion(
    string Id,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record DesktopVoiceSettings(
    string DefaultVoiceId,
    IReadOnlyDictionary<string, string> SpeakerVoiceIds,
    IReadOnlyList<DesktopVoiceInfo> Voices,
    int Speed = 0,
    bool FptApiKeyConfigured = false,
    int EstimatedCharacters = 0);

public sealed record DesktopVoiceInfo(
    string VoiceId,
    string Engine,
    string DisplayName,
    string Gender,
    string Region,
    string Style,
    string ModelVersion,
    string License,
    bool Installed,
    bool IsCloud = false,
    bool RequiresInstall = true);

public sealed record DesktopTranslationSettings(
    string Provider,
    string ModelId,
    string QualityMode,
    bool ReviewEnabled,
    bool FallbackToLocal,
    bool ApiKeyConfigured,
    string ProjectContext,
    string CharacterInstructions,
    string StyleInstructions,
    string GlossaryText,
    int TranslationMemoryCount);

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
    string Speaker,
    string? VoiceId,
    string ResolvedVoiceId,
    string Status,
    bool OverlapsPrevious,
    bool HasVoice,
    double? TranslationConfidence,
    IReadOnlyList<string> TranslationWarnings);

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

using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Translation;

public enum TranslationPass
{
    Translate,
    Review,
}

public static class TranslationRunModes
{
    public const string Continue = "continue";
    public const string Restart = "restart";
    public const string ParameterName = "translationRunMode";
    public const string RestartPreparedParameterName = "translationRestartPrepared";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Restart, StringComparison.OrdinalIgnoreCase)
            ? Restart
            : Continue;
}

public sealed record TranslationCueInput(
    Guid CueId,
    long StartMilliseconds,
    long EndMilliseconds,
    string Speaker,
    string OriginalText,
    bool IsTarget,
    int SuggestedMaximumCharacters,
    string? CandidateTranslation = null);

public sealed record TranslationSceneRequest(
    string ProjectName,
    string SourceLanguage,
    string TargetLanguage,
    string ProjectSummary,
    string CharacterInstructions,
    string StyleInstructions,
    IReadOnlyList<TranslationGlossaryEntry> Glossary,
    IReadOnlyList<TranslationMemoryEntry> TranslationMemory,
    IReadOnlyList<TranslationCueInput> Cues,
    TranslationPass Pass)
{
    public IReadOnlyList<Guid> TargetCueIds => Cues
        .Where(cue => cue.IsTarget)
        .Select(cue => cue.CueId)
        .ToArray();
}

public sealed record TranslationItemResult(
    Guid CueId,
    string TranslatedText,
    double Confidence,
    IReadOnlyList<string> Warnings,
    bool WasReviewed = false,
    bool WasAutoRepaired = false);

public sealed record TranslationUsage(
    long InputTokens = 0,
    long OutputTokens = 0,
    long CachedInputTokens = 0,
    int ApiRequests = 0,
    int RetryRequests = 0)
{
    public static TranslationUsage Empty { get; } = new();

    public TranslationUsage Add(TranslationUsage? other) => other is null
        ? this
        : new TranslationUsage(
            InputTokens + Math.Max(0, other.InputTokens),
            OutputTokens + Math.Max(0, other.OutputTokens),
            CachedInputTokens + Math.Max(0, other.CachedInputTokens),
            ApiRequests + Math.Max(0, other.ApiRequests),
            RetryRequests + Math.Max(0, other.RetryRequests));
}

public sealed record TranslationSceneResult(
    string ProviderId,
    string ModelId,
    string ModelVersion,
    IReadOnlyList<TranslationItemResult> Items,
    TranslationUsage? Usage = null);

public interface ITranslationProvider
{
    string ProviderId { get; }

    string ModelId { get; }

    bool SupportsContextualReview { get; }

    Task<TranslationSceneResult> TranslateAsync(
        TranslationSceneRequest request,
        CancellationToken cancellationToken);
}

public sealed class TranslationProviderException(
    string code,
    string message,
    bool retryable = true,
    Exception? innerException = null,
    TimeSpan? retryAfter = null,
    TranslationUsage? usage = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public TranslationUsage? Usage { get; } = usage;
}

public static class TranslationModelDefaults
{
    public static string Resolve(
        string provider,
        string? configuredModel,
        string qualityMode,
        string sourceLanguage)
    {
        var normalizedProvider = TranslationProviders.Normalize(provider);
        var configured = configuredModel?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        return normalizedProvider switch
        {
            TranslationProviders.OpenAi => TranslationQualityModes.Normalize(qualityMode) switch
            {
                TranslationQualityModes.Fast => "gpt-5.6-luna",
                TranslationQualityModes.High => "gpt-5.6-sol",
                _ => "gpt-5.6-terra",
            },
            TranslationProviders.Gemini => TranslationQualityModes.Normalize(qualityMode) == TranslationQualityModes.High
                ? "gemini-3.1-pro-preview"
                : "gemini-3.6-flash",
            TranslationProviders.DeepSeek => TranslationQualityModes.Normalize(qualityMode) == TranslationQualityModes.High
                ? "deepseek-v4-pro"
                : "deepseek-v4-flash",
            TranslationProviders.Groq => TranslationQualityModes.Normalize(qualityMode) == TranslationQualityModes.Fast
                ? "openai/gpt-oss-20b"
                : "openai/gpt-oss-120b",
            _ => TOOL_VIETSUB_APP.LocalAi.LocalTranslatorFactory.GetModelId(sourceLanguage),
        };
    }
}

public static class TranslationSceneLimits
{
    public const int GroqFreeTierMaximumTargetCues = 8;
    public const int CloudMaximumContextCues = 2;

    public static int ResolveMaximumTargetCues(string provider, int configuredMaximum)
    {
        var normalizedMaximum = Math.Clamp(configuredMaximum, 1, 30);
        return TranslationProviders.Normalize(provider) == TranslationProviders.Groq
            ? Math.Min(normalizedMaximum, GroqFreeTierMaximumTargetCues)
            : normalizedMaximum;
    }

    public static int ResolveContextCueCount(string provider, int configuredCount)
    {
        var normalized = Math.Clamp(configuredCount, 0, 10);
        return TranslationProviders.IsCloud(provider)
            ? Math.Min(normalized, CloudMaximumContextCues)
            : normalized;
    }
}

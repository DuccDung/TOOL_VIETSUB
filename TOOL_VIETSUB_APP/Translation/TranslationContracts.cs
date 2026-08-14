using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Translation;

public enum TranslationPass
{
    Translate,
    Review,
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
    IReadOnlyList<string> Warnings);

public sealed record TranslationSceneResult(
    string ProviderId,
    string ModelId,
    string ModelVersion,
    IReadOnlyList<TranslationItemResult> Items);

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
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
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
            _ => TOOL_VIETSUB_APP.LocalAi.LocalTranslatorFactory.GetModelId(sourceLanguage),
        };
    }
}

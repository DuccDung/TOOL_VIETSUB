using TOOL_VIETSUB_APP.LocalAi;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Translation;

public sealed class LocalTranslationProviderAdapter : ITranslationProvider
{
    private readonly ILocalTranslator _translator;
    private readonly string _modelVersion;
    private readonly LocalTranslationContextResolver _context = new();

    public LocalTranslationProviderAdapter(
        ILocalTranslator translator,
        string modelId,
        string? modelVersion = null)
    {
        _translator = translator;
        ModelId = modelId;
        _modelVersion = string.IsNullOrWhiteSpace(modelVersion) ? modelId : modelVersion;
    }

    public string ProviderId => TranslationProviders.Local;

    public string ModelId { get; }

    public bool SupportsContextualReview => false;

    public async Task<TranslationSceneResult> TranslateAsync(
        TranslationSceneRequest request,
        CancellationToken cancellationToken)
    {
        var targets = request.Cues.Where(cue => cue.IsTarget).ToArray();
        if (targets.Length == 0)
        {
            return new TranslationSceneResult(ProviderId, ModelId, _modelVersion, []);
        }

        if (request.Pass == TranslationPass.Review)
        {
            return new TranslationSceneResult(
                ProviderId,
                ModelId,
                _modelVersion,
                targets.Select(cue => new TranslationItemResult(
                    cue.CueId,
                    cue.CandidateTranslation ?? string.Empty,
                    0.5,
                    ["LOCAL_NO_CONTEXTUAL_REVIEW"])).ToArray());
        }

        var exactMatches = _context.BuildExactMatches(request);
        var results = new Dictionary<Guid, TranslationItemResult>();
        foreach (var target in targets)
        {
            var key = LocalTranslationContextResolver.NormalizeKey(target.OriginalText);
            if (exactMatches.TryGetValue(key, out var match))
            {
                results[target.CueId] = new TranslationItemResult(
                    target.CueId,
                    match.Text,
                    match.Confidence,
                    []);
            }
        }

        var unresolved = targets
            .Where(cue => !results.ContainsKey(cue.CueId))
            .GroupBy(
                cue => LocalTranslationContextResolver.NormalizeKey(cue.OriginalText),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var modelCues = unresolved.Select(group => group.First()).ToArray();
        IReadOnlyList<string> translations = [];
        if (modelCues.Length > 0)
        {
            translations = await _translator.TranslateAsync(
                modelCues.Select(cue => cue.OriginalText).ToArray(),
                request.SourceLanguage,
                request.TargetLanguage,
                cancellationToken);
            if (translations.Count != modelCues.Length)
            {
                throw new TranslationProviderException(
                    "TRANSLATION_RESULT_INVALID",
                    "Local translator returned an invalid number of translations.");
            }
        }

        for (var index = 0; index < modelCues.Length; index++)
        {
            var source = modelCues[index];
            var translation = _context.ApplyGlossary(
                source.OriginalText,
                translations[index],
                request.Glossary);
            if (string.IsNullOrWhiteSpace(translation))
            {
                throw new TranslationProviderException(
                    "TRANSLATION_RESULT_INVALID",
                    "Local translator returned an empty translation.");
            }

            _context.Remember(source.OriginalText, translation);
            foreach (var target in unresolved[index])
            {
                results[target.CueId] = new TranslationItemResult(
                    target.CueId,
                    translation,
                    0.72,
                    []);
            }
        }

        return new TranslationSceneResult(
            ProviderId,
            ModelId,
            _modelVersion,
            targets.Select(target => results[target.CueId]).ToArray());
    }
}

public sealed class FallbackTranslationProvider(
    ITranslationProvider primary,
    ITranslationProvider fallback)
    : ITranslationProvider
{
    public string ProviderId => primary.ProviderId;

    public string ModelId => primary.ModelId;

    public bool SupportsContextualReview => primary.SupportsContextualReview;

    public async Task<TranslationSceneResult> TranslateAsync(
        TranslationSceneRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await primary.TranslateAsync(request, cancellationToken);
        }
        catch (TranslationProviderException exception) when (
            exception.Retryable && request.Pass == TranslationPass.Review)
        {
            return new TranslationSceneResult(
                primary.ProviderId,
                primary.ModelId,
                primary.ModelId,
                request.Cues.Where(cue => cue.IsTarget).Select(cue => new TranslationItemResult(
                    cue.CueId,
                    cue.CandidateTranslation ?? string.Empty,
                    0.65,
                    ["CLOUD_REVIEW_UNAVAILABLE"])).ToArray(),
                exception.Usage);
        }
        catch (TranslationProviderException exception) when (exception.Retryable)
        {
            var fallbackResult = await fallback.TranslateAsync(request, cancellationToken);
            return fallbackResult with
            {
                Usage = (exception.Usage ?? TranslationUsage.Empty).Add(fallbackResult.Usage),
            };
        }
    }
}

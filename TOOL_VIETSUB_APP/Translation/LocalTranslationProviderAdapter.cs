using TOOL_VIETSUB_APP.LocalAi;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Translation;

public sealed class LocalTranslationProviderAdapter : ITranslationProvider
{
    private readonly ILocalTranslator _translator;
    private readonly string _modelVersion;

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

        var translations = await _translator.TranslateAsync(
            targets.Select(cue => cue.OriginalText).ToArray(),
            request.SourceLanguage,
            request.TargetLanguage,
            cancellationToken);
        if (translations.Count != targets.Length)
        {
            throw new TranslationProviderException(
                "TRANSLATION_RESULT_INVALID",
                "Model local trả về số lượng bản dịch không hợp lệ.");
        }

        return new TranslationSceneResult(
            ProviderId,
            ModelId,
            _modelVersion,
            translations.Select((text, index) => new TranslationItemResult(
                targets[index].CueId,
                text.Trim(),
                0.5,
                ["LOCAL_TRANSLATION"])).ToArray());
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
                    ["CLOUD_REVIEW_UNAVAILABLE"])).ToArray());
        }
        catch (TranslationProviderException exception) when (exception.Retryable)
        {
            return await fallback.TranslateAsync(request, cancellationToken);
        }
    }
}

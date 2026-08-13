using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.LocalAi;

public interface ILocalTranslator
{
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}

public static class LocalLanguageCodes
{
    public static string? NormalizeSource(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        var normalized = languageCode.Trim().ToLowerInvariant().Replace('_', '-');
        if (normalized is "auto" or "und")
        {
            return null;
        }

        if (normalized is "zh" or "cmn" || normalized.StartsWith("zh-", StringComparison.Ordinal))
        {
            return "zh";
        }

        if (normalized is "en" || normalized.StartsWith("en-", StringComparison.Ordinal))
        {
            return "en";
        }

        return normalized;
    }

    public static string NormalizeSetting(string? languageCode)
    {
        var normalized = NormalizeSource(languageCode);
        if (normalized is null)
        {
            return "auto";
        }

        if (normalized is "en" or "zh")
        {
            return normalized;
        }

        throw new LocalModelException(
            "SOURCE_LANGUAGE_UNSUPPORTED",
            "Hiện tại ứng dụng hỗ trợ nguồn tiếng Trung hoặc tiếng Anh để dịch sang tiếng Việt.");
    }

    public static string? ResolveProjectSource(ProjectManifest project)
    {
        var configured = NormalizeSource(project.SourceLanguageCode);
        if (configured is not null)
        {
            return configured;
        }

        return project.SubtitleTracks
            .LastOrDefault(track => track.Cues.Count > 0) is { } track
            ? NormalizeSource(track.LanguageCode)
            : null;
    }
}

public static class LocalTranslatorFactory
{
    public static string GetModelId(string sourceLanguage) =>
        LocalLanguageCodes.NormalizeSource(sourceLanguage) switch
        {
            "en" => ArgosLocalTranslator.ModelId,
            "zh" => OpusMtChineseVietnameseTranslator.ModelId,
            _ => throw new LocalModelException(
                "TRANSLATION_PAIR_UNSUPPORTED",
                "Hiện tại ứng dụng hỗ trợ dịch tiếng Trung hoặc tiếng Anh sang tiếng Việt."),
        };

    public static ILocalTranslator Create(
        string sourceLanguage,
        AppPaths paths,
        LocalModelManager models) =>
        LocalLanguageCodes.NormalizeSource(sourceLanguage) switch
        {
            "en" => new ArgosLocalTranslator(paths, models),
            "zh" => new OpusMtChineseVietnameseTranslator(paths, models),
            _ => throw new LocalModelException(
                "TRANSLATION_PAIR_UNSUPPORTED",
                "Hiện tại ứng dụng hỗ trợ dịch tiếng Trung hoặc tiếng Anh sang tiếng Việt."),
        };
}

public sealed class ArgosLocalTranslator : ILocalTranslator
{
    public const string ModelId = "argos-en-vi";
    public static readonly string PackageRelativePath = Path.Combine("argos", "translate-en_vi-1_9.argosmodel");

    private readonly AppPaths _paths;
    private readonly LocalModelManager _models;
    private readonly LocalWorkerProcess _worker;
    private readonly LocalWorkerRuntimeLocator _runtime;

    public ArgosLocalTranslator(
        AppPaths paths,
        LocalModelManager models,
        LocalWorkerProcess? worker = null)
    {
        _paths = paths;
        _models = models;
        _worker = worker ?? new LocalWorkerProcess();
        _runtime = new LocalWorkerRuntimeLocator(paths);
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(sourceLanguage, "en", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(targetLanguage, "vi", StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalModelException(
                "TRANSLATION_PAIR_UNSUPPORTED",
                "Bản local hiện hỗ trợ dịch từ tiếng Anh sang tiếng Việt.");
        }

        if (texts.Count == 0)
        {
            return [];
        }

        var response = await _worker.RunAsync<ArgosWorkerResponse>(
            _runtime.RequirePython(),
            _runtime.RequireWorker("argos_worker.py"),
            new
            {
                packagePath = _models.RequireFile(ModelId, PackageRelativePath),
                packageDirectory = _paths.GetModelPath("argos", "installed"),
                sourceLanguage,
                targetLanguage,
                texts,
            },
            TimeSpan.FromMinutes(10),
            cancellationToken);
        if (response.Translations.Count != texts.Count
            || response.Translations.Any(string.IsNullOrWhiteSpace))
        {
            throw new LocalModelException(
                "TRANSLATION_RESULT_INVALID",
                "Argos trả về số lượng bản dịch không hợp lệ.");
        }

        return response.Translations;
    }

    private sealed record ArgosWorkerResponse(IReadOnlyList<string> Translations);
}

public sealed class OpusMtChineseVietnameseTranslator : ILocalTranslator
{
    public const string ModelId = "opus-mt-zh-vi-official-v2";
    public const string ModelVersion = "67ea2dbfbaf13a16772a40346d3d72b59e591443";
    private static readonly string ModelDirectoryRelativePath = Path.Combine("transformers", "opus-mt-zh-vi-official");
    public static readonly string ConfigRelativePath = Path.Combine(ModelDirectoryRelativePath, "config.json");
    public static readonly string GenerationConfigRelativePath = Path.Combine(ModelDirectoryRelativePath, "generation_config.json");
    public static readonly string ModelRelativePath = Path.Combine(ModelDirectoryRelativePath, "pytorch_model.bin");
    public static readonly string VocabularyRelativePath = Path.Combine(ModelDirectoryRelativePath, "vocab.json");
    public static readonly string TokenizerConfigRelativePath = Path.Combine(ModelDirectoryRelativePath, "tokenizer_config.json");
    public static readonly string SourceSentencePieceRelativePath = Path.Combine(ModelDirectoryRelativePath, "source.spm");
    public static readonly string TargetSentencePieceRelativePath = Path.Combine(ModelDirectoryRelativePath, "target.spm");

    private readonly LocalModelManager _models;
    private readonly LocalWorkerProcess _worker;
    private readonly LocalWorkerRuntimeLocator _runtime;

    public OpusMtChineseVietnameseTranslator(
        AppPaths paths,
        LocalModelManager models,
        LocalWorkerProcess? worker = null)
    {
        _models = models;
        _worker = worker ?? new LocalWorkerProcess();
        _runtime = new LocalWorkerRuntimeLocator(paths);
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (LocalLanguageCodes.NormalizeSource(sourceLanguage) != "zh"
            || !string.Equals(targetLanguage, "vi", StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalModelException(
                "TRANSLATION_PAIR_UNSUPPORTED",
                "Model OPUS-MT này chỉ hỗ trợ dịch tiếng Trung sang tiếng Việt.");
        }

        if (texts.Count == 0)
        {
            return [];
        }

        var configPath = _models.RequireFile(ModelId, ConfigRelativePath);
        _ = _models.RequireFile(ModelId, GenerationConfigRelativePath);
        _ = _models.RequireFile(ModelId, ModelRelativePath);
        _ = _models.RequireFile(ModelId, VocabularyRelativePath);
        _ = _models.RequireFile(ModelId, TokenizerConfigRelativePath);
        _ = _models.RequireFile(ModelId, SourceSentencePieceRelativePath);
        _ = _models.RequireFile(ModelId, TargetSentencePieceRelativePath);
        var response = await _worker.RunAsync<TransformersTranslationWorkerResponse>(
            _runtime.RequirePython(),
            _runtime.RequireWorker("transformers_translation_worker.py"),
            new
            {
                modelDirectory = Path.GetDirectoryName(configPath)!,
                texts,
            },
            TimeSpan.FromMinutes(10),
            cancellationToken);
        if (response.Results.Count != texts.Count)
        {
            throw new LocalModelException(
                "TRANSLATION_RESULT_INVALID",
                "Model Trung → Việt trả về số lượng bản dịch không hợp lệ.");
        }

        var translations = new string[texts.Count];
        for (var index = 0; index < texts.Count; index++)
        {
            var result = response.Results[index];
            var quality = TranslationQualityValidator.Validate(
                texts[index],
                result.Text,
                result.EndedWithEos,
                result.GeneratedTokenCount,
                result.MaxGeneratedTokens);
            if (!quality.IsValid)
            {
                throw new LocalModelException(
                    "TRANSLATION_OUTPUT_INVALID",
                    $"Model Trung → Việt trả về nội dung không an toàn để lưu ({quality.Code}). Hãy thử lại.");
            }

            translations[index] = result.Text.Trim();
        }

        return translations;
    }

    private sealed record TransformersTranslationWorkerResult(
        string Text,
        bool EndedWithEos,
        int GeneratedTokenCount,
        int MaxGeneratedTokens);

    private sealed record TransformersTranslationWorkerResponse(
        IReadOnlyList<TransformersTranslationWorkerResult> Results);
}

public sealed record VoiceSynthesisRequest(Guid CueId, string Text, string OutputPath);

public interface ILocalVoiceSynthesizer
{
    Task SynthesizeAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        CancellationToken cancellationToken);
}

public sealed class PiperLocalVoiceSynthesizer : ILocalVoiceSynthesizer
{
    public const string ModelId = "piper-vi-vais1000-medium";
    public static readonly string ModelRelativePath = Path.Combine("piper", "vi_VN-vais1000-medium.onnx");
    public static readonly string ConfigRelativePath = Path.Combine("piper", "vi_VN-vais1000-medium.onnx.json");

    private readonly LocalModelManager _models;
    private readonly LocalWorkerProcess _worker;
    private readonly LocalWorkerRuntimeLocator _runtime;

    public PiperLocalVoiceSynthesizer(
        AppPaths paths,
        LocalModelManager models,
        LocalWorkerProcess? worker = null)
    {
        _models = models;
        _worker = worker ?? new LocalWorkerProcess();
        _runtime = new LocalWorkerRuntimeLocator(paths);
    }

    public async Task SynthesizeAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        var response = await _worker.RunAsync<PiperWorkerResponse>(
            _runtime.RequirePython(),
            _runtime.RequireWorker("piper_worker.py"),
            new
            {
                modelPath = _models.RequireFile(ModelId, ModelRelativePath),
                configPath = _models.RequireFile(ModelId, ConfigRelativePath),
                volume = 1.0,
                lengthScale = 1.0,
                items = items.Select(item => new { item.Text, item.OutputPath }).ToArray(),
            },
            TimeSpan.FromMinutes(10),
            cancellationToken);
        if (response.Written.Count != items.Count
            || items.Any(item => !File.Exists(item.OutputPath) || new FileInfo(item.OutputPath).Length <= 44))
        {
            throw new LocalModelException(
                "VOICE_RESULT_INVALID",
                "Piper không tạo đủ file giọng đọc.");
        }
    }

    private sealed record PiperWorkerResponse(IReadOnlyList<string> Written);
}

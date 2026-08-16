using SubVid.App.Core;

namespace SubVid.App.LocalAi;

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
            TimeSpan.FromMinutes(60),
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

public sealed record VoiceSynthesisRequest(
    Guid CueId,
    string Text,
    string OutputPath,
    string VoiceId = LocalVoiceCatalog.DefaultVoiceId,
    int Speed = 0,
    VoiceProviderCheckpoint? ProviderCheckpoint = null);

public sealed record VoiceProviderCheckpoint(
    string? RequestId,
    string? ResultUrl,
    Func<string?, string?, CancellationToken, ValueTask>? SaveAsync = null);

public interface IVoiceSynthesizer
{
    Task SynthesizeAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        CancellationToken cancellationToken);
}

public interface ILocalVoiceSynthesizer : IVoiceSynthesizer;

public interface IIncrementalVoiceSynthesizer : IVoiceSynthesizer
{
    Task SynthesizeIncrementallyAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        Func<VoiceSynthesisRequest, ValueTask> onCompleted,
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

public sealed class VieNeuLocalVoiceSynthesizer : ILocalVoiceSynthesizer
{
    private readonly AppPaths _paths;
    private readonly LocalWorkerProcess _worker;
    private readonly LocalWorkerRuntimeLocator _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VieNeuLocalVoiceSynthesizer(AppPaths paths, LocalWorkerProcess? worker = null)
    {
        _paths = paths;
        _worker = worker ?? new LocalWorkerProcess();
        _runtime = new LocalWorkerRuntimeLocator(paths);
    }

    public bool IsReady
    {
        get
        {
            try
            {
                return File.Exists(ReadyMarkerPath)
                    && string.Equals(
                        File.ReadAllText(ReadyMarkerPath).Trim(),
                        LocalVoiceCatalog.VieNeuModelVersion,
                        StringComparison.Ordinal)
                    && Directory.Exists(HuggingFaceCacheRoot)
                    && Directory.EnumerateFiles(
                        HuggingFaceCacheRoot,
                        "*",
                        SearchOption.AllDirectories).Any();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (IsReady)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady)
            {
                return;
            }

            Directory.CreateDirectory(ModelRoot);
            _ = await RunWorkerAsync([], prepareOnly: true, cancellationToken);
            await File.WriteAllTextAsync(
                ReadyMarkerPath,
                LocalVoiceCatalog.VieNeuModelVersion,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SynthesizeAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (items.Any(item => LocalVoiceCatalog.Resolve(item.VoiceId).Engine != LocalVoiceEngines.VieNeu))
        {
            throw new LocalModelException("VOICE_ID_INVALID", "Danh sách tạo giọng VieNeu chứa giọng không hợp lệ.");
        }

        await EnsureReadyAsync(cancellationToken);
        var response = await RunWorkerAsync(items, prepareOnly: false, cancellationToken);
        if (response.Written.Count != items.Count
            || items.Any(item => !File.Exists(item.OutputPath) || new FileInfo(item.OutputPath).Length <= 44))
        {
            throw new LocalModelException(
                "VOICE_RESULT_INVALID",
                "VieNeu không tạo đủ file giọng đọc.");
        }
    }

    private async Task<VieNeuWorkerResponse> RunWorkerAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        bool prepareOnly,
        CancellationToken cancellationToken) =>
        await _worker.RunAsync<VieNeuWorkerResponse>(
            _runtime.RequireVieNeuPython(),
            _runtime.RequireWorker("vieneu_worker.py"),
            new
            {
                prepareOnly,
                mode = "v3turbo",
                backend = "onnx",
                precision = "int8",
                items = items.Select(item => new
                {
                    item.Text,
                    item.OutputPath,
                    voice = LocalVoiceCatalog.Resolve(item.VoiceId).ProviderVoiceId,
                }).ToArray(),
            },
            TimeSpan.FromMinutes(60),
            cancellationToken,
            new Dictionary<string, string>
            {
                ["HF_HOME"] = HuggingFaceCacheRoot,
                ["HF_HUB_DISABLE_TELEMETRY"] = "1",
                ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
                ["TEMP"] = _paths.AiTempDirectory,
                ["TMP"] = _paths.AiTempDirectory,
            },
            _paths.AiTempDirectory);

    private string ModelRoot => _paths.GetModelPath("vieneu", "v3-turbo");

    private string HuggingFaceCacheRoot => Path.Combine(_paths.AiCacheDirectory, "HuggingFace");

    private string ReadyMarkerPath => Path.Combine(ModelRoot, ".ready");

    private sealed record VieNeuWorkerResponse(IReadOnlyList<string> Written);
}

public sealed class CompositeLocalVoiceSynthesizer : ILocalVoiceSynthesizer, IIncrementalVoiceSynthesizer
{
    private readonly ILocalVoiceSynthesizer _piper;
    private readonly ILocalVoiceSynthesizer _vieNeu;
    private readonly IIncrementalVoiceSynthesizer? _fpt;

    public CompositeLocalVoiceSynthesizer(
        ILocalVoiceSynthesizer piper,
        ILocalVoiceSynthesizer vieNeu,
        IIncrementalVoiceSynthesizer? fpt = null)
    {
        _piper = piper;
        _vieNeu = vieNeu;
        _fpt = fpt;
    }

    public async Task SynthesizeAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        CancellationToken cancellationToken) =>
        await SynthesizeIncrementallyAsync(items, _ => ValueTask.CompletedTask, cancellationToken);

    public async Task SynthesizeIncrementallyAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        Func<VoiceSynthesisRequest, ValueTask> onCompleted,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (LocalVoiceCatalog.Find(item.VoiceId) is null)
            {
                throw new LocalModelException("VOICE_ID_INVALID", $"Không tìm thấy giọng đọc '{item.VoiceId}'.");
            }
        }

        var piperItems = items
            .Where(item => LocalVoiceCatalog.Resolve(item.VoiceId).Engine == LocalVoiceEngines.Piper)
            .ToArray();
        var vieNeuItems = items
            .Where(item => LocalVoiceCatalog.Resolve(item.VoiceId).Engine == LocalVoiceEngines.VieNeu)
            .ToArray();
        var fptItems = items
            .Where(item => LocalVoiceCatalog.Resolve(item.VoiceId).Engine == LocalVoiceEngines.Fpt)
            .ToArray();

        if (fptItems.Length > 0 && _fpt is null)
        {
            throw new VoiceSynthesisException(
                "FPT_API_KEY_REQUIRED",
                "Hãy lưu API key FPT.AI trước khi tạo giọng online.",
                retryable: false);
        }

        // Chạy tuần tự để tránh giữ đồng thời hai model giọng trong RAM.
        await _piper.SynthesizeAsync(piperItems, cancellationToken);
        foreach (var item in piperItems)
        {
            await onCompleted(item);
        }

        await _vieNeu.SynthesizeAsync(vieNeuItems, cancellationToken);
        foreach (var item in vieNeuItems)
        {
            await onCompleted(item);
        }

        if (_fpt is not null)
        {
            await _fpt.SynthesizeIncrementallyAsync(fptItems, onCompleted, cancellationToken);
        }
    }
}

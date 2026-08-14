using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.LocalAi;
using TOOL_VIETSUB_APP.Subtitles;
using TOOL_VIETSUB_APP.Translation;

namespace TOOL_VIETSUB_APP.Jobs;

public sealed class TranslationJobExecutor : ILocalJobExecutor
{
    private static readonly JsonSerializerOptions FingerprintJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly ITranslationProvider _provider;

    public TranslationJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        ITranslationProvider provider)
    {
        _paths = paths;
        _workspace = workspace;
        _project = project;
        _provider = provider;
    }

    public TranslationJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        ILocalTranslator translator)
        : this(
            paths,
            workspace,
            project,
            new LocalTranslationProviderAdapter(
                translator,
                string.IsNullOrWhiteSpace(project.Settings.TranslationModelId)
                    ? "local-test"
                    : project.Settings.TranslationModelId))
    {
    }

    public async Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var track = _project.SubtitleTracks.LastOrDefault(item => item.Cues.Count > 0)
            ?? throw new LocalJobException(
                "SUBTITLE_TRACK_MISSING",
                "Chưa có transcript để dịch.",
                retryable: false);
        var sourceLanguage = ResolveSourceLanguage(job);
        var targetLanguage = job.Parameters.GetValueOrDefault("targetLanguage")
            ?? _project.TargetLanguageCode;
        var fingerprints = track.Cues
            .Select((cue, index) => BuildCueFingerprint(cue, index, track, sourceLanguage, targetLanguage))
            .ToDictionary(item => item.CueId, item => item.Fingerprint);
        var pending = track.Cues
            .Where(cue => !cue.TranslationLocked
                && (string.IsNullOrWhiteSpace(cue.TranslatedText)
                    || TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText)
                    || !string.Equals(
                        cue.TranslationSourceFingerprint,
                        fingerprints[cue.CueId],
                        StringComparison.Ordinal)))
            .Select(cue => cue.CueId)
            .ToHashSet();
        if (pending.Count == 0)
        {
            await reportProgress(new JobProgressUpdate(
                "TRANSLATE",
                100,
                100,
                "Không còn phân đoạn cần dịch."));
            return;
        }

        var settings = _project.Settings;
        var scenes = TranslationScenePlanner.Plan(
            track.Cues,
            pending,
            settings.TranslationSceneMaxCues,
            settings.TranslationContextCueCount,
            settings.TranslationSceneGapMilliseconds,
            settings.TranslationMaxCharactersPerSecond);
        var cache = new TranslationResultCache(_paths, _project.ProjectId);
        var completed = 0;
        foreach (var scene in scenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = BuildRequest(
                scene,
                sourceLanguage,
                targetLanguage,
                TranslationPass.Translate);
            var reviewEnabled = settings.TranslationReviewEnabled && _provider.SupportsContextualReview;
            var cacheKey = cache.BuildKey(request, _provider.ProviderId, _provider.ModelId, reviewEnabled);
            var result = await cache.TryReadAsync(cacheKey, cancellationToken);
            if (!IsResultValid(result, request.TargetCueIds))
            {
                result = await TranslateSceneAsync(request, reviewEnabled, cancellationToken);
                await cache.WriteAsync(cacheKey, result, cancellationToken);
            }

            ApplySceneResult(
                track,
                result!,
                fingerprints,
                settings.TranslationMaxCharactersPerSecond);
            completed += request.TargetCueIds.Count;
            await _workspace.SaveAsync(_project, cancellationToken);
            var percent = completed * 100d / pending.Count;
            await reportProgress(new JobProgressUpdate(
                "TRANSLATE",
                percent,
                percent,
                $"Đã dịch theo ngữ cảnh và lưu {completed}/{pending.Count} phân đoạn."));
        }

        foreach (var (cue, index) in track.Cues.Select((cue, index) => (cue, index)))
        {
            if (pending.Contains(cue.CueId) && !string.IsNullOrWhiteSpace(cue.TranslatedText))
            {
                cue.TranslationSourceFingerprint = BuildCueFingerprint(
                    cue,
                    index,
                    track,
                    sourceLanguage,
                    targetLanguage).Fingerprint;
            }
        }

        await WriteTranslatedSubtitleAsync(track, job, cancellationToken);
    }

    private string ResolveSourceLanguage(LocalJob job)
    {
        var sourceLanguage = job.Parameters.GetValueOrDefault("sourceLanguage")
            ?? LocalLanguageCodes.ResolveProjectSource(_project)
            ?? throw new LocalJobException(
                "TRANSLATION_SOURCE_REQUIRED",
                "Hãy chọn tiếng Trung hoặc tiếng Anh trước khi dịch.",
                retryable: false);
        return LocalLanguageCodes.NormalizeSource(sourceLanguage)
            ?? throw new LocalJobException(
                "TRANSLATION_SOURCE_REQUIRED",
                "Không xác định được ngôn ngữ nguồn để dịch.",
                retryable: false);
    }

    private TranslationSceneRequest BuildRequest(
        PlannedTranslationScene scene,
        string sourceLanguage,
        string targetLanguage,
        TranslationPass pass)
    {
        var context = _project.TranslationContext ?? new ProjectTranslationContext();
        var memory = _project.TranslationMemory
            .Where(entry => string.Equals(
                    LocalLanguageCodes.NormalizeSource(entry.SourceLanguageCode),
                    sourceLanguage,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.TargetLanguageCode, targetLanguage, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.UpdatedAtUtc)
            .Take(500)
            .ToArray();
        return new TranslationSceneRequest(
            _project.Name,
            sourceLanguage,
            targetLanguage,
            context.Summary,
            context.CharacterInstructions,
            context.StyleInstructions,
            _project.TranslationGlossary.Take(200).ToArray(),
            memory,
            scene.Cues,
            pass);
    }

    private async Task<TranslationSceneResult> TranslateSceneAsync(
        TranslationSceneRequest request,
        bool reviewEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            var firstPass = await _provider.TranslateAsync(request, cancellationToken);
            EnsureResultValid(firstPass, request.TargetCueIds);
            if (!reviewEnabled || !TranslationProviders.IsCloud(firstPass.ProviderId))
            {
                return firstPass;
            }

            var candidates = firstPass.Items.ToDictionary(item => item.CueId);
            var reviewRequest = request with
            {
                Pass = TranslationPass.Review,
                Cues = request.Cues.Select(cue => cue.IsTarget
                    ? cue with { CandidateTranslation = candidates[cue.CueId].TranslatedText }
                    : cue).ToArray(),
            };
            var reviewed = await _provider.TranslateAsync(reviewRequest, cancellationToken);
            EnsureResultValid(reviewed, request.TargetCueIds);
            var combined = reviewed.Items.Select(item => item with
            {
                Warnings = candidates[item.CueId].Warnings
                    .Concat(item.Warnings)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            }).ToArray();
            return reviewed with { Items = combined };
        }
        catch (TranslationProviderException exception)
        {
            throw new LocalJobException(exception.Code, exception.Message, exception.Retryable);
        }
        catch (LocalModelException exception)
        {
            throw new LocalJobException(exception.Code, exception.Message, retryable: true);
        }
    }

    private void ApplySceneResult(
        SubtitleDocument track,
        TranslationSceneResult result,
        IReadOnlyDictionary<Guid, string> fingerprints,
        double maximumCharactersPerSecond)
    {
        var cues = track.Cues.ToDictionary(cue => cue.CueId);
        var prepared = result.Items.Select(item =>
        {
            var cue = cues[item.CueId];
            var normalized = item.TranslatedText.Trim();
            var assessment = TranslationQualityValidator.AssessCue(
                cue.OriginalText,
                normalized,
                cue.EndMilliseconds - cue.StartMilliseconds,
                _project.TranslationGlossary,
                maximumCharactersPerSecond,
                item.Confidence,
                item.Warnings);
            if (!assessment.IsValid)
            {
                throw new LocalJobException(
                    "TRANSLATION_OUTPUT_INVALID",
                    $"Bản dịch phân đoạn {cue.CueId} bị từ chối ({assessment.FailureCode}). Dữ liệu cũ vẫn được giữ nguyên.",
                    retryable: true);
            }

            return (Cue: cue, Text: normalized, Item: item, Assessment: assessment);
        }).ToArray();

        foreach (var entry in prepared)
        {
            var changed = !string.Equals(entry.Cue.TranslatedText, entry.Text, StringComparison.Ordinal);
            entry.Cue.TranslatedText = entry.Text;
            entry.Cue.TranslationModelId = $"{result.ProviderId}:{result.ModelId}";
            entry.Cue.TranslationModelVersion = result.ModelVersion;
            entry.Cue.TranslationSourceFingerprint = fingerprints[entry.Cue.CueId];
            entry.Cue.TranslationConfidence = entry.Item.Confidence;
            entry.Cue.TranslationWarnings = entry.Assessment.Warnings.ToList();
            entry.Cue.TranslationQualityStatus = entry.Assessment.Warnings.Count == 0 ? "VALID" : "REVIEW";
            entry.Cue.TranslationReviewedAtUtc = TranslationProviders.IsCloud(result.ProviderId)
                && _project.Settings.TranslationReviewEnabled
                ? DateTime.UtcNow
                : null;
            if (changed)
            {
                _project.AudioTracks.RemoveAll(item =>
                    item.Role == "VOICE_TIMELINE"
                    || (item.Role == "VOICE_CUE" && item.CueId == entry.Cue.CueId));
            }
        }
    }

    private async Task WriteTranslatedSubtitleAsync(
        SubtitleDocument track,
        LocalJob job,
        CancellationToken cancellationToken)
    {
        var relativeOutput = Path.Combine("subtitles", $"translated-{track.TrackId:N}.srt");
        var outputPath = _paths.GetProjectPath(_project.ProjectId, relativeOutput);
        var partialPath = outputPath + ".partial";
        try
        {
            await File.WriteAllTextAsync(
                partialPath,
                SrtService.Serialize(track.Cues, preferTranslation: true),
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(partialPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }

        job.Steps.Single(item => item.Code == "TRANSLATE").OutputRelativePath = relativeOutput;
        await _workspace.SaveAsync(_project, cancellationToken);
    }

    private static bool IsResultValid(
        TranslationSceneResult? result,
        IReadOnlyList<Guid> expectedCueIds) =>
        result is not null
        && result.Items.Count == expectedCueIds.Count
        && result.Items.Select(item => item.CueId).SequenceEqual(expectedCueIds)
        && result.Items.All(item => !string.IsNullOrWhiteSpace(item.TranslatedText));

    private static void EnsureResultValid(
        TranslationSceneResult result,
        IReadOnlyList<Guid> expectedCueIds)
    {
        if (!IsResultValid(result, expectedCueIds))
        {
            throw new TranslationProviderException(
                "TRANSLATION_RESULT_INVALID",
                "Dịch vụ trả về thiếu, thừa hoặc sai thứ tự cue.");
        }
    }

    private (Guid CueId, string Fingerprint) BuildCueFingerprint(
        SubtitleCue cue,
        int cueIndex,
        SubtitleDocument track,
        string sourceLanguage,
        string targetLanguage)
    {
        var settings = _project.Settings;
        var contextCount = Math.Clamp(settings.TranslationContextCueCount, 0, 10);
        var first = Math.Max(0, cueIndex - contextCount);
        var last = Math.Min(track.Cues.Count - 1, cueIndex + contextCount);
        var payload = JsonSerializer.Serialize(new
        {
            version = 2,
            sourceLanguage,
            targetLanguage,
            provider = _provider.ProviderId,
            model = _provider.ModelId,
            qualityMode = TranslationQualityModes.Normalize(settings.TranslationQualityMode),
            settings.TranslationReviewEnabled,
            settings.TranslationMaxCharactersPerSecond,
            context = _project.TranslationContext,
            glossary = _project.TranslationGlossary.Select(entry => new
            {
                entry.SourceText,
                entry.TargetText,
                entry.Note,
            }),
            memory = _project.TranslationMemory.Select(entry => new
            {
                entry.SourceLanguageCode,
                entry.TargetLanguageCode,
                entry.SourceText,
                entry.TranslatedText,
            }),
            cues = track.Cues.Skip(first).Take(last - first + 1).Select(item => new
            {
                item.CueId,
                item.StartMilliseconds,
                item.EndMilliseconds,
                item.Speaker,
                item.OriginalText,
                approvedTranslation = item.TranslationLocked ? item.TranslatedText : null,
                item.TranslationLocked,
            }),
        }, FingerprintJsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return (cue.CueId, Convert.ToHexString(bytes).ToLowerInvariant());
    }
}

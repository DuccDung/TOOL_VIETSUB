using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SubVid.App.Core;
using SubVid.App.LocalAi;
using SubVid.App.Subtitles;
using SubVid.App.Translation;

namespace SubVid.App.Jobs;

public sealed class TranslationJobExecutor : ILocalJobExecutor
{
    private const int MaximumSafetyRepairAttempts = 2;
    private static readonly JsonSerializerOptions FingerprintJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly ITranslationProvider _provider;
    private readonly object _metricsGate = new();
    private TranslationJobMetrics? _activeMetrics;

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
        if (_provider is ILocalJobAwareTranslationProvider jobAwareProvider)
        {
            jobAwareProvider.BindJob(job);
        }

        var track = _project.SubtitleTracks.LastOrDefault(item => item.Cues.Count > 0)
            ?? throw new LocalJobException(
                "SUBTITLE_TRACK_MISSING",
                "Chưa có transcript để dịch.",
                retryable: false);
        var sourceLanguage = ResolveSourceLanguage(job);
        var targetLanguage = job.Parameters.GetValueOrDefault("targetLanguage")
            ?? _project.TargetLanguageCode;
        var runMode = TranslationRunModes.Normalize(
            job.Parameters.GetValueOrDefault(TranslationRunModes.ParameterName));
        var restartPrepared = bool.TryParse(
            job.Parameters.GetValueOrDefault(TranslationRunModes.RestartPreparedParameterName),
            out var prepared)
            && prepared;
        if (runMode == TranslationRunModes.Restart && !restartPrepared)
        {
            foreach (var cue in track.Cues.Where(cue => !cue.TranslationLocked))
            {
                cue.TranslationSourceFingerprint = null;
            }

            job.Parameters[TranslationRunModes.RestartPreparedParameterName] = bool.TrueString;
            await _workspace.SaveAsync(_project, cancellationToken);
        }

        var configurationFingerprint = BuildTranslationConfigurationFingerprint(sourceLanguage, targetLanguage);
        var fingerprints = track.Cues
            .Select((cue, index) => BuildCueFingerprint(cue, index, track, configurationFingerprint))
            .ToDictionary(item => item.CueId, item => item.Fingerprint);
        var pending = track.Cues
            .Where(cue => !cue.TranslationLocked
                && (string.IsNullOrWhiteSpace(cue.TranslatedText)
                    || TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText)
                    || string.Equals(
                        cue.VoiceTiming?.Status,
                        VoiceTimingStatuses.ReviewRequired,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        cue.TranslationSourceFingerprint,
                        fingerprints[cue.CueId],
                        StringComparison.Ordinal)))
            .Select(cue => cue.CueId)
            .ToHashSet();
        var metrics = job.TranslationMetrics ??= new TranslationJobMetrics();
        metrics.TotalPendingCues = Math.Max(metrics.TotalPendingCues, pending.Count);
        _activeMetrics = metrics;
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
            TranslationSceneLimits.ResolveMaximumTargetCues(
                _provider.ProviderId,
                settings.TranslationSceneMaxCues),
            TranslationSceneLimits.ResolveContextCueCount(
                _provider.ProviderId,
                settings.TranslationContextCueCount),
            settings.TranslationSceneGapMilliseconds,
            settings.TranslationMaxCharactersPerSecond);
        var cache = new TranslationResultCache(_paths, _project.ProjectId);
        var bypassExistingCache = runMode == TranslationRunModes.Restart;
        var completed = 0;
        var maximumConcurrency = TranslationProviders.IsCloud(_provider.ProviderId) ? 2 : 1;
        var chapterQueue = new Queue<Queue<PlannedTranslationScene>>(
            scenes.GroupBy(scene => scene.ChapterNumber)
                .OrderBy(group => group.Key)
                .Select(group => new Queue<PlannedTranslationScene>(group.OrderBy(scene => scene.SceneNumber))));
        while (chapterQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activeChapters = new List<Queue<PlannedTranslationScene>>(maximumConcurrency);
            var activeScenes = new List<PlannedTranslationScene>(maximumConcurrency);
            while (activeScenes.Count < maximumConcurrency && chapterQueue.Count > 0)
            {
                var chapter = chapterQueue.Dequeue();
                activeChapters.Add(chapter);
                activeScenes.Add(chapter.Dequeue());
            }

            var processedScenes = await Task.WhenAll(activeScenes.Select(scene => ProcessSceneAsync(
                scene,
                track,
                sourceLanguage,
                targetLanguage,
                settings.TranslationReviewEnabled && _provider.SupportsContextualReview,
                cache,
                bypassExistingCache,
                cancellationToken)));
            foreach (var processed in processedScenes.OrderBy(item => item.Scene.SceneNumber))
            {
                if (processed.CacheHit)
                {
                    metrics.CacheHitScenes++;
                }
                else
                {
                    metrics.TranslatedScenes++;
                }

                var applyOutcome = ApplySceneResult(
                    track,
                    processed.Result,
                    fingerprints,
                    settings.TranslationMaxCharactersPerSecond);
                completed += processed.Request.TargetCueIds.Count;
                metrics.CompletedCues = Math.Min(
                    metrics.TotalPendingCues,
                    metrics.CompletedCues + applyOutcome.AppliedCues);
                metrics.SkippedCues += applyOutcome.SkippedCues;
                metrics.ReviewedCues += processed.Result.Items.Count(item => item.WasReviewed);
                metrics.AutoRepairedCues += processed.Result.Items.Count(item => item.WasAutoRepaired);
            }

            await _workspace.SaveAsync(_project, cancellationToken);
            var percent = completed * 100d / pending.Count;
            var skippedMessage = metrics.SkippedCues > 0
                ? $"; giữ lại {metrics.SkippedCues} cue cần chú ý"
                : string.Empty;
            await reportProgress(new JobProgressUpdate(
                "TRANSLATE",
                percent,
                percent,
                $"Đã xử lý {completed}/{pending.Count} phân đoạn{skippedMessage}."));

            foreach (var chapter in activeChapters.Where(chapter => chapter.Count > 0))
            {
                chapterQueue.Enqueue(chapter);
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
        var relevantGlossary = _project.TranslationGlossary
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SourceText)
                && scene.Cues.Any(cue => cue.OriginalText.Contains(
                    entry.SourceText,
                    StringComparison.OrdinalIgnoreCase)))
            .Take(80)
            .ToArray();
        var memory = _project.TranslationMemory
            .Where(entry => string.Equals(
                    LocalLanguageCodes.NormalizeSource(entry.SourceLanguageCode),
                    sourceLanguage,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.TargetLanguageCode, targetLanguage, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.UpdatedAtUtc)
            .Take(500)
            .ToArray();
        var latestCues = _project.SubtitleTracks
            .SelectMany(track => track.Cues)
            .ToDictionary(cue => cue.CueId);
        var sceneCues = scene.Cues.Select(input => latestCues.TryGetValue(input.CueId, out var latest)
            ? input with
            {
                CandidateTranslation = !input.IsTarget && !string.IsNullOrWhiteSpace(latest.TranslatedText)
                    ? latest.TranslatedText.Trim()
                    : input.CandidateTranslation,
            }
            : input).ToArray();
        return new TranslationSceneRequest(
            _project.Name,
            sourceLanguage,
            targetLanguage,
            context.Summary,
            context.CharacterInstructions,
            context.StyleInstructions,
            relevantGlossary,
            memory,
            sceneCues,
            pass,
            TranslationScenePlanner.BuildChapterContext(scene, _project.SubtitleTracks
                .SelectMany(track => track.Cues)
                .OrderBy(cue => cue.StartMilliseconds)
                .ToArray()));
    }

    private async Task<ProcessedTranslationScene> ProcessSceneAsync(
        PlannedTranslationScene scene,
        SubtitleDocument track,
        string sourceLanguage,
        string targetLanguage,
        bool reviewEnabled,
        TranslationResultCache cache,
        bool bypassExistingCache,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(scene, sourceLanguage, targetLanguage, TranslationPass.Translate);
        var cacheKey = cache.BuildKey(request, _provider.ProviderId, _provider.ModelId, reviewEnabled);
        var result = bypassExistingCache
            ? null
            : await cache.TryReadAsync(cacheKey, cancellationToken);
        var cacheHit = IsResultSafe(result, request);
        if (!cacheHit)
        {
            result = await TranslateSceneAsync(request, reviewEnabled, cancellationToken);
            EnsureResultValid(result, request.TargetCueIds);
            if (FindUnsafeTranslations(result, request).Count == 0)
            {
                await cache.WriteAsync(cacheKey, result, cancellationToken);
            }
        }

        return new ProcessedTranslationScene(scene, request, result!, cacheHit);
    }

    private async Task<TranslationSceneResult> TranslateSceneAsync(
        TranslationSceneRequest request,
        bool reviewEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            var firstPass = await CallProviderAsync(request, cancellationToken);
            EnsureResultValid(firstPass, request.TargetCueIds);
            if (!reviewEnabled || !TranslationProviders.IsCloud(firstPass.ProviderId))
            {
                return await RepairUnsafeTranslationsAsync(request, firstPass, cancellationToken);
            }

            var candidates = firstPass.Items.ToDictionary(item => item.CueId);
            var reviewIds = FindSelectiveReviewCueIds(firstPass, request).ToHashSet();
            if (reviewIds.Count == 0)
            {
                return await RepairUnsafeTranslationsAsync(request, firstPass, cancellationToken);
            }

            var reviewRequest = request with
            {
                Pass = TranslationPass.Review,
                Cues = request.Cues.Select(cue => cue with
                {
                    IsTarget = reviewIds.Contains(cue.CueId),
                    CandidateTranslation = candidates.TryGetValue(cue.CueId, out var candidate)
                        ? candidate.TranslatedText
                        : cue.CandidateTranslation,
                }).ToArray(),
            };
            var reviewed = await CallProviderAsync(reviewRequest, cancellationToken);
            EnsureResultValid(reviewed, reviewRequest.TargetCueIds);
            var reviewedItems = reviewed.Items.ToDictionary(item => item.CueId);
            var combined = firstPass.Items.Select(item => reviewedItems.TryGetValue(item.CueId, out var replacement)
                ? replacement with
                {
                    Warnings = item.Warnings
                        .Concat(replacement.Warnings)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    WasReviewed = true,
                }
                : item).ToArray();
            var combinedUsage = (firstPass.Usage ?? TranslationUsage.Empty).Add(reviewed.Usage);
            return await RepairUnsafeTranslationsAsync(
                request,
                firstPass with
                {
                    ModelVersion = reviewed.ModelVersion,
                    Items = combined,
                    Usage = combinedUsage,
                },
                cancellationToken);
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

    private async Task<TranslationSceneResult> RepairUnsafeTranslationsAsync(
        TranslationSceneRequest request,
        TranslationSceneResult result,
        CancellationToken cancellationToken)
    {
        if (!TranslationProviders.IsCloud(result.ProviderId))
        {
            return result;
        }

        var current = result;
        for (var attempt = 1; attempt <= MaximumSafetyRepairAttempts; attempt++)
        {
            var invalid = FindUnsafeTranslations(current, request);
            if (invalid.Count == 0)
            {
                return current;
            }

            var invalidIds = invalid.Select(item => item.CueId).ToHashSet();
            var currentItems = current.Items.ToDictionary(item => item.CueId);
            var repairRequest = request with
            {
                Pass = TranslationPass.Review,
                Cues = request.Cues.Select(cue => cue with
                {
                    IsTarget = invalidIds.Contains(cue.CueId),
                    CandidateTranslation = currentItems.TryGetValue(cue.CueId, out var item)
                        ? item.TranslatedText
                        : cue.CandidateTranslation,
                }).ToArray(),
            };
            var repaired = await CallProviderAsync(repairRequest, cancellationToken);
            EnsureResultValid(repaired, repairRequest.TargetCueIds);
            var repairedItems = repaired.Items.ToDictionary(item => item.CueId);
            current = current with
            {
                ModelVersion = repaired.ModelVersion,
                Usage = (current.Usage ?? TranslationUsage.Empty).Add(repaired.Usage),
                Items = current.Items.Select(item => repairedItems.TryGetValue(item.CueId, out var replacement)
                    ? replacement with
                    {
                        Warnings = item.Warnings
                            .Concat(replacement.Warnings)
                            .Append("AUTO_REPAIRED")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        WasAutoRepaired = true,
                    }
                    : item).ToArray(),
            };
        }

        return current;
    }

    private SceneApplyOutcome ApplySceneResult(
        SubtitleDocument track,
        TranslationSceneResult result,
        IReadOnlyDictionary<Guid, string> fingerprints,
        double maximumCharactersPerSecond)
    {
        var cues = track.Cues.ToDictionary(cue => cue.CueId);
        var prepared = new List<(SubtitleCue Cue, string Text, TranslationItemResult Item, TranslationCueQualityAssessment Assessment)>();
        var skipped = 0;
        foreach (var item in result.Items)
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
                var validationWarning = $"TRANSLATION_INVALID:{assessment.FailureCode ?? "UNKNOWN"}";
                cue.TranslationWarnings = cue.TranslationWarnings
                    .Append(validationWarning)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                cue.TranslationQualityStatus = "REVIEW";
                skipped++;
                continue;
            }

            prepared.Add((cue, normalized, item, assessment));
        }

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
            entry.Cue.TranslationReviewedAtUtc = entry.Item.WasReviewed
                ? DateTime.UtcNow
                : null;
            if (changed)
            {
                entry.Cue.VoiceTiming = null;
                VoiceTimelinePreviewState.MarkStale(_project);
                _project.AudioTracks.RemoveAll(item =>
                    (item.Role == "VOICE_CUE" && item.CueId == entry.Cue.CueId)
                    || (item.Role == "VOICE_PHRASE" && item.CueIds.Contains(entry.Cue.CueId)));
            }
        }

        return new SceneApplyOutcome(prepared.Count, skipped);
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

    private static bool IsResultSafe(
        TranslationSceneResult? result,
        TranslationSceneRequest request) =>
        IsResultValid(result, request.TargetCueIds)
        && FindUnsafeTranslations(result!, request).Count == 0;

    private static IReadOnlyList<(Guid CueId, string Code)> FindUnsafeTranslations(
        TranslationSceneResult result,
        TranslationSceneRequest request)
    {
        var inputs = request.Cues.ToDictionary(cue => cue.CueId);
        return result.Items.Select(item =>
            {
                var quality = inputs.TryGetValue(item.CueId, out var cue)
                    ? TranslationQualityValidator.ValidateText(cue.OriginalText, item.TranslatedText)
                    : TranslationQualityResult.Invalid("UNKNOWN_CUE");
                return (item.CueId, Quality: quality);
            })
            .Where(item => !item.Quality.IsValid)
            .Select(item => (item.CueId, item.Quality.Code ?? "INVALID_TRANSLATION"))
            .ToArray();
    }

    private static IReadOnlyList<Guid> FindSelectiveReviewCueIds(
        TranslationSceneResult result,
        TranslationSceneRequest request)
    {
        var inputs = request.Cues.ToDictionary(cue => cue.CueId);
        return result.Items.Where(item =>
            {
                if (!inputs.TryGetValue(item.CueId, out var cue))
                {
                    return true;
                }

                var durationMilliseconds = Math.Max(250, cue.EndMilliseconds - cue.StartMilliseconds);
                var durationSeconds = durationMilliseconds / 1000d;
                var maximumCharactersPerSecond = Math.Clamp(
                    cue.SuggestedMaximumCharacters / durationSeconds,
                    8,
                    30);
                var assessment = TranslationQualityValidator.AssessCue(
                    cue.OriginalText,
                    item.TranslatedText,
                    durationMilliseconds,
                    request.Glossary,
                    maximumCharactersPerSecond,
                    item.Confidence,
                    item.Warnings);
                return !assessment.IsValid
                    || assessment.Warnings.Count > 0
                    || item.Confidence < 0.82;
            })
            .Select(item => item.CueId)
            .ToArray();
    }

    private async Task<TranslationSceneResult> CallProviderAsync(
        TranslationSceneRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _provider.TranslateAsync(request, cancellationToken);
            AddUsage(result.Usage);
            return result;
        }
        catch (TranslationProviderException exception)
        {
            AddUsage(exception.Usage);
            throw;
        }
    }

    private void AddUsage(TranslationUsage? usage)
    {
        if (usage is null || _activeMetrics is null)
        {
            return;
        }

        lock (_metricsGate)
        {
            _activeMetrics.InputTokens += Math.Max(0, usage.InputTokens);
            _activeMetrics.OutputTokens += Math.Max(0, usage.OutputTokens);
            _activeMetrics.CachedInputTokens += Math.Max(0, usage.CachedInputTokens);
            _activeMetrics.ApiRequests += Math.Max(0, usage.ApiRequests);
            _activeMetrics.RetryRequests += Math.Max(0, usage.RetryRequests);
        }
    }

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
        string configurationFingerprint)
    {
        var settings = _project.Settings;
        var contextCount = Math.Clamp(settings.TranslationContextCueCount, 0, 10);
        var first = Math.Max(0, cueIndex - contextCount);
        var last = Math.Min(track.Cues.Count - 1, cueIndex + contextCount);
        var payload = JsonSerializer.Serialize(new
        {
            version = 3,
            configurationFingerprint,
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

    private string BuildTranslationConfigurationFingerprint(
        string sourceLanguage,
        string targetLanguage)
    {
        var settings = _project.Settings;
        var payload = JsonSerializer.Serialize(new
        {
            version = 1,
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
        }, FingerprintJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private sealed record SceneApplyOutcome(int AppliedCues, int SkippedCues);

    private sealed record ProcessedTranslationScene(
        PlannedTranslationScene Scene,
        TranslationSceneRequest Request,
        TranslationSceneResult Result,
        bool CacheHit);
}

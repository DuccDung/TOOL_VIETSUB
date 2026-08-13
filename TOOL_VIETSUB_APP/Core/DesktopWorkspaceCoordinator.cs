using TOOL_VIETSUB_APP.Api;
using TOOL_VIETSUB_APP.Jobs;
using TOOL_VIETSUB_APP.LocalAi;
using TOOL_VIETSUB_APP.Media;
using TOOL_VIETSUB_APP.Usage;
using TOOL_VIETSUB_APP.Subtitles;

namespace TOOL_VIETSUB_APP.Core;

public sealed class DesktopWorkspaceCoordinator : IAsyncDisposable
{
    public const string PlaybackUrl = "https://media.vietsub.local/video";
    public const string VoicePlaybackUrl = "https://media.vietsub.local/voice";
    private readonly AuthSessionManager _auth;
    private readonly AppPaths _paths = new();
    private readonly ProjectWorkspaceService _projects;
    private readonly PersistentJobManager _jobs;
    private readonly QuotaProtectedJobService _quotaJobs;
    private readonly SrtService _subtitles;
    private readonly LocalModelManager _models;
    private readonly LocalAiRuntimeProvisioner _runtimeProvisioner;
    private ProjectSession? _session;
    private CancellationTokenSource? _importCancellation;

    public DesktopWorkspaceCoordinator(AuthSessionManager auth)
    {
        _auth = auth;
        _projects = new ProjectWorkspaceService(_paths);
        _jobs = new PersistentJobManager(_projects, _paths);
        _quotaJobs = new QuotaProtectedJobService(
            new DesktopQuotaGateway(auth),
            _jobs,
            _projects);
        _subtitles = new SrtService(_paths, _projects);
        _models = new LocalModelManager(_paths);
        _runtimeProvisioner = new LocalAiRuntimeProvisioner(_paths);
        _jobs.JobChanged += (_, job) => JobChanged?.Invoke(this, job);
    }

    public ProjectManifest? CurrentProject => _session?.Manifest;

    public event EventHandler<MediaImportProgress>? ImportProgressChanged;

    public event EventHandler<LocalJob>? JobChanged;

    public event EventHandler<LocalModelDownloadProgress>? ModelDownloadProgressChanged;

    public event EventHandler<LocalRuntimeProgress>? RuntimeProgressChanged;

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var account = RequireAccount();
        var projects = await _projects.ListAsync(account.UserId, cancellationToken);
        var current = CurrentProject;
        return current is null
            ? projects
            : projects.Select(item => item.ProjectId == current.ProjectId
                ? item with { NeedsRecovery = current.RecoveryRequired }
                : item).ToArray();
    }

    public async Task<DesktopProjectState> CreateAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var account = RequireAccount();
        var projectId = Guid.NewGuid();
        await _auth.ExecuteAuthenticatedAsync(
            (api, token) => api.CreateProjectAsync(
                new CreateProjectApiRequest(projectId, name.Trim(), null),
                token),
            cancellationToken);

        await CloseCurrentAsync(cancellationToken);
        var manifest = await _projects.CreateAsync(account.UserId, name, projectId, cancellationToken);
        manifest.ServerSynchronized = true;
        _session = new ProjectSession(_projects, manifest);
        await _session.StartAsync(cancellationToken);
        await _quotaJobs.ReconcilePendingSettlementsAsync(manifest, cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> OpenAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var account = RequireAccount();
        await CloseCurrentAsync(cancellationToken);
        var manifest = await _projects.OpenAsync(projectId, cancellationToken);
        if (manifest.OwnerUserId != account.UserId)
        {
            throw new UnauthorizedAccessException("Dự án không thuộc tài khoản đang đăng nhập.");
        }

        await _auth.ExecuteAuthenticatedAsync(
            (api, token) => api.CreateProjectAsync(
                new CreateProjectApiRequest(
                    manifest.ProjectId,
                    manifest.Name,
                    manifest.SourceLanguageCode),
                token),
            cancellationToken);
        manifest.ServerSynchronized = true;
        await _jobs.RestoreInterruptedJobsAsync(manifest, cancellationToken);
        _session = new ProjectSession(_projects, manifest);
        await _session.StartAsync(cancellationToken);
        await _quotaJobs.ReconcilePendingSettlementsAsync(manifest, cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> RenameAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        await _auth.ExecuteAuthenticatedAsync(
            (api, token) => api.RenameProjectAsync(
                manifest.ProjectId,
                new RenameProjectApiRequest(name.Trim()),
                token),
            cancellationToken);
        var renamed = await _projects.RenameAsync(manifest.ProjectId, name, cancellationToken);
        manifest.Name = renamed.Name;
        manifest.UpdatedAtUtc = renamed.UpdatedAtUtc;
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> UpdateLanguageSettingsAsync(
        string sourceLanguageCode,
        string ocrLanguageCode,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        var previousSourceLanguage = LocalLanguageCodes.ResolveProjectSource(manifest);
        var sourceSetting = LocalLanguageCodes.NormalizeSetting(sourceLanguageCode);
        var ocrSetting = LocalLanguageCodes.NormalizeSetting(ocrLanguageCode);
        var sourceLanguage = LocalLanguageCodes.NormalizeSource(sourceSetting);

        manifest.SourceLanguageCode = sourceLanguage;
        manifest.TargetLanguageCode = "vi";
        manifest.Settings.TranslationTarget = "vi";
        manifest.Settings.OcrLanguageCode = ocrSetting;
        manifest.Settings.TranslationModelId = sourceLanguage is null
            ? "auto"
            : LocalTranslatorFactory.GetModelId(sourceLanguage);
        if (sourceLanguage is not null)
        {
            foreach (var track in manifest.SubtitleTracks)
            {
                track.LanguageCode = sourceLanguage;
            }
        }

        var effectiveSourceLanguage = LocalLanguageCodes.ResolveProjectSource(manifest);
        if (!string.Equals(
            previousSourceLanguage,
            effectiveSourceLanguage,
            StringComparison.OrdinalIgnoreCase))
        {
            var invalidatedCueIds = manifest.SubtitleTracks
                .SelectMany(track => track.Cues)
                .Where(cue => !cue.TranslationLocked && !string.IsNullOrWhiteSpace(cue.TranslatedText))
                .Select(cue =>
                {
                    cue.TranslatedText = string.Empty;
                    return cue.CueId;
                })
                .ToHashSet();
            manifest.AudioTracks.RemoveAll(track =>
                track.Role == "VOICE_CUE"
                && track.CueId is Guid cueId
                && invalidatedCueIds.Contains(cueId));
        }

        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> UpdateOriginalSubtitleRemovalAsync(
        bool enabled,
        string mode,
        double x,
        double y,
        double width,
        double height,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        var normalizedMode = mode.Trim().ToLowerInvariant();
        if (normalizedMode is not ("blur" or "cover"))
        {
            throw new InvalidOperationException("Chế độ xóa phụ đề gốc không hợp lệ.");
        }

        if (!IsValidSubtitleRegion(x, y, width, height))
        {
            throw new InvalidOperationException("Vùng xóa phụ đề phải nằm hoàn toàn bên trong khung hình.");
        }

        manifest.Settings.RemoveOriginalSubtitles = enabled;
        manifest.Settings.OriginalSubtitleRemovalMode = normalizedMode;
        manifest.Settings.OriginalSubtitleRegionX = x;
        manifest.Settings.OriginalSubtitleRegionY = y;
        manifest.Settings.OriginalSubtitleRegionWidth = width;
        manifest.Settings.OriginalSubtitleRegionHeight = height;
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> UpdateSubtitleStyleAsync(
        SubtitleStyleSettings style,
        CancellationToken cancellationToken)
    {
        if (!SubtitleStyleRules.TryValidate(style, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var manifest = RequireProject();
        manifest.Settings.SubtitleStyle = SubtitleStyleRules.Normalize(style);
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> UpdateAudioSettingsAsync(
        bool originalAudioEnabled,
        double originalAudioVolumePercent,
        bool vietnameseVoiceEnabled,
        double vietnameseVoiceVolumePercent,
        CancellationToken cancellationToken)
    {
        if (!IsValidVolume(originalAudioVolumePercent)
            || !IsValidVolume(vietnameseVoiceVolumePercent))
        {
            throw new InvalidOperationException("Âm lượng phải nằm trong khoảng từ 0 đến 100.");
        }

        var manifest = RequireProject();
        manifest.Settings.OriginalAudioEnabled = originalAudioEnabled;
        manifest.Settings.OriginalAudioVolumePercent = originalAudioVolumePercent;
        manifest.Settings.VietnameseVoiceEnabled = vietnameseVoiceEnabled;
        manifest.Settings.VietnameseVoiceVolumePercent = vietnameseVoiceVolumePercent;
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    private static bool IsValidVolume(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 100;

    private static bool IsValidSubtitleRegion(double x, double y, double width, double height) =>
        double.IsFinite(x)
        && double.IsFinite(y)
        && double.IsFinite(width)
        && double.IsFinite(height)
        && x >= 0
        && y >= 0
        && width >= 0.05
        && height >= 0.04
        && x + width <= 1.000001
        && y + height <= 1.000001;

    public async Task<DesktopProjectState> ImportVideoAsync(
        string sourcePath,
        MediaImportMode mode,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (_importCancellation is not null)
        {
            throw new InvalidOperationException("Một video đang được nhập vào dự án.");
        }

        var entitlements = _auth.CurrentState.Entitlements
            ?? throw new InvalidOperationException("Chưa tải được thông tin gói sử dụng.");
        _importCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var inspector = new FfprobeMediaInspector(_paths);
            var importer = new MediaImportService(_paths, _projects, inspector);
            var progress = new Progress<MediaImportProgress>(value =>
                ImportProgressChanged?.Invoke(this, value));
            await importer.ImportAsync(
                manifest,
                sourcePath,
                mode,
                entitlements.Quota.MaxVideoMinutes,
                progress,
                _importCancellation.Token);
            await _session!.FlushAsync(_importCancellation.Token);
            return Map(manifest);
        }
        finally
        {
            _importCancellation.Dispose();
            _importCancellation = null;
        }
    }

    public void CancelImport() => _importCancellation?.Cancel();

    public async Task<DesktopProjectState> ImportSrtAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        await _subtitles.ImportAsync(manifest, filePath, "und", cancellationToken);
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> UpdateSubtitleAsync(
        Guid cueId,
        string original,
        string translated,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        await _subtitles.UpdateCueAsync(
            manifest,
            cueId,
            original,
            translated,
            cancellationToken);
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> SplitSubtitleCueAsync(
        Guid cueId,
        double positionSeconds,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        await _subtitles.SplitCueAsync(
            manifest,
            cueId,
            checked((long)Math.Round(positionSeconds * 1000)),
            cancellationToken);
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> AlignSubtitleCueAsync(
        Guid cueId,
        double positionSeconds,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        await _subtitles.AlignCueStartAsync(
            manifest,
            cueId,
            checked((long)Math.Round(positionSeconds * 1000)),
            cancellationToken);
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> DuplicateSubtitleCueAsync(
        Guid cueId,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        _ = await _subtitles.DuplicateCueAsync(manifest, cueId, cancellationToken);
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> DeleteSubtitleCueAsync(
        Guid cueId,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        await _subtitles.DeleteCueAsync(manifest, cueId, cancellationToken);
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public Task ExportSrtAsync(string filePath, CancellationToken cancellationToken) =>
        _subtitles.ExportAsync(RequireProject(), filePath, cancellationToken);

    public async Task<DesktopProjectState> PrepareAudioAsync(CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (manifest.SourceVideo is null)
        {
            throw new InvalidOperationException("Hãy nhập video trước khi chuẩn hóa audio.");
        }

        var job = await _jobs.EnqueueAsync(
            manifest,
            "EXTRACT_AUDIO",
            ["EXTRACT_AUDIO"],
            cancellationToken);
        await _jobs.StartAsync(
            manifest,
            job.JobId,
            new AudioExtractionJobExecutor(_paths, manifest),
            cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> TranscribeAsync(CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (manifest.SourceVideo is null)
        {
            throw new InvalidOperationException("Hãy nhập video trước khi nhận dạng giọng nói.");
        }

        if (!manifest.SourceVideo.Metadata.HasAudio)
        {
            throw new InvalidOperationException("Video không có audio để nhận dạng giọng nói.");
        }

        var modelProgress = new Progress<LocalModelDownloadProgress>(progress =>
            ModelDownloadProgressChanged?.Invoke(this, progress));
        await _models.DownloadAsync(
            WhisperLocalSpeechRecognizer.ModelId,
            modelProgress,
            cancellationToken);
        _ = await _quotaJobs.StartAsync(
            manifest,
            "TRANSCRIBE_LOCAL",
            "subtitle.transcribe",
            ["EXTRACT_AUDIO", "TRANSCRIBE"],
            EstimateMinutes(manifest),
            new TranscriptionJobExecutor(
                _paths,
                _projects,
                manifest,
                new WhisperLocalSpeechRecognizer(_models)),
            cancellationToken,
            new Dictionary<string, string>
            {
                ["sourceLanguage"] = LocalLanguageCodes.NormalizeSource(manifest.SourceLanguageCode) ?? "auto",
            });
        return Map(manifest);
    }

    public async Task<DesktopProjectState> RunOcrAsync(CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (manifest.SourceVideo is null)
        {
            throw new InvalidOperationException("Hãy nhập video trước khi nhận dạng phụ đề cứng.");
        }

        manifest.Settings.OcrEnabled = true;
        var ocrLanguage = LocalLanguageCodes.NormalizeSource(manifest.Settings.OcrLanguageCode)
            ?? LocalLanguageCodes.ResolveProjectSource(manifest)
            ?? throw new LocalModelException(
                "OCR_LANGUAGE_REQUIRED",
                "Hãy chọn tiếng Trung hoặc tiếng Anh trước khi chạy OCR.");
        if (ocrLanguage is not ("en" or "zh"))
        {
            throw new LocalModelException(
                "OCR_LANGUAGE_UNSUPPORTED",
                "PaddleOCR hiện hỗ trợ phụ đề tiếng Trung hoặc tiếng Anh.");
        }
        if (LocalLanguageCodes.NormalizeSource(manifest.SourceLanguageCode) is null)
        {
            manifest.SourceLanguageCode = ocrLanguage;
        }
        _ = await _quotaJobs.StartAsync(
            manifest,
            "OCR_LOCAL",
            "ocr.detect",
            ["OCR_EXTRACT_FRAMES", "OCR_RECOGNIZE"],
            EstimateMinutes(manifest),
            new OcrJobExecutor(_paths, _projects, manifest, languageCode: ocrLanguage),
            cancellationToken,
            new Dictionary<string, string>
            {
                ["ocrLanguage"] = ocrLanguage,
            });
        return Map(manifest);
    }

    public async Task<DesktopProjectState> TranslateAsync(CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (!manifest.SubtitleTracks.Any(track => track.Cues.Count > 0))
        {
            throw new InvalidOperationException("Hãy nhận dạng hoặc nhập phụ đề trước khi dịch.");
        }

        var sourceLanguage = LocalLanguageCodes.ResolveProjectSource(manifest)
            ?? throw new LocalModelException(
                "TRANSLATION_SOURCE_REQUIRED",
                "Hãy chọn tiếng Trung hoặc tiếng Anh trước khi dịch.");
        var modelId = LocalTranslatorFactory.GetModelId(sourceLanguage);
        manifest.SourceLanguageCode = sourceLanguage;
        manifest.TargetLanguageCode = "vi";
        manifest.Settings.TranslationTarget = "vi";
        manifest.Settings.TranslationModelId = modelId;

        await EnsureLanguageRuntimeAsync(cancellationToken);
        var modelProgress = new Progress<LocalModelDownloadProgress>(progress =>
            ModelDownloadProgressChanged?.Invoke(this, progress));
        await _models.DownloadAsync(
            modelId,
            modelProgress,
            cancellationToken);
        _ = await _quotaJobs.StartAsync(
            manifest,
            "TRANSLATE_LOCAL",
            "subtitle.translate",
            ["TRANSLATE"],
            EstimateMinutes(manifest),
            new TranslationJobExecutor(
                _paths,
                _projects,
                manifest,
                LocalTranslatorFactory.Create(sourceLanguage, _paths, _models)),
            cancellationToken,
            new Dictionary<string, string>
            {
                ["sourceLanguage"] = sourceLanguage,
                ["targetLanguage"] = "vi",
                ["modelId"] = modelId,
                ["modelVersion"] = sourceLanguage == "zh"
                    ? OpusMtChineseVietnameseTranslator.ModelVersion
                    : ArgosLocalTranslator.ModelId,
            });
        return Map(manifest);
    }

    public async Task<DesktopProjectState> SynthesizeVoiceAsync(CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        var invalidTranslationCount = manifest.SubtitleTracks
            .SelectMany(track => track.Cues)
            .Count(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText)
                && TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText));
        if (invalidTranslationCount > 0)
        {
            throw new InvalidOperationException(
                $"Có {invalidTranslationCount} bản dịch bị lặp hoặc dài bất thường. Hãy dịch lại lỗi trước khi tạo giọng Việt.");
        }

        if (!manifest.SubtitleTracks.SelectMany(track => track.Cues)
            .Any(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText)))
        {
            throw new InvalidOperationException("Hãy dịch phụ đề trước khi tạo giọng Việt.");
        }

        await EnsureLanguageRuntimeAsync(cancellationToken);
        var modelProgress = new Progress<LocalModelDownloadProgress>(progress =>
            ModelDownloadProgressChanged?.Invoke(this, progress));
        await _models.DownloadAsync(
            PiperLocalVoiceSynthesizer.ModelId,
            modelProgress,
            cancellationToken);
        _ = await _quotaJobs.StartAsync(
            manifest,
            "SYNTHESIZE_VOICE_LOCAL",
            "voice.generate",
            ["SYNTHESIZE_VOICE", "SYNC_VOICE"],
            EstimateMinutes(manifest),
            new VoiceGenerationJobExecutor(
                new VoiceSynthesisJobExecutor(
                    _paths,
                    _projects,
                    manifest,
                    new PiperLocalVoiceSynthesizer(_paths, _models)),
                new VoiceTimelineJobExecutor(_paths, _projects, manifest)),
            cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> ExportVideoAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (manifest.SourceVideo is null)
        {
            throw new InvalidOperationException("Hãy nhập video trước khi xuất.");
        }

        var includeOriginalAudio = manifest.Settings.OriginalAudioEnabled
            && manifest.SourceVideo.Metadata.HasAudio;
        var includeVietnameseVoice = manifest.Settings.VietnameseVoiceEnabled;
        if (!includeOriginalAudio && !includeVietnameseVoice)
        {
            throw new InvalidOperationException("Hãy bật Âm gốc hoặc Giọng Việt trước khi xuất video.");
        }

        if (includeVietnameseVoice
            && !manifest.AudioTracks.Any(item => item.Role == "VOICE_CUE"))
        {
            throw new InvalidOperationException("Hãy tạo giọng Việt trước khi xuất video.");
        }

        var steps = includeVietnameseVoice
            ? new[] { "SYNC_VOICE", "EXPORT_VIDEO" }
            : ["EXPORT_VIDEO"];
        ILocalJobExecutor executor = includeVietnameseVoice
            ? new FullExportJobExecutor(
                new VoiceTimelineJobExecutor(_paths, _projects, manifest),
                new VideoExportJobExecutor(_paths, _projects, manifest))
            : new VideoExportJobExecutor(_paths, _projects, manifest);
        _ = await _quotaJobs.StartAsync(
            manifest,
            "EXPORT_VIDEO_LOCAL",
            "video.export",
            steps,
            EstimateMinutes(manifest),
            executor,
            cancellationToken,
            new Dictionary<string, string>
            {
                [VideoExportJobExecutor.DestinationParameter] = Path.GetFullPath(destinationPath),
            });
        return Map(manifest);
    }

    public async Task<DesktopProjectState> PauseJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        await _jobs.PauseAsync(manifest, jobId, cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> ResumeJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        var job = manifest.Jobs.Single(item => item.JobId == jobId);
        await EnsureJobDependenciesAsync(job, cancellationToken);
        var executor = CreateExecutor(manifest, jobId);
        if (job.QuotaReservationId is null)
        {
            await _jobs.StartAsync(manifest, jobId, executor, cancellationToken);
        }
        else
        {
            _ = await _quotaJobs.RestartAsync(
                manifest,
                job,
                GetFeatureCode(job.JobType),
                EstimateMinutes(manifest),
                executor,
                cancellationToken);
        }

        return Map(manifest);
    }

    public async Task<DesktopProjectState> RetryJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        var job = manifest.Jobs.Single(item => item.JobId == jobId);
        await EnsureJobDependenciesAsync(job, cancellationToken);
        var executor = CreateExecutor(manifest, jobId);
        if (job.QuotaReservationId is null)
        {
            await _jobs.RetryAsync(manifest, jobId, executor, cancellationToken);
        }
        else
        {
            _ = await _quotaJobs.RestartAsync(
                manifest,
                job,
                GetFeatureCode(job.JobType),
                EstimateMinutes(manifest),
                executor,
                cancellationToken);
        }

        return Map(manifest);
    }

    public async Task<DesktopProjectState> CancelJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        await _jobs.CancelAsync(manifest, jobId, cancellationToken);
        return Map(manifest);
    }

    public DesktopProjectState GetCurrentState() => Map(RequireProject());

    public string GetCurrentSourcePath()
    {
        var manifest = RequireProject();
        var source = manifest.SourceVideo
            ?? throw new FileNotFoundException("Dự án chưa có video nguồn.");
        var path = source.ImportMode == "COPY" && source.WorkspaceRelativePath is not null
            ? _paths.GetProjectPath(manifest.ProjectId, source.WorkspaceRelativePath)
            : Path.GetFullPath(source.OriginalPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Video nguồn đã bị di chuyển hoặc không còn tồn tại.", path);
        }

        return path;
    }

    public string GetCurrentVoiceTimelinePath()
    {
        var manifest = RequireProject();
        var voice = manifest.AudioTracks.LastOrDefault(item => item.Role == "VOICE_TIMELINE")
            ?? throw new FileNotFoundException("Dự án chưa có timeline giọng Việt.");
        if (string.IsNullOrWhiteSpace(voice.WorkspaceRelativePath))
        {
            throw new FileNotFoundException("Timeline giọng Việt không có đường dẫn hợp lệ.");
        }

        var path = _paths.GetProjectPath(manifest.ProjectId, voice.WorkspaceRelativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Timeline giọng Việt không còn tồn tại.", path);
        }

        return path;
    }

    public async Task CloseCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return;
        }

        await _session.CloseAsync(cancellationToken);
        await _session.DisposeAsync();
        _session = null;
    }

    private async Task EnsureLanguageRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = new LocalWorkerRuntimeLocator(_paths).RequirePython();
            return;
        }
        catch (LocalModelException exception) when (exception.Code == "LOCAL_PYTHON_MISSING")
        {
            // Install the isolated runtime below.
        }

        var progress = new Progress<LocalRuntimeProgress>(value =>
            RuntimeProgressChanged?.Invoke(this, value));
        await _runtimeProvisioner.EnsureReadyAsync(progress, cancellationToken);
    }

    private static decimal EstimateMinutes(ProjectManifest manifest)
    {
        var durationMinutes = (decimal)(manifest.SourceVideo?.Metadata.DurationSeconds ?? 0) / 60m;
        return Math.Max(0.01m, Math.Ceiling(durationMinutes * 100m) / 100m);
    }

    private async Task EnsureJobDependenciesAsync(
        LocalJob job,
        CancellationToken cancellationToken)
    {
        var modelProgress = new Progress<LocalModelDownloadProgress>(progress =>
            ModelDownloadProgressChanged?.Invoke(this, progress));
        switch (job.JobType)
        {
            case "TRANSCRIBE_LOCAL":
                await _models.DownloadAsync(
                    WhisperLocalSpeechRecognizer.ModelId,
                    modelProgress,
                    cancellationToken);
                break;
            case "TRANSLATE_LOCAL":
                await EnsureLanguageRuntimeAsync(cancellationToken);
                var sourceLanguage = job.Parameters.GetValueOrDefault("sourceLanguage")
                    ?? LocalLanguageCodes.ResolveProjectSource(RequireProject())
                    ?? throw new LocalModelException(
                        "TRANSLATION_SOURCE_REQUIRED",
                        "Không xác định được ngôn ngữ nguồn của job dịch.");
                await _models.DownloadAsync(
                    LocalTranslatorFactory.GetModelId(sourceLanguage),
                    modelProgress,
                    cancellationToken);
                break;
            case "SYNTHESIZE_VOICE_LOCAL":
                await EnsureLanguageRuntimeAsync(cancellationToken);
                await _models.DownloadAsync(
                    PiperLocalVoiceSynthesizer.ModelId,
                    modelProgress,
                    cancellationToken);
                break;
        }
    }

    private static string GetFeatureCode(string jobType) => jobType switch
    {
        "TRANSCRIBE_LOCAL" => "subtitle.transcribe",
        "OCR_LOCAL" => "ocr.detect",
        "TRANSLATE_LOCAL" => "subtitle.translate",
        "SYNTHESIZE_VOICE_LOCAL" => "voice.generate",
        "EXPORT_VIDEO_LOCAL" => "video.export",
        _ => throw new InvalidOperationException("Loáº¡i job khÃ´ng sá»­ dá»¥ng háº¡n má»©c Server."),
    };

    private AccountResponse RequireAccount() =>
        _auth.CurrentState.IsAuthenticated && _auth.CurrentState.Account is not null
            ? _auth.CurrentState.Account
            : throw new UnauthorizedAccessException("Vui lòng đăng nhập để quản lý dự án.");

    private ProjectManifest RequireProject() =>
        CurrentProject ?? throw new InvalidOperationException("Hãy tạo hoặc mở một dự án trước.");

    private ILocalJobExecutor CreateExecutor(ProjectManifest manifest, Guid jobId)
    {
        var job = manifest.Jobs.SingleOrDefault(item => item.JobId == jobId)
            ?? throw new KeyNotFoundException("Không tìm thấy job trong dự án.");
        return job.JobType switch
        {
            "EXTRACT_AUDIO" => new AudioExtractionJobExecutor(_paths, manifest),
            "TRANSCRIBE_LOCAL" => new TranscriptionJobExecutor(
                _paths,
                _projects,
                manifest,
                new WhisperLocalSpeechRecognizer(_models),
                languageCode: job.Parameters.GetValueOrDefault("sourceLanguage")),
            "OCR_LOCAL" => new OcrJobExecutor(
                _paths,
                _projects,
                manifest,
                languageCode: job.Parameters.GetValueOrDefault("ocrLanguage")),
            "TRANSLATE_LOCAL" => new TranslationJobExecutor(
                _paths,
                _projects,
                manifest,
                LocalTranslatorFactory.Create(
                    job.Parameters.GetValueOrDefault("sourceLanguage")
                        ?? LocalLanguageCodes.ResolveProjectSource(manifest)
                        ?? throw new LocalModelException(
                            "TRANSLATION_SOURCE_REQUIRED",
                            "Không xác định được ngôn ngữ nguồn của job dịch."),
                    _paths,
                    _models)),
            "SYNTHESIZE_VOICE_LOCAL" => job.Steps.Any(item => item.Code == "SYNC_VOICE")
                ? new VoiceGenerationJobExecutor(
                    new VoiceSynthesisJobExecutor(
                        _paths,
                        _projects,
                        manifest,
                        new PiperLocalVoiceSynthesizer(_paths, _models)),
                    new VoiceTimelineJobExecutor(_paths, _projects, manifest))
                : new VoiceSynthesisJobExecutor(
                    _paths,
                    _projects,
                    manifest,
                    new PiperLocalVoiceSynthesizer(_paths, _models)),
            "EXPORT_VIDEO_LOCAL" => job.Steps.Any(item => item.Code == "SYNC_VOICE")
                ? new FullExportJobExecutor(
                    new VoiceTimelineJobExecutor(_paths, _projects, manifest),
                    new VideoExportJobExecutor(_paths, _projects, manifest))
                : new VideoExportJobExecutor(_paths, _projects, manifest),
            _ => throw new InvalidOperationException("Loại job chưa có bộ thực thi cục bộ."),
        };
    }

    private static DesktopProjectState Map(ProjectManifest manifest)
    {
        DesktopVideoInfo? video = null;
        if (manifest.SourceVideo is not null)
        {
            var source = manifest.SourceVideo;
            video = new DesktopVideoInfo(
                source.FileName,
                Path.GetExtension(source.FileName).TrimStart('.').ToUpperInvariant(),
                source.SizeBytes,
                source.Metadata.DurationSeconds,
                source.Metadata.Width,
                source.Metadata.Height,
                source.Metadata.FramesPerSecond,
                source.Metadata.VideoCodec,
                source.Metadata.AudioCodec,
                source.Metadata.AudioTrackCount,
                source.Metadata.HasAudio,
                source.ImportMode,
                source.Sha256,
                PlaybackUrl);
        }

        var subtitleCues = new List<DesktopSubtitleCue>();
        var voicedCueIds = manifest.AudioTracks
            .Where(item => item.Role == "VOICE_CUE" && item.CueId.HasValue)
            .Select(item => item.CueId!.Value)
            .ToHashSet();
        var track = manifest.SubtitleTracks.LastOrDefault();
        if (track is not null)
        {
            for (var index = 0; index < track.Cues.Count; index++)
            {
                var cue = track.Cues[index];
                var overlap = index > 0 && cue.StartMilliseconds < track.Cues[index - 1].EndMilliseconds;
                var hasVoice = voicedCueIds.Contains(cue.CueId);
                var invalidTranslation = !string.IsNullOrWhiteSpace(cue.TranslatedText)
                    && TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText);
                var status = invalidTranslation
                    ? "invalid-translation"
                    : string.IsNullOrWhiteSpace(cue.TranslatedText) || overlap
                    ? "review"
                    : hasVoice ? "translated" : "missing-audio";
                subtitleCues.Add(new DesktopSubtitleCue(
                    cue.CueId,
                    index + 1,
                    cue.StartMilliseconds / 1000d,
                    cue.EndMilliseconds / 1000d,
                    cue.OriginalText,
                    cue.TranslatedText,
                    status,
                    overlap,
                    hasVoice));
            }
        }

        var sourceLanguage = LocalLanguageCodes.NormalizeSource(manifest.SourceLanguageCode);
        var translationModelId = sourceLanguage switch
        {
            "zh" => OpusMtChineseVietnameseTranslator.ModelId,
            "en" => ArgosLocalTranslator.ModelId,
            _ => "auto",
        };
        var subtitleStyle = SubtitleStyleRules.Normalize(manifest.Settings.SubtitleStyle);
        return new DesktopProjectState(
            manifest.ProjectId,
            manifest.Name,
            manifest.Status,
            manifest.RecoveryRequired,
            manifest.ServerSynchronized,
            manifest.UpdatedAtUtc,
            sourceLanguage ?? "auto",
            string.IsNullOrWhiteSpace(manifest.TargetLanguageCode) ? "vi" : manifest.TargetLanguageCode,
            new DesktopProjectSettings(
                manifest.Settings.SpeechModel,
                LocalLanguageCodes.NormalizeSetting(manifest.Settings.OcrLanguageCode),
                translationModelId,
                manifest.Settings.OriginalAudioEnabled,
                manifest.Settings.OriginalAudioVolumePercent,
                manifest.Settings.VietnameseVoiceEnabled,
                manifest.Settings.VietnameseVoiceVolumePercent,
                manifest.Settings.RemoveOriginalSubtitles,
                manifest.Settings.OriginalSubtitleRemovalMode,
                manifest.Settings.OriginalSubtitleRegionX,
                manifest.Settings.OriginalSubtitleRegionY,
                manifest.Settings.OriginalSubtitleRegionWidth,
                manifest.Settings.OriginalSubtitleRegionHeight,
                new DesktopSubtitleStyleSettings(
                    subtitleStyle.PresetId,
                    subtitleStyle.FontFamily,
                    subtitleStyle.FontSizePercent,
                    subtitleStyle.Bold,
                    subtitleStyle.TextColor,
                    subtitleStyle.OutlineColor,
                    subtitleStyle.OutlineSize,
                    subtitleStyle.ShadowSize,
                    subtitleStyle.BackgroundMode,
                    subtitleStyle.BackgroundColor,
                    subtitleStyle.BackgroundOpacity,
                    subtitleStyle.HorizontalAlignment,
                    subtitleStyle.VerticalPosition,
                    subtitleStyle.PositionXPercent,
                    subtitleStyle.PositionYPercent,
                    subtitleStyle.MaxWidthPercent,
                    subtitleStyle.MaxLines)),
            video,
            manifest.AudioTracks.Any(item =>
                item.Role == "VOICE_TIMELINE"
                && !string.IsNullOrWhiteSpace(item.WorkspaceRelativePath))
                ? VoicePlaybackUrl
                : null,
            subtitleCues,
            manifest.Jobs);
    }

    public async ValueTask DisposeAsync()
    {
        CancelImport();
        await _jobs.DisposeAsync();
        await CloseCurrentAsync();
        _models.Dispose();
        _runtimeProvisioner.Dispose();
    }
}

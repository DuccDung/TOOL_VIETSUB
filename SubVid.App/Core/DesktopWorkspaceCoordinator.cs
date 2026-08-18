using SubVid.App.Api;
using SubVid.App.Jobs;
using SubVid.App.LocalAi;
using SubVid.App.Media;
using SubVid.App.Usage;
using SubVid.App.Subtitles;
using SubVid.App.Translation;

namespace SubVid.App.Core;

public sealed class DesktopWorkspaceCoordinator : IAsyncDisposable
{
    public const string PlaybackUrl = "https://media.subvid.local/video";
    public const string VoicePlaybackUrl = "https://media.subvid.local/voice";
    public const int MaxSubtitleRemovalRegions = 10;
    private readonly AuthSessionManager _auth;
    private readonly AppPaths _paths = new();
    private readonly ProjectWorkspaceService _projects;
    private readonly PersistentJobManager _jobs;
    private readonly QuotaProtectedJobService _quotaJobs;
    private readonly SrtService _subtitles;
    private LocalModelManager _models;
    private LocalAiRuntimeProvisioner _runtimeProvisioner;
    private VieNeuRuntimeProvisioner _vieNeuRuntimeProvisioner;
    private readonly AiStorageService _aiStorage;
    private readonly IDesktopCloudAccessGateway _cloudAccess;
    private readonly CloudUsageSettlementReconciler _cloudSettlementReconciler;
    private readonly ProtectedVoiceCredentialStore _voiceCredentials;
    private readonly HttpClient _translationHttpClient;
    private readonly HttpClient _voiceHttpClient;
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
        _vieNeuRuntimeProvisioner = new VieNeuRuntimeProvisioner(_paths);
        _aiStorage = new AiStorageService(_paths);
        _cloudAccess = new DesktopCloudAccessGateway(auth);
        _cloudSettlementReconciler = new CloudUsageSettlementReconciler(_cloudAccess, _projects);
        _voiceCredentials = new ProtectedVoiceCredentialStore(_paths);
        _translationHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _translationHttpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("SubVid-App/1.0");
        _voiceHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _voiceHttpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("SubVid-App/1.0");
        _jobs.JobChanged += (_, job) => JobChanged?.Invoke(this, job);
    }

    public ProjectManifest? CurrentProject => _session?.Manifest;

    public AiStorageStatus AiStorageStatus => _aiStorage.GetStatus();

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
        await _cloudSettlementReconciler.ReconcileAsync(manifest, cancellationToken);
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
        await _cloudSettlementReconciler.ReconcileAsync(manifest, cancellationToken);
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
        if (TranslationProviders.Normalize(manifest.Settings.TranslationProvider) == TranslationProviders.Local)
        {
            manifest.Settings.TranslationModelId = sourceLanguage is null
                ? "auto"
                : LocalTranslatorFactory.GetModelId(sourceLanguage);
        }
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
                    cue.TranslationModelId = null;
                    cue.TranslationModelVersion = null;
                    cue.TranslationSourceFingerprint = null;
                    cue.TranslationQualityStatus = null;
                    cue.TranslationConfidence = null;
                    cue.TranslationWarnings = [];
                    cue.TranslationReviewedAtUtc = null;
                    return cue.CueId;
                })
                .ToHashSet();
            manifest.AudioTracks.RemoveAll(track =>
                track.Role == "VOICE_TIMELINE"
                || (track.Role == "VOICE_CUE"
                    && track.CueId is Guid cueId
                    && invalidatedCueIds.Contains(cueId)));
        }

        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> UpdateTranslationSettingsAsync(
        string provider,
        string modelId,
        string qualityMode,
        bool reviewEnabled,
        bool fallbackToLocal,
        string projectContext,
        string characterInstructions,
        string styleInstructions,
        string glossaryText,
        string? apiKey,
        bool clearApiKey,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        manifest.TranslationContext ??= new ProjectTranslationContext();
        manifest.TranslationGlossary ??= [];
        manifest.TranslationMemory ??= [];
        var normalizedProvider = TranslationProviders.Normalize(provider);
        var normalizedQuality = TranslationQualityModes.Normalize(qualityMode);
        var normalizedContext = NormalizeTranslationText(projectContext, 4000, "Bối cảnh dự án");
        var normalizedCharacters = NormalizeTranslationText(characterInstructions, 4000, "Thông tin nhân vật");
        var normalizedStyle = NormalizeTranslationText(styleInstructions, 2000, "Phong cách dịch");
        var sourceLanguage = LocalLanguageCodes.ResolveProjectSource(manifest) ?? "en";
        var resolvedModel = TranslationModelDefaults.Resolve(
            normalizedProvider,
            modelId,
            normalizedQuality,
            sourceLanguage);
        if (resolvedModel.Length > 120 || resolvedModel.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new InvalidOperationException("Tên model dịch không hợp lệ.");
        }

        var glossary = ParseGlossary(glossaryText, manifest.TranslationGlossary);
        manifest.Settings.TranslationProvider = normalizedProvider;
        manifest.Settings.TranslationModelId = resolvedModel;
        manifest.Settings.TranslationQualityMode = normalizedQuality;
        manifest.Settings.TranslationReviewEnabled = reviewEnabled;
        manifest.Settings.TranslationFallbackToLocal = fallbackToLocal;
        manifest.TranslationContext.Summary = normalizedContext;
        manifest.TranslationContext.CharacterInstructions = normalizedCharacters;
        manifest.TranslationContext.StyleInstructions = normalizedStyle;
        manifest.TranslationGlossary = glossary;
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
        IReadOnlyList<SubtitleRemovalRegionSettings> regions,
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

        if (regions.Count is < 1 or > MaxSubtitleRemovalRegions
            || regions.Any(region => !IsValidSubtitleRegion(
                region.X,
                region.Y,
                region.Width,
                region.Height)))
        {
            throw new InvalidOperationException("Danh sách vùng che không hợp lệ.");
        }

        var normalizedRegions = new List<SubtitleRemovalRegionSettings>(regions.Count);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in regions)
        {
            var id = region.Id?.Trim() ?? string.Empty;
            if (id.Length is < 1 or > 64
                || id.Any(char.IsControl)
                || !usedIds.Add(id))
            {
                do
                {
                    id = Guid.NewGuid().ToString("N");
                }
                while (!usedIds.Add(id));
            }

            normalizedRegions.Add(new SubtitleRemovalRegionSettings
            {
                Id = id,
                X = region.X,
                Y = region.Y,
                Width = region.Width,
                Height = region.Height,
            });
        }

        var primaryRegion = normalizedRegions[0];
        manifest.Settings.RemoveOriginalSubtitles = enabled;
        manifest.Settings.OriginalSubtitleRemovalMode = normalizedMode;
        manifest.Settings.OriginalSubtitleRegionX = primaryRegion.X;
        manifest.Settings.OriginalSubtitleRegionY = primaryRegion.Y;
        manifest.Settings.OriginalSubtitleRegionWidth = primaryRegion.Width;
        manifest.Settings.OriginalSubtitleRegionHeight = primaryRegion.Height;
        manifest.Settings.OriginalSubtitleRemovalRegions = normalizedRegions;
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> UpdateVideoTransformAsync(
        bool flipHorizontal,
        bool flipVertical,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        var settings = manifest.Settings;
        if (settings.FlipHorizontal == flipHorizontal
            && settings.FlipVertical == flipVertical)
        {
            return Map(manifest);
        }

        settings.OriginalSubtitleRemovalRegions ??= [];
        if (settings.OriginalSubtitleRemovalRegions.Count == 0)
        {
            settings.OriginalSubtitleRemovalRegions.Add(new SubtitleRemovalRegionSettings
            {
                Id = "legacy",
                X = settings.OriginalSubtitleRegionX,
                Y = settings.OriginalSubtitleRegionY,
                Width = settings.OriginalSubtitleRegionWidth,
                Height = settings.OriginalSubtitleRegionHeight,
            });
        }

        if (settings.FlipHorizontal != flipHorizontal)
        {
            foreach (var region in settings.OriginalSubtitleRemovalRegions)
            {
                region.X = MirrorRegionCoordinate(region.X, region.Width);
            }
        }

        if (settings.FlipVertical != flipVertical)
        {
            foreach (var region in settings.OriginalSubtitleRemovalRegions)
            {
                region.Y = MirrorRegionCoordinate(region.Y, region.Height);
            }
        }

        var primaryRegion = settings.OriginalSubtitleRemovalRegions[0];
        settings.OriginalSubtitleRegionX = primaryRegion.X;
        settings.OriginalSubtitleRegionY = primaryRegion.Y;
        settings.OriginalSubtitleRegionWidth = primaryRegion.Width;
        settings.OriginalSubtitleRegionHeight = primaryRegion.Height;

        settings.FlipHorizontal = flipHorizontal;
        settings.FlipVertical = flipVertical;
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

    public async Task<DesktopProjectState> UpdateVietnameseSubtitlesEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        manifest.Settings.VietnameseSubtitlesEnabled = enabled;
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

    public async Task<DesktopProjectState> UpdateVoiceSettingsAsync(
        string defaultVoiceId,
        IReadOnlyDictionary<string, string> speakerVoiceIds,
        CancellationToken cancellationToken) =>
        await UpdateVoiceSettingsAsync(
            defaultVoiceId,
            speakerVoiceIds,
            RequireProject().Settings.VoiceSpeed,
            cancellationToken);

    public async Task<DesktopProjectState> UpdateVoiceSettingsAsync(
        string defaultVoiceId,
        IReadOnlyDictionary<string, string> speakerVoiceIds,
        int speed,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        var normalizedSpeed = Math.Clamp(speed, -3, 3);
        var defaultVoice = LocalVoiceCatalog.Find(defaultVoiceId)
            ?? throw new InvalidOperationException("Giọng đọc mặc định không hợp lệ.");
        var normalizedMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (speaker, voiceId) in speakerVoiceIds)
        {
            var normalizedSpeaker = NormalizeSpeaker(speaker);
            var voice = LocalVoiceCatalog.Find(voiceId)
                ?? throw new InvalidOperationException($"Giọng đọc của nhân vật '{normalizedSpeaker}' không hợp lệ.");
            normalizedMappings[normalizedSpeaker] = voice.VoiceId;
        }

        var cues = manifest.SubtitleTracks.SelectMany(track => track.Cues).ToArray();
        var previousVoiceIds = cues.ToDictionary(
            cue => cue.CueId,
            cue => LocalVoiceCatalog.Resolve(manifest, cue).VoiceId);
        var previousSpeed = Math.Clamp(manifest.Settings.VoiceSpeed, -3, 3);
        manifest.Settings.VoiceId = defaultVoice.VoiceId;
        manifest.Settings.SpeakerVoiceIds = normalizedMappings;
        manifest.Settings.VoiceSpeed = normalizedSpeed;
        var changedCueIds = cues
            .Where(cue =>
            {
                var currentVoice = LocalVoiceCatalog.Resolve(manifest, cue);
                return !string.Equals(
                        previousVoiceIds[cue.CueId],
                        currentVoice.VoiceId,
                        StringComparison.OrdinalIgnoreCase)
                    || (currentVoice.Engine == LocalVoiceEngines.Fpt && previousSpeed != normalizedSpeed);
            })
            .Select(cue => cue.CueId)
            .ToHashSet();
        InvalidateVoice(manifest, changedCueIds);
        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> UpdateFptVoiceCredentialAsync(
        string? apiKey,
        bool clearApiKey,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (clearApiKey)
        {
            _voiceCredentials.DeleteFptKey();
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _voiceCredentials.SaveFptKey(apiKey);
        }

        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> UpdateSubtitleVoiceAsync(
        Guid cueId,
        string speaker,
        string? voiceId,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        var cue = manifest.SubtitleTracks
            .SelectMany(track => track.Cues)
            .SingleOrDefault(item => item.CueId == cueId)
            ?? throw new InvalidOperationException("Không tìm thấy phân đoạn phụ đề.");
        var normalizedVoiceId = string.IsNullOrWhiteSpace(voiceId)
            ? null
            : LocalVoiceCatalog.Find(voiceId)?.VoiceId
                ?? throw new InvalidOperationException("Giọng đọc của phân đoạn không hợp lệ.");
        var previousVoiceId = LocalVoiceCatalog.Resolve(manifest, cue).VoiceId;
        cue.Speaker = NormalizeSpeaker(speaker);
        cue.VoiceId = normalizedVoiceId;
        var currentVoiceId = LocalVoiceCatalog.Resolve(manifest, cue).VoiceId;
        if (!string.Equals(previousVoiceId, currentVoiceId, StringComparison.OrdinalIgnoreCase))
        {
            InvalidateVoice(manifest, new HashSet<Guid> { cue.CueId });
        }

        await _session!.FlushAsync(cancellationToken);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> InstallVoiceAsync(
        string voiceId,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        var voice = LocalVoiceCatalog.Find(voiceId)
            ?? throw new InvalidOperationException("Giọng đọc cần cài không hợp lệ.");
        if (voice.IsCloud)
        {
            throw new InvalidOperationException("Giọng FPT.AI không cần cài model. Hãy cấu hình API key để sử dụng.");
        }
        if (voice.Engine == LocalVoiceEngines.Piper)
        {
            await EnsureLanguageRuntimeAsync(cancellationToken);
            var progress = new Progress<LocalModelDownloadProgress>(value =>
                ModelDownloadProgressChanged?.Invoke(this, value));
            await _models.DownloadAsync(PiperLocalVoiceSynthesizer.ModelId, progress, cancellationToken);
        }
        else
        {
            var runtimeProgress = new Progress<LocalRuntimeProgress>(value =>
                RuntimeProgressChanged?.Invoke(this, value));
            await _vieNeuRuntimeProvisioner.EnsureReadyAsync(runtimeProgress, cancellationToken);
            RuntimeProgressChanged?.Invoke(this, new LocalRuntimeProgress(
                "VOICE_MODEL",
                0,
                "Đang tải bộ giọng VieNeu lần đầu. Quá trình này có thể mất vài phút."));
            await new VieNeuLocalVoiceSynthesizer(_paths).EnsureReadyAsync(cancellationToken);
            RuntimeProgressChanged?.Invoke(this, new LocalRuntimeProgress(
                "VOICE_MODEL",
                100,
                "Bộ giọng VieNeu đã sẵn sàng."));
        }

        return Map(manifest);
    }

    public async Task<DesktopProjectState> ChangeAiStorageAsync(
        string destinationRoot,
        bool migrateExisting,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (manifest.Jobs.Any(job => job.Status is LocalJobStatus.Pending or LocalJobStatus.Running or LocalJobStatus.Paused))
        {
            throw new LocalModelException(
                "AI_STORAGE_BUSY",
                "Hãy chờ hoặc hủy các job AI đang chạy trước khi đổi thư mục lưu.");
        }

        var progress = new Progress<LocalRuntimeProgress>(value =>
            RuntimeProgressChanged?.Invoke(this, value));
        await _aiStorage.ChangeRootAsync(
            destinationRoot,
            migrateExisting,
            progress,
            cancellationToken);

        _models.Dispose();
        _runtimeProvisioner.Dispose();
        _vieNeuRuntimeProvisioner.Dispose();
        _models = new LocalModelManager(_paths);
        _runtimeProvisioner = new LocalAiRuntimeProvisioner(_paths);
        _vieNeuRuntimeProvisioner = new VieNeuRuntimeProvisioner(_paths);
        return Map(manifest);
    }

    public async Task<DesktopProjectState> DiscardPendingAiStorageMigrationAsync(
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (manifest.Jobs.Any(job => job.Status is LocalJobStatus.Pending or LocalJobStatus.Running or LocalJobStatus.Paused))
        {
            throw new LocalModelException(
                "AI_STORAGE_BUSY",
                "Hãy chờ hoặc hủy các job AI đang chạy trước khi dọn migration.");
        }

        await _aiStorage.DiscardPendingMigrationAsync(cancellationToken);
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

    private static IReadOnlyList<SubtitleRemovalRegionSettings> GetEffectiveSubtitleRemovalRegions(
        ProjectSettings settings)
    {
        if (settings.OriginalSubtitleRemovalRegions is { Count: > 0 })
        {
            return settings.OriginalSubtitleRemovalRegions;
        }

        return
        [
            new SubtitleRemovalRegionSettings
            {
                Id = "legacy",
                X = settings.OriginalSubtitleRegionX,
                Y = settings.OriginalSubtitleRegionY,
                Width = settings.OriginalSubtitleRegionWidth,
                Height = settings.OriginalSubtitleRegionHeight,
            },
        ];
    }

    private static double MirrorRegionCoordinate(double position, double size)
    {
        if (!double.IsFinite(position)
            || !double.IsFinite(size)
            || size < 0
            || size > 1)
        {
            return position;
        }

        return Math.Clamp(1d - position - size, 0d, 1d - size);
    }

    private static string NormalizeSpeaker(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 80 || normalized.Any(char.IsControl))
        {
            throw new InvalidOperationException("Tên nhân vật phải có từ 1 đến 80 ký tự hợp lệ.");
        }

        return normalized;
    }

    private static void InvalidateVoice(ProjectManifest manifest, IReadOnlySet<Guid> cueIds)
    {
        if (cueIds.Count == 0)
        {
            return;
        }

        manifest.AudioTracks.RemoveAll(item =>
            item.Role == "VOICE_TIMELINE"
            || (item.Role == "VOICE_CUE"
                && item.CueId is Guid cueId
                && cueIds.Contains(cueId)));
    }

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

    public async Task<DesktopProjectState> TranslateAsync(
        CancellationToken cancellationToken,
        string? translationRunMode = null)
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
        var providerId = TranslationProviders.Normalize(manifest.Settings.TranslationProvider);
        var runMode = TranslationRunModes.Normalize(translationRunMode);
        var modelId = TranslationModelDefaults.Resolve(
            providerId,
            manifest.Settings.TranslationModelId,
            manifest.Settings.TranslationQualityMode,
            sourceLanguage);
        manifest.SourceLanguageCode = sourceLanguage;
        manifest.TargetLanguageCode = "vi";
        manifest.Settings.TranslationTarget = "vi";
        manifest.Settings.TranslationProvider = providerId;
        manifest.Settings.TranslationModelId = modelId;

        await EnsureTranslationDependenciesAsync(
            providerId,
            sourceLanguage,
            manifest.Settings.TranslationFallbackToLocal,
            cancellationToken);
        var provider = CreateTranslationProvider(
            providerId,
            modelId,
            sourceLanguage,
            manifest.Settings.TranslationFallbackToLocal);
        _ = await _quotaJobs.StartAsync(
            manifest,
            providerId == TranslationProviders.Local ? "TRANSLATE_LOCAL" : "TRANSLATE_CLOUD",
            "subtitle.translate",
            ["TRANSLATE"],
            EstimateMinutes(manifest),
            new TranslationJobExecutor(
                _paths,
                _projects,
                manifest,
                provider),
            cancellationToken,
            new Dictionary<string, string>
            {
                ["sourceLanguage"] = sourceLanguage,
                ["targetLanguage"] = "vi",
                ["provider"] = providerId,
                ["modelId"] = modelId,
                ["fallbackToLocal"] = manifest.Settings.TranslationFallbackToLocal.ToString(),
                [TranslationRunModes.ParameterName] = runMode,
            });
        return Map(manifest);
    }

    public async Task<DesktopProjectState> SynthesizeVoiceAsync(
        string? requestedDefaultVoiceId,
        CancellationToken cancellationToken) =>
        await SynthesizeVoiceAsync(
            requestedDefaultVoiceId,
            requestedSpeed: null,
            apiKey: null,
            cancellationToken: cancellationToken);

    public async Task<DesktopProjectState> SynthesizeVoiceAsync(
        string? requestedDefaultVoiceId,
        int? requestedSpeed,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var manifest = RequireProject();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _voiceCredentials.SaveFptKey(apiKey);
        }

        if (!string.IsNullOrWhiteSpace(requestedDefaultVoiceId))
        {
            await UpdateVoiceSettingsAsync(
                requestedDefaultVoiceId,
                manifest.Settings.SpeakerVoiceIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                requestedSpeed ?? manifest.Settings.VoiceSpeed,
                cancellationToken);
            manifest = RequireProject();
        }
        else if (requestedSpeed.HasValue && requestedSpeed.Value != manifest.Settings.VoiceSpeed)
        {
            await UpdateVoiceSettingsAsync(
                LocalVoiceCatalog.Resolve(manifest.Settings.VoiceId).VoiceId,
                manifest.Settings.SpeakerVoiceIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                requestedSpeed.Value,
                cancellationToken);
            manifest = RequireProject();
        }

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

        var resolvedVoices = manifest.SubtitleTracks
            .SelectMany(track => track.Cues)
            .Where(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText))
            .Select(cue => LocalVoiceCatalog.Resolve(manifest, cue))
            .ToArray();
        var usesFpt = resolvedVoices.Any(voice => voice.Engine == LocalVoiceEngines.Fpt);
        if (usesFpt && !_voiceCredentials.HasFptKey)
        {
            throw new LocalModelException(
                "FPT_API_KEY_REQUIRED",
                "Hãy nhập và lưu API key FPT.AI trước khi tạo giọng online.");
        }

        await EnsureVoiceDependenciesAsync(manifest, cancellationToken);
        _ = await _quotaJobs.StartAsync(
            manifest,
            usesFpt ? "SYNTHESIZE_VOICE_CLOUD" : "SYNTHESIZE_VOICE_LOCAL",
            "voice.generate",
            ["SYNTHESIZE_VOICE", "SYNC_VOICE"],
            EstimateMinutes(manifest),
            new VoiceGenerationJobExecutor(
                new VoiceSynthesisJobExecutor(
                    _paths,
                    _projects,
                    manifest,
                    CreateVoiceSynthesizer()),
                new VoiceTimelineJobExecutor(_paths, _projects, manifest)),
            cancellationToken,
            new Dictionary<string, string>
            {
                ["provider"] = usesFpt ? ProtectedVoiceCredentialStore.FptProvider : "local",
                ["speed"] = Math.Clamp(manifest.Settings.VoiceSpeed, -3, 3).ToString(),
            });
        return Map(manifest);
    }

    public async Task<byte[]> PreviewFptVoiceAsync(
        string voiceId,
        int speed,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        _ = RequireProject();
        var voice = LocalVoiceCatalog.Find(voiceId)
            ?? throw new LocalModelException("VOICE_ID_INVALID", "Không tìm thấy giọng đọc đã chọn.");
        if (voice.Engine != LocalVoiceEngines.Fpt)
        {
            throw new LocalModelException("VOICE_PREVIEW_UNSUPPORTED", "Bản nghe thử online chỉ áp dụng cho giọng FPT.AI.");
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _voiceCredentials.SaveFptKey(apiKey);
        }

        var savedKey = _voiceCredentials.GetFptKey()
            ?? throw new LocalModelException("FPT_API_KEY_REQUIRED", "Hãy nhập API key FPT.AI để nghe thử.");
        var previewRoot = Path.Combine(_paths.RootDirectory, "Preview");
        Directory.CreateDirectory(previewRoot);
        var outputPath = Path.Combine(previewRoot, $"fpt-{Guid.NewGuid():N}.wav");
        try
        {
            var synthesizer = new FptVoiceSynthesizer(_voiceHttpClient, savedKey);
            await synthesizer.SynthesizeAsync(
                [new VoiceSynthesisRequest(
                    Guid.NewGuid(),
                    "Xin chào, đây là bản nghe thử giọng đọc FPT AI.",
                    outputPath,
                    voice.VoiceId,
                    Math.Clamp(speed, -3, 3))],
                cancellationToken);
            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            if (bytes.Length > 5 * 1024 * 1024)
            {
                throw new LocalModelException("FPT_PREVIEW_TOO_LARGE", "Bản nghe thử FPT.AI vượt giới hạn an toàn.");
            }

            return bytes;
        }
        catch (VoiceSynthesisException exception)
        {
            throw new LocalModelException(exception.Code, exception.Message, exception);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
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
        CancellationToken cancellationToken,
        string? translationRunMode = null)
    {
        var manifest = RequireProject();
        var job = manifest.Jobs.Single(item => item.JobId == jobId);
        if (job.JobType is "TRANSLATE_LOCAL" or "TRANSLATE_CLOUD"
            && !string.IsNullOrWhiteSpace(translationRunMode))
        {
            var normalizedRunMode = TranslationRunModes.Normalize(translationRunMode);
            job.Parameters[TranslationRunModes.ParameterName] = normalizedRunMode;
            if (normalizedRunMode == TranslationRunModes.Restart)
            {
                job.Parameters.Remove(TranslationRunModes.RestartPreparedParameterName);
                job.TranslationMetrics = new TranslationJobMetrics();
                job.ProgressPercent = 0;
                job.CurrentStep = null;
                foreach (var step in job.Steps)
                {
                    step.ProgressPercent = 0;
                }
            }
        }

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
        if (_runtimeProvisioner.IsReady)
        {
            return;
        }

        var progress = new Progress<LocalRuntimeProgress>(value =>
            RuntimeProgressChanged?.Invoke(this, value));
        await _runtimeProvisioner.EnsureReadyAsync(progress, cancellationToken);
    }

    private async Task EnsureVoiceDependenciesAsync(
        ProjectManifest manifest,
        CancellationToken cancellationToken)
    {
        var voices = manifest.SubtitleTracks
            .SelectMany(track => track.Cues)
            .Where(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText))
            .Select(cue => LocalVoiceCatalog.Resolve(manifest, cue))
            .DistinctBy(voice => voice.Engine)
            .ToArray();
        if (voices.Any(voice => voice.Engine == LocalVoiceEngines.Piper))
        {
            await EnsureLanguageRuntimeAsync(cancellationToken);
            var progress = new Progress<LocalModelDownloadProgress>(value =>
                ModelDownloadProgressChanged?.Invoke(this, value));
            await _models.DownloadAsync(
                PiperLocalVoiceSynthesizer.ModelId,
                progress,
                cancellationToken);
        }

        if (voices.Any(voice => voice.Engine == LocalVoiceEngines.VieNeu))
        {
            var runtimeProgress = new Progress<LocalRuntimeProgress>(value =>
                RuntimeProgressChanged?.Invoke(this, value));
            await _vieNeuRuntimeProvisioner.EnsureReadyAsync(runtimeProgress, cancellationToken);
            RuntimeProgressChanged?.Invoke(this, new LocalRuntimeProgress(
                "VOICE_MODEL",
                0,
                "Đang chuẩn bị bộ giọng VieNeu."));
            await new VieNeuLocalVoiceSynthesizer(_paths).EnsureReadyAsync(cancellationToken);
            RuntimeProgressChanged?.Invoke(this, new LocalRuntimeProgress(
                "VOICE_MODEL",
                100,
                "Bộ giọng VieNeu đã sẵn sàng."));
        }
    }

    private IVoiceSynthesizer CreateVoiceSynthesizer() =>
        new CompositeLocalVoiceSynthesizer(
            new PiperLocalVoiceSynthesizer(_paths, _models),
            new VieNeuLocalVoiceSynthesizer(_paths),
            _voiceCredentials.GetFptKey() is { } apiKey
                ? new FptVoiceSynthesizer(_voiceHttpClient, apiKey)
                : null);

    private async Task EnsureTranslationDependenciesAsync(
        string providerId,
        string sourceLanguage,
        bool fallbackToLocal,
        CancellationToken cancellationToken)
    {
        if (providerId != TranslationProviders.Local && !fallbackToLocal)
        {
            return;
        }

        await EnsureLanguageRuntimeAsync(cancellationToken);
        var modelProgress = new Progress<LocalModelDownloadProgress>(progress =>
            ModelDownloadProgressChanged?.Invoke(this, progress));
        await _models.DownloadAsync(
            LocalTranslatorFactory.GetModelId(sourceLanguage),
            modelProgress,
            cancellationToken);
    }

    private ITranslationProvider CreateTranslationProvider(
        string providerId,
        string modelId,
        string sourceLanguage,
        bool fallbackToLocal)
    {
        var normalizedProvider = TranslationProviders.Normalize(providerId);
        var localModelId = LocalTranslatorFactory.GetModelId(sourceLanguage);
        var local = new LocalTranslationProviderAdapter(
            LocalTranslatorFactory.Create(sourceLanguage, _paths, _models),
            localModelId,
            sourceLanguage == "zh"
                ? OpusMtChineseVietnameseTranslator.ModelVersion
                : ArgosLocalTranslator.ModelId);
        if (normalizedProvider == TranslationProviders.Local)
        {
            return local;
        }

        ITranslationProvider cloud = new ServerManagedTranslationProvider(
            _cloudAccess,
            _translationHttpClient,
            _projects,
            RequireProject(),
            normalizedProvider,
            modelId);
        return fallbackToLocal ? new FallbackTranslationProvider(cloud, local) : cloud;
    }

    private ITranslationProvider CreateTranslationProviderForJob(
        ProjectManifest manifest,
        LocalJob job)
    {
        var sourceLanguage = job.Parameters.GetValueOrDefault("sourceLanguage")
            ?? LocalLanguageCodes.ResolveProjectSource(manifest)
            ?? throw new LocalModelException(
                "TRANSLATION_SOURCE_REQUIRED",
                "Không xác định được ngôn ngữ nguồn của job dịch.");
        var providerId = TranslationProviders.Normalize(
            job.Parameters.GetValueOrDefault("provider")
            ?? (job.JobType == "TRANSLATE_LOCAL"
                ? TranslationProviders.Local
                : manifest.Settings.TranslationProvider));
        var modelId = TranslationModelDefaults.Resolve(
            providerId,
            job.Parameters.GetValueOrDefault("modelId") ?? manifest.Settings.TranslationModelId,
            manifest.Settings.TranslationQualityMode,
            sourceLanguage);
        var fallback = bool.TryParse(job.Parameters.GetValueOrDefault("fallbackToLocal"), out var enabled)
            && enabled;
        return CreateTranslationProvider(providerId, modelId, sourceLanguage, fallback);
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
            case "TRANSLATE_CLOUD":
            {
                var sourceLanguage = job.Parameters.GetValueOrDefault("sourceLanguage")
                    ?? LocalLanguageCodes.ResolveProjectSource(RequireProject())
                    ?? throw new LocalModelException(
                        "TRANSLATION_SOURCE_REQUIRED",
                        "Không xác định được ngôn ngữ nguồn của job dịch.");
                var providerId = job.Parameters.GetValueOrDefault("provider")
                    ?? (job.JobType == "TRANSLATE_LOCAL"
                        ? TranslationProviders.Local
                        : RequireProject().Settings.TranslationProvider);
                var fallback = bool.TryParse(job.Parameters.GetValueOrDefault("fallbackToLocal"), out var enabled)
                    && enabled;
                await EnsureTranslationDependenciesAsync(
                    TranslationProviders.Normalize(providerId),
                    sourceLanguage,
                    fallback,
                    cancellationToken);
                break;
            }
            case "SYNTHESIZE_VOICE_LOCAL":
            case "SYNTHESIZE_VOICE_CLOUD":
                if (job.JobType == "SYNTHESIZE_VOICE_CLOUD" && !_voiceCredentials.HasFptKey)
                {
                    throw new LocalModelException(
                        "FPT_API_KEY_REQUIRED",
                        "Hãy nhập lại API key FPT.AI trước khi tiếp tục job tạo giọng online.");
                }

                await EnsureVoiceDependenciesAsync(RequireProject(), cancellationToken);
                break;
        }
    }

    private static string GetFeatureCode(string jobType) => jobType switch
    {
        "TRANSCRIBE_LOCAL" => "subtitle.transcribe",
        "OCR_LOCAL" => "ocr.detect",
        "TRANSLATE_LOCAL" => "subtitle.translate",
        "TRANSLATE_CLOUD" => "subtitle.translate",
        "SYNTHESIZE_VOICE_LOCAL" => "voice.generate",
        "SYNTHESIZE_VOICE_CLOUD" => "voice.generate",
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
            "TRANSLATE_LOCAL" or "TRANSLATE_CLOUD" => new TranslationJobExecutor(
                _paths,
                _projects,
                manifest,
                CreateTranslationProviderForJob(manifest, job)),
            "SYNTHESIZE_VOICE_LOCAL" or "SYNTHESIZE_VOICE_CLOUD" => job.Steps.Any(item => item.Code == "SYNC_VOICE")
                ? new VoiceGenerationJobExecutor(
                    new VoiceSynthesisJobExecutor(
                        _paths,
                        _projects,
                        manifest,
                        CreateVoiceSynthesizer()),
                    new VoiceTimelineJobExecutor(_paths, _projects, manifest))
                : new VoiceSynthesisJobExecutor(
                    _paths,
                    _projects,
                    manifest,
                    CreateVoiceSynthesizer()),
            "EXPORT_VIDEO_LOCAL" => job.Steps.Any(item => item.Code == "SYNC_VOICE")
                ? new FullExportJobExecutor(
                    new VoiceTimelineJobExecutor(_paths, _projects, manifest),
                    new VideoExportJobExecutor(_paths, _projects, manifest))
                : new VideoExportJobExecutor(_paths, _projects, manifest),
            _ => throw new InvalidOperationException("Loại job chưa có bộ thực thi cục bộ."),
        };
    }

    private static string NormalizeTranslationText(string? value, int maximumLength, string label)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maximumLength
            || normalized.Any(character => char.IsControl(character)
                && character is not ('\r' or '\n' or '\t')))
        {
            throw new InvalidOperationException($"{label} vượt quá giới hạn hoặc chứa ký tự không hợp lệ.");
        }

        return normalized;
    }

    private static List<TranslationGlossaryEntry> ParseGlossary(
        string? glossaryText,
        IReadOnlyList<TranslationGlossaryEntry> existing)
    {
        var text = NormalizeTranslationText(glossaryText, 20_000, "Glossary");
        var result = new List<TranslationGlossaryEntry>();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var noteSeparator = rawLine.IndexOf('|');
            var mapping = noteSeparator >= 0 ? rawLine[..noteSeparator] : rawLine;
            var note = noteSeparator >= 0 ? rawLine[(noteSeparator + 1)..].Trim() : null;
            var separator = mapping.IndexOf('=');
            if (separator <= 0 || separator >= mapping.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Glossary không hợp lệ: '{rawLine}'. Mỗi dòng phải có dạng từ gốc = tiếng Việt | ghi chú.");
            }

            var source = mapping[..separator].Trim();
            var target = mapping[(separator + 1)..].Trim();
            if (source.Length is < 1 or > 200
                || target.Length is < 1 or > 200
                || note?.Length > 300
                || !sources.Add(source))
            {
                throw new InvalidOperationException("Glossary có mục trùng hoặc vượt quá giới hạn cho phép.");
            }

            var previous = existing.FirstOrDefault(entry =>
                string.Equals(entry.SourceText, source, StringComparison.OrdinalIgnoreCase));
            result.Add(new TranslationGlossaryEntry
            {
                EntryId = previous?.EntryId ?? Guid.NewGuid(),
                SourceText = source,
                TargetText = target,
                Note = string.IsNullOrWhiteSpace(note) ? null : note,
            });
        }

        if (result.Count > 200)
        {
            throw new InvalidOperationException("Glossary hỗ trợ tối đa 200 mục trong một dự án.");
        }

        return result;
    }

    private DesktopProjectState Map(ProjectManifest manifest)
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
                var requiresReview = string.Equals(
                        cue.TranslationQualityStatus,
                        "REVIEW",
                        StringComparison.OrdinalIgnoreCase)
                    || cue.TranslationWarnings is { Count: > 0 };
                var status = invalidTranslation
                    ? "invalid-translation"
                    : string.IsNullOrWhiteSpace(cue.TranslatedText) || overlap || requiresReview
                    ? "review"
                    : hasVoice ? "translated" : "missing-audio";
                subtitleCues.Add(new DesktopSubtitleCue(
                    cue.CueId,
                    index + 1,
                    cue.StartMilliseconds / 1000d,
                    cue.EndMilliseconds / 1000d,
                    cue.OriginalText,
                    cue.TranslatedText,
                    cue.Speaker,
                    cue.VoiceId,
                    LocalVoiceCatalog.Resolve(manifest, cue).VoiceId,
                    status,
                    overlap,
                    hasVoice,
                    cue.TranslationConfidence,
                    cue.TranslationWarnings ?? []));
            }
        }

        var sourceLanguage = LocalLanguageCodes.NormalizeSource(manifest.SourceLanguageCode);
        var providerId = TranslationProviders.Normalize(manifest.Settings.TranslationProvider);
        var translationModelId = sourceLanguage is null && providerId == TranslationProviders.Local
            ? "auto"
            : TranslationModelDefaults.Resolve(
                providerId,
                manifest.Settings.TranslationModelId,
                manifest.Settings.TranslationQualityMode,
                sourceLanguage ?? "en");
        var translationContext = manifest.TranslationContext ?? new ProjectTranslationContext();
        var glossaryText = string.Join(
            Environment.NewLine,
            manifest.TranslationGlossary.Select(entry =>
                string.IsNullOrWhiteSpace(entry.Note)
                    ? $"{entry.SourceText} = {entry.TargetText}"
                    : $"{entry.SourceText} = {entry.TargetText} | {entry.Note}"));
        var subtitleStyle = SubtitleStyleRules.Normalize(manifest.Settings.SubtitleStyle);
        manifest.Settings.SpeakerVoiceIds ??= new(StringComparer.OrdinalIgnoreCase);
        var piperInstalled = _models.GetStatuses()
            .Any(status => status.Id == PiperLocalVoiceSynthesizer.ModelId && status.Ready);
        var vieNeuInstalled = _vieNeuRuntimeProvisioner.IsReady
            && new VieNeuLocalVoiceSynthesizer(_paths).IsReady;
        var voiceSettings = new DesktopVoiceSettings(
            LocalVoiceCatalog.Resolve(manifest.Settings.VoiceId).VoiceId,
            new Dictionary<string, string>(
                manifest.Settings.SpeakerVoiceIds,
                StringComparer.OrdinalIgnoreCase),
            LocalVoiceCatalog.All.Select(voice => new DesktopVoiceInfo(
                voice.VoiceId,
                voice.Engine,
                voice.DisplayName,
                voice.Gender,
                voice.Region,
                voice.Style,
                voice.ModelVersion,
                voice.License,
                voice.Engine == LocalVoiceEngines.Piper
                    ? piperInstalled
                    : voice.Engine == LocalVoiceEngines.VieNeu
                        ? vieNeuInstalled
                        : _voiceCredentials.HasFptKey,
                voice.IsCloud,
                voice.RequiresInstall))
                .ToArray(),
            Math.Clamp(manifest.Settings.VoiceSpeed, -3, 3),
            _voiceCredentials.HasFptKey,
            manifest.SubtitleTracks
                .SelectMany(track => track.Cues)
                .Where(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText))
                .Sum(cue => cue.TranslatedText.Trim().Length));
        var subtitleRemovalRegions = GetEffectiveSubtitleRemovalRegions(manifest.Settings);
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
                new DesktopTranslationSettings(
                    providerId,
                    translationModelId,
                    TranslationQualityModes.Normalize(manifest.Settings.TranslationQualityMode),
                    manifest.Settings.TranslationReviewEnabled,
                    manifest.Settings.TranslationFallbackToLocal,
                    TranslationProviders.IsCloud(providerId),
                    translationContext.Summary,
                    translationContext.CharacterInstructions,
                    translationContext.StyleInstructions,
                    glossaryText,
                    manifest.TranslationMemory.Count),
                voiceSettings,
                manifest.Settings.OriginalAudioEnabled,
                manifest.Settings.OriginalAudioVolumePercent,
                manifest.Settings.VietnameseVoiceEnabled,
                manifest.Settings.VietnameseVoiceVolumePercent,
                manifest.Settings.VietnameseSubtitlesEnabled,
                manifest.Settings.FlipHorizontal,
                manifest.Settings.FlipVertical,
                manifest.Settings.RemoveOriginalSubtitles,
                manifest.Settings.OriginalSubtitleRemovalMode,
                manifest.Settings.OriginalSubtitleRegionX,
                manifest.Settings.OriginalSubtitleRegionY,
                manifest.Settings.OriginalSubtitleRegionWidth,
                manifest.Settings.OriginalSubtitleRegionHeight,
                subtitleRemovalRegions.Select(region => new DesktopSubtitleRemovalRegion(
                    region.Id,
                    region.X,
                    region.Y,
                    region.Width,
                    region.Height)).ToArray(),
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
            MapAiStorage(),
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
        _vieNeuRuntimeProvisioner.Dispose();
        _aiStorage.Dispose();
        _translationHttpClient.Dispose();
        _voiceHttpClient.Dispose();
    }

    private DesktopAiStorageInfo MapAiStorage()
    {
        var status = _aiStorage.GetStatus();
        return new DesktopAiStorageInfo(
            status.RootPath,
            status.FreeBytes,
            status.UsesLegacyLocation,
            status.RecommendedPath,
            status.PendingMigrationPath);
    }
}

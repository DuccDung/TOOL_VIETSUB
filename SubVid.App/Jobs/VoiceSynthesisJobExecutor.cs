using System.Security.Cryptography;
using SubVid.App.Core;
using SubVid.App.LocalAi;

namespace SubVid.App.Jobs;

public sealed class VoiceSynthesisJobExecutor : ILocalJobExecutor
{
    public const string ForcePhraseRegenerationParameter = "voice.forcePhraseRegeneration";

    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly IVoiceSynthesizer _synthesizer;

    public VoiceSynthesisJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        IVoiceSynthesizer synthesizer)
    {
        _paths = paths;
        _workspace = workspace;
        _project = project;
        _synthesizer = synthesizer;
    }

    public async Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var track = _project.SubtitleTracks.LastOrDefault(item => item.Cues.Count > 0)
            ?? throw new LocalJobException("SUBTITLE_TRACK_MISSING", "Chưa có phụ đề để tạo giọng.", retryable: false);
        var invalidTranslations = track.Cues
            .Where(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText)
                && (string.Equals(cue.TranslationQualityStatus, "INVALID", StringComparison.OrdinalIgnoreCase)
                    || TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText)))
            .ToArray();
        if (invalidTranslations.Length > 0)
        {
            throw new LocalJobException(
                "TRANSLATION_QUALITY_INVALID",
                $"Có {invalidTranslations.Length} bản dịch bị lặp hoặc dài bất thường. Hãy dịch lại lỗi trước khi tạo giọng.",
                retryable: false);
        }

        var cues = track.Cues
            .Where(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText))
            .OrderBy(cue => cue.StartMilliseconds)
            .ThenBy(cue => cue.EndMilliseconds)
            .ToArray();
        if (cues.Length == 0)
        {
            throw new LocalJobException(
                "TRANSLATION_MISSING",
                "Hãy dịch phụ đề sang tiếng Việt trước khi tạo giọng.",
                retryable: false);
        }

        var phrases = VoicePhrasePlanner.Plan(
            _project,
            track.Cues,
            _project.Settings.VoicePhraseGapMilliseconds,
            _project.Settings.VoicePhraseMaximumDurationSeconds);
        var forcePhraseRegeneration = bool.TryParse(
            job.Parameters.GetValueOrDefault(ForcePhraseRegenerationParameter),
            out var forceRegeneration)
            && forceRegeneration;
        job.VoiceMetrics = new VoiceSynthesisJobMetrics
        {
            TotalCharacters = cues.Sum(cue => cue.TranslatedText.Trim().Length),
            TotalCues = cues.Length,
            RetryRequests = Math.Max(0, job.AttemptCount - 1),
        };

        var pending = new List<VoiceSynthesisUnit>();
        var completed = 0;
        var cacheMetadataChanged = false;
        foreach (var phrase in phrases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phraseUnit = CreatePhraseUnit(phrase);
            var shouldSynthesizePhrase = _project.Settings.VoicePhraseSynthesisEnabled
                && phrase.Cues.Count > 1;
            var cachedPhrase = shouldSynthesizePhrase
                ? _project.AudioTracks.LastOrDefault(item =>
                    item.Role == "VOICE_PHRASE"
                    && string.Equals(item.VoicePhraseId, phrase.PhraseId, StringComparison.Ordinal)
                    && item.ContentFingerprint == phraseUnit.Fingerprint)
                : null;
            if (shouldSynthesizePhrase)
            {
                if (!forcePhraseRegeneration
                    && cachedPhrase is not null
                    && await IsCachedMediaValidAsync(cachedPhrase, cancellationToken))
                {
                    var removedCueTracks = _project.AudioTracks.RemoveAll(item =>
                        item.Role == "VOICE_CUE"
                        && item.CueId is Guid cueId
                        && phrase.Cues.Any(cue => cue.CueId == cueId));
                    cacheMetadataChanged |= removedCueTracks > 0;
                    completed += phrase.Cues.Count;
                    continue;
                }

                if (!forcePhraseRegeneration && cachedPhrase is not null)
                {
                    _project.AudioTracks.Remove(cachedPhrase);
                    cacheMetadataChanged = true;
                }

                // Per-cue cache is intentionally ignored for a multi-cue phrase. It remains
                // available until the replacement phrase has been synthesized successfully.
                pending.Add(phraseUnit);
                continue;
            }

            var missingCueUnits = new List<VoiceSynthesisUnit>();
            foreach (var cue in phrase.Cues)
            {
                var cueUnit = CreateCueUnit(cue);
                var cachedCue = _project.AudioTracks.LastOrDefault(item =>
                    item.Role == "VOICE_CUE"
                    && item.CueId == cue.CueId
                    && item.ContentFingerprint == cueUnit.Fingerprint);
                if (!forcePhraseRegeneration
                    && cachedCue is not null
                    && await IsCachedMediaValidAsync(cachedCue, cancellationToken))
                {
                    completed++;
                    continue;
                }

                if (!forcePhraseRegeneration && cachedCue is not null)
                {
                    _project.AudioTracks.Remove(cachedCue);
                }

                missingCueUnits.Add(cueUnit);
            }

            if (missingCueUnits.Count == 0)
            {
                continue;
            }

            pending.AddRange(missingCueUnits);
        }

        job.VoiceMetrics.CacheHitCues = completed;
        job.VoiceMetrics.CompletedCues = completed;
        if (pending.Count == 0)
        {
            if (cacheMetadataChanged)
            {
                await _workspace.SaveAsync(_project, cancellationToken);
            }
            job.Steps.Single(item => item.Code == "SYNTHESIZE_VOICE").OutputRelativePath = "voice";
            await reportProgress(new JobProgressUpdate(
                "SYNTHESIZE_VOICE",
                100,
                100,
                $"Đã dùng lại cache cho {completed} đoạn giọng Việt."));
            return;
        }

        if (completed > 0)
        {
            var cachedPercent = completed * 100d / cues.Length;
            await reportProgress(new JobProgressUpdate(
                "SYNTHESIZE_VOICE",
                cachedPercent,
                cachedPercent,
                $"Đã dùng lại {completed} đoạn giọng từ cache."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var unitsByRequestId = pending.ToDictionary(unit => unit.RequestId);
        var requests = pending.Select(unit =>
        {
            var partialPath = _paths.GetProjectPath(
                _project.ProjectId,
                "temp",
                $"voice-{unit.RequestId:N}.partial.wav");
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            var checkpointPrefix = GetCheckpointPrefix(unit.RequestId);
            var providerCheckpoint = unit.Voice.Engine == LocalVoiceEngines.Fpt
                ? new VoiceProviderCheckpoint(
                    job.Parameters.GetValueOrDefault(checkpointPrefix + "requestId"),
                    job.Parameters.GetValueOrDefault(checkpointPrefix + "resultUrl"),
                    async (requestId, resultUrl, token) =>
                    {
                        var wasSubmitted = !string.IsNullOrWhiteSpace(resultUrl)
                            && !job.Parameters.ContainsKey(checkpointPrefix + "resultUrl");
                        SetOrRemove(job.Parameters, checkpointPrefix + "requestId", requestId);
                        SetOrRemove(job.Parameters, checkpointPrefix + "resultUrl", resultUrl);
                        if (wasSubmitted)
                        {
                            job.VoiceMetrics.ApiRequests++;
                            job.VoiceMetrics.SubmittedCharacters += unit.Text.Length;
                        }

                        await _workspace.SaveAsync(_project, token);
                    })
                : null;
            return new VoiceSynthesisRequest(
                unit.RequestId,
                unit.Text,
                partialPath,
                unit.Voice.VoiceId,
                unit.Voice.Engine == LocalVoiceEngines.Fpt
                    ? Math.Clamp(_project.Settings.VoiceSpeed, -3, 3)
                    : 0,
                providerCheckpoint,
                unit.PhraseId,
                unit.Cues.Select(cue => cue.CueId).ToArray());
        }).ToArray();

        async ValueTask PersistCompletedAsync(VoiceSynthesisRequest request)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unit = unitsByRequestId[request.CueId];
            var relativeOutput = unit.IsPhrase
                ? Path.Combine("voice", $"phrase-{unit.PhraseId}.wav")
                : Path.Combine("voice", $"cue-{unit.Cues[0].CueId:N}.wav");
            var outputPath = _paths.GetProjectPath(_project.ProjectId, relativeOutput);
            File.Move(request.OutputPath, outputPath, overwrite: true);
            var file = new FileInfo(outputPath);
            var wave = WaveFileMetadata.Read(outputPath);
            var sha = await CalculateHashAsync(outputPath, cancellationToken);
            if (unit.IsPhrase)
            {
                _project.AudioTracks.RemoveAll(item =>
                    (item.Role == "VOICE_PHRASE"
                        && string.Equals(item.VoicePhraseId, unit.PhraseId, StringComparison.Ordinal))
                    || (item.Role == "VOICE_CUE"
                        && item.CueId is Guid cueId
                        && unit.Cues.Any(cue => cue.CueId == cueId)));
            }
            else
            {
                _project.AudioTracks.RemoveAll(item =>
                    item.Role == "VOICE_CUE" && item.CueId == unit.Cues[0].CueId);
            }

            _project.AudioTracks.Add(new LocalMediaReference
            {
                CueId = unit.IsPhrase ? null : unit.Cues[0].CueId,
                CueIds = unit.Cues.Select(cue => cue.CueId).ToList(),
                VoicePhraseId = unit.PhraseId,
                Role = unit.IsPhrase ? "VOICE_PHRASE" : "VOICE_CUE",
                ImportMode = "GENERATED",
                WorkspaceRelativePath = relativeOutput,
                FileName = file.Name,
                SizeBytes = file.Length,
                Sha256 = sha,
                ContentFingerprint = unit.Fingerprint,
                SourceLastWriteAtUtc = file.LastWriteTimeUtc,
                Metadata = new MediaMetadata
                {
                    DurationSeconds = wave.DurationSeconds,
                    HasAudio = true,
                    AudioTrackCount = 1,
                    AudioCodec = "pcm_s16le",
                    AudioChannels = wave.Channels,
                    AudioSampleRate = wave.SampleRate,
                    Container = "wav",
                },
            });

            var checkpointPrefix = GetCheckpointPrefix(request.CueId);
            job.Parameters.Remove(checkpointPrefix + "requestId");
            job.Parameters.Remove(checkpointPrefix + "resultUrl");
            completed += unit.Cues.Count;
            job.VoiceMetrics.CompletedCues = Math.Min(cues.Length, completed);
            await _workspace.SaveAsync(_project, cancellationToken);
            var percent = Math.Min(100, completed * 100d / cues.Length);
            await reportProgress(new JobProgressUpdate(
                "SYNTHESIZE_VOICE",
                percent,
                percent,
                unit.IsPhrase
                    ? $"Đã tạo cụm giọng tự nhiên {completed}/{cues.Length} câu."
                    : $"Đã tạo và lưu {completed}/{cues.Length} đoạn giọng Việt."));
        }

        try
        {
            if (_synthesizer is IIncrementalVoiceSynthesizer incremental)
            {
                await incremental.SynthesizeIncrementallyAsync(requests, PersistCompletedAsync, cancellationToken);
            }
            else
            {
                await _synthesizer.SynthesizeAsync(requests, cancellationToken);
                foreach (var request in requests)
                {
                    await PersistCompletedAsync(request);
                }
            }
        }
        catch (VoiceSynthesisException exception)
        {
            throw new LocalJobException(exception.Code, exception.Message, exception.Retryable);
        }
        finally
        {
            foreach (var request in requests)
            {
                if (File.Exists(request.OutputPath))
                {
                    File.Delete(request.OutputPath);
                }
            }
        }

        job.Steps.Single(item => item.Code == "SYNTHESIZE_VOICE").OutputRelativePath = "voice";
        await _workspace.SaveAsync(_project, cancellationToken);
    }

    private VoiceSynthesisUnit CreateCueUnit(SubtitleCue cue)
    {
        var voice = LocalVoiceCatalog.Resolve(_project, cue);
        var text = cue.TranslatedText.Trim();
        return new VoiceSynthesisUnit(
            cue.CueId,
            null,
            [cue],
            text,
            voice,
            BuildFingerprint("VOICE-V3", voice, text),
            false);
    }

    private VoiceSynthesisUnit CreatePhraseUnit(VoicePhrasePlan phrase)
    {
        var voice = LocalVoiceCatalog.Resolve(_project, phrase.Cues[0]);
        return new VoiceSynthesisUnit(
            phrase.RequestId,
            phrase.PhraseId,
            phrase.Cues,
            phrase.SynthesisText,
            voice,
            BuildPhraseFingerprint(_project, phrase),
            true);
    }

    public static string BuildPhraseFingerprint(ProjectManifest project, VoicePhrasePlan phrase)
    {
        var voice = LocalVoiceCatalog.Resolve(project, phrase.Cues[0]);
        var identityText = string.Join(
            '\n',
            phrase.PhraseId,
            phrase.SynthesisText);
        return BuildFingerprint(project, "VOICE-PHRASE-V2", voice, identityText);
    }

    private string BuildFingerprint(string version, LocalVoiceDefinition voice, string text)
        => BuildFingerprint(_project, version, voice, text);

    private static string BuildFingerprint(
        ProjectManifest project,
        string version,
        LocalVoiceDefinition voice,
        string text)
    {
        var identity = string.Join(
            '\n',
            version,
            voice.Engine,
            voice.ModelId,
            voice.ModelVersion,
            voice.ProviderVoiceId,
            voice.Engine == LocalVoiceEngines.Fpt
                ? Math.Clamp(project.Settings.VoiceSpeed, -3, 3).ToString()
                : "0",
            text);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private async Task<bool> IsCachedMediaValidAsync(
        LocalMediaReference cached,
        CancellationToken cancellationToken)
    {
        if (cached.WorkspaceRelativePath is not { } relativePath)
        {
            return false;
        }

        var cachedPath = _paths.GetProjectPath(_project.ProjectId, relativePath);
        var cachedFile = new FileInfo(cachedPath);
        return cachedFile.Exists
            && cachedFile.Length == cached.SizeBytes
            && !string.IsNullOrWhiteSpace(cached.Sha256)
            && string.Equals(
                await CalculateHashAsync(cachedPath, cancellationToken),
                cached.Sha256,
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> CalculateHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string GetCheckpointPrefix(Guid requestId) => $"voice.fpt.{requestId:N}.";

    private static void SetOrRemove(
        IDictionary<string, string> parameters,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parameters.Remove(key);
        }
        else
        {
            parameters[key] = value;
        }
    }

    private sealed record VoiceSynthesisUnit(
        Guid RequestId,
        string? PhraseId,
        IReadOnlyList<SubtitleCue> Cues,
        string Text,
        LocalVoiceDefinition Voice,
        string Fingerprint,
        bool IsPhrase);
}

public sealed record WaveFileMetadata(int SampleRate, int Channels, double DurationSeconds)
{
    public static WaveFileMetadata Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new LocalJobException("VOICE_WAVE_INVALID", "Bộ tổng hợp giọng tạo file WAV không hợp lệ.");
        }

        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new LocalJobException("VOICE_WAVE_INVALID", "Bộ tổng hợp giọng tạo file WAV không hợp lệ.");
        }

        int sampleRate = 0;
        int channels = 0;
        int blockAlign = 0;
        long dataSize = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadUInt32();
            var next = Math.Min(stream.Length, stream.Position + chunkSize + (chunkSize % 2));
            if (chunkId == "fmt " && chunkSize >= 16)
            {
                _ = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
            }
            else if (chunkId == "data")
            {
                dataSize = chunkSize;
            }

            stream.Position = next;
        }

        if (sampleRate <= 0 || channels <= 0 || blockAlign <= 0 || dataSize <= 0)
        {
            throw new LocalJobException("VOICE_WAVE_INVALID", "Bộ tổng hợp giọng tạo file WAV không hợp lệ.");
        }

        return new WaveFileMetadata(sampleRate, channels, dataSize / (double)(sampleRate * blockAlign));
    }
}

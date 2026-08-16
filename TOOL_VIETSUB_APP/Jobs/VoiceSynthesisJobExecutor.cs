using System.Security.Cryptography;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.LocalAi;

namespace TOOL_VIETSUB_APP.Jobs;

public sealed class VoiceSynthesisJobExecutor : ILocalJobExecutor
{
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

        var cues = track.Cues.Where(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText)).ToArray();
        if (cues.Length == 0)
        {
            throw new LocalJobException(
                "TRANSLATION_MISSING",
                "Hãy dịch phụ đề sang tiếng Việt trước khi tạo giọng.",
                retryable: false);
        }

        var fingerprints = cues.ToDictionary(cue => cue.CueId, BuildFingerprint);
        job.VoiceMetrics = new VoiceSynthesisJobMetrics
        {
            TotalCharacters = cues.Sum(cue => cue.TranslatedText.Trim().Length),
            TotalCues = cues.Length,
            RetryRequests = Math.Max(0, job.AttemptCount - 1),
        };
        var pending = new List<SubtitleCue>(cues.Length);
        foreach (var cue in cues)
        {
            var cached = _project.AudioTracks.LastOrDefault(item =>
                item.Role == "VOICE_CUE"
                && item.CueId == cue.CueId
                && item.ContentFingerprint == fingerprints[cue.CueId]);
            if (cached?.WorkspaceRelativePath is not { } relativePath)
            {
                pending.Add(cue);
                continue;
            }

            var cachedPath = _paths.GetProjectPath(_project.ProjectId, relativePath);
            var cachedFile = new FileInfo(cachedPath);
            if (!cachedFile.Exists || cachedFile.Length != cached.SizeBytes
                || string.IsNullOrWhiteSpace(cached.Sha256)
                || !string.Equals(
                    await CalculateHashAsync(cachedPath, cancellationToken),
                    cached.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                pending.Add(cue);
            }
        }

        var completed = cues.Length - pending.Count;
        job.VoiceMetrics.CacheHitCues = completed;
        job.VoiceMetrics.CompletedCues = completed;
        if (pending.Count == 0)
        {
            job.Steps.Single(item => item.Code == "SYNTHESIZE_VOICE").OutputRelativePath = "voice";
            await reportProgress(new JobProgressUpdate(
                "SYNTHESIZE_VOICE",
                100,
                100,
                $"ÄÃ£ dÃ¹ng láº¡i cache cho {completed} Ä‘oáº¡n giá»ng Viá»‡t."));
            return;
        }

        if (completed > 0)
        {
            var cachedPercent = completed * 100d / cues.Length;
            await reportProgress(new JobProgressUpdate(
                "SYNTHESIZE_VOICE",
                cachedPercent,
                cachedPercent,
                $"ÄÃ£ dÃ¹ng láº¡i {completed} Ä‘oáº¡n giá»ng tá»« cache."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var requests = pending.Select(cue =>
        {
            var partialPath = _paths.GetProjectPath(
                _project.ProjectId,
                "temp",
                $"voice-{cue.CueId:N}.partial.wav");
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            var voice = LocalVoiceCatalog.Resolve(_project, cue);
            var checkpointPrefix = GetCheckpointPrefix(cue.CueId);
            var providerCheckpoint = voice.Engine == LocalVoiceEngines.Fpt
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
                            job.VoiceMetrics.SubmittedCharacters += cue.TranslatedText.Trim().Length;
                        }

                        await _workspace.SaveAsync(_project, token);
                    })
                : null;
            return new VoiceSynthesisRequest(
                cue.CueId,
                cue.TranslatedText,
                partialPath,
                voice.VoiceId,
                voice.Engine == LocalVoiceEngines.Fpt ? Math.Clamp(_project.Settings.VoiceSpeed, -3, 3) : 0,
                providerCheckpoint);
        }).ToArray();

        async ValueTask PersistCompletedAsync(VoiceSynthesisRequest request)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeOutput = Path.Combine("voice", $"cue-{request.CueId:N}.wav");
            var outputPath = _paths.GetProjectPath(_project.ProjectId, relativeOutput);
            File.Move(request.OutputPath, outputPath, overwrite: true);
            var file = new FileInfo(outputPath);
            var wave = WaveFileMetadata.Read(outputPath);
            var sha = await CalculateHashAsync(outputPath, cancellationToken);
            _project.AudioTracks.RemoveAll(item =>
                item.Role == "VOICE_CUE" && item.CueId == request.CueId);
            _project.AudioTracks.Add(new LocalMediaReference
            {
                CueId = request.CueId,
                Role = "VOICE_CUE",
                ImportMode = "GENERATED",
                WorkspaceRelativePath = relativeOutput,
                FileName = file.Name,
                SizeBytes = file.Length,
                Sha256 = sha,
                ContentFingerprint = fingerprints[request.CueId],
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
            completed++;
            job.VoiceMetrics.CompletedCues = completed;
            await _workspace.SaveAsync(_project, cancellationToken);
            var percent = completed * 100d / cues.Length;
            await reportProgress(new JobProgressUpdate(
                "SYNTHESIZE_VOICE",
                percent,
                percent,
                $"Đã tạo và lưu {completed}/{cues.Length} đoạn giọng Việt."));
        }

        try
        {
            if (_synthesizer is IIncrementalVoiceSynthesizer incremental)
            {
                await incremental.SynthesizeIncrementallyAsync(requests, PersistCompletedAsync, cancellationToken);
            }
            else
            {
                // Local engine chỉ nạp model một lần cho toàn bộ phần còn thiếu của job.
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

    private string BuildFingerprint(SubtitleCue cue)
    {
        var voice = LocalVoiceCatalog.Resolve(_project, cue);
        var identity = string.Join(
            '\n',
            "VOICE-V3",
            voice.Engine,
            voice.ModelId,
            voice.ModelVersion,
            voice.ProviderVoiceId,
            voice.Engine == LocalVoiceEngines.Fpt
                ? Math.Clamp(_project.Settings.VoiceSpeed, -3, 3).ToString()
                : "0",
            cue.TranslatedText.Trim());
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
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

    private static string GetCheckpointPrefix(Guid cueId) => $"voice.fpt.{cueId:N}.";

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
}

public sealed record WaveFileMetadata(int SampleRate, int Channels, double DurationSeconds)
{
    public static WaveFileMetadata Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new LocalJobException("VOICE_WAVE_INVALID", "Piper tạo file WAV không hợp lệ.");
        }

        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new LocalJobException("VOICE_WAVE_INVALID", "Piper tạo file WAV không hợp lệ.");
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
            throw new LocalJobException("VOICE_WAVE_INVALID", "Piper tạo file WAV không hợp lệ.");
        }

        return new WaveFileMetadata(sampleRate, channels, dataSize / (double)(sampleRate * blockAlign));
    }
}

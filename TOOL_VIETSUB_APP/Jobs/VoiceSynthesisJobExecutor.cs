using System.Security.Cryptography;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.LocalAi;

namespace TOOL_VIETSUB_APP.Jobs;

public sealed class VoiceSynthesisJobExecutor : ILocalJobExecutor
{
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly ILocalVoiceSynthesizer _synthesizer;

    public VoiceSynthesisJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        ILocalVoiceSynthesizer synthesizer)
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

        const int batchSize = 8;
        var completed = cues.Length - pending.Count;
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

        for (var offset = 0; offset < pending.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = pending.Skip(offset).Take(batchSize).ToArray();
            var requests = batch.Select(cue =>
            {
                var partialPath = _paths.GetProjectPath(_project.ProjectId, "temp", $"voice-{cue.CueId:N}.partial.wav");
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }

                return new VoiceSynthesisRequest(cue.CueId, cue.TranslatedText, partialPath);
            }).ToArray();
            try
            {
                await _synthesizer.SynthesizeAsync(requests, cancellationToken);
                foreach (var request in requests)
                {
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
                }
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

            completed += batch.Length;
            await _workspace.SaveAsync(_project, cancellationToken);
            var percent = completed * 100d / cues.Length;
            await reportProgress(new JobProgressUpdate(
                "SYNTHESIZE_VOICE",
                percent,
                percent,
                $"Đã tạo và lưu {completed}/{cues.Length} đoạn giọng Việt."));
        }

        job.Steps.Single(item => item.Code == "SYNTHESIZE_VOICE").OutputRelativePath = "voice";
        await _workspace.SaveAsync(_project, cancellationToken);
    }

    private string BuildFingerprint(SubtitleCue cue)
    {
        var identity = string.Join(
            '\n',
            "PIPER",
            PiperLocalVoiceSynthesizer.ModelId,
            _project.Settings.VoiceId ?? "vi_VN-vais1000-medium",
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

using System.Text;
using SubVid.App.Core;
using SubVid.App.LocalAi;
using SubVid.App.Subtitles;

namespace SubVid.App.Jobs;

public sealed class TranscriptionJobExecutor : ILocalJobExecutor
{
    internal const double LongFormThresholdSeconds = 30 * 60;
    private const int LongFormCheckpointCueBatchSize = 25;
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly ILocalSpeechRecognizer _recognizer;
    private readonly Func<ILocalJobExecutor> _audioExecutorFactory;
    private readonly ILongFormAudioChunkExtractor? _longFormChunkExtractor;
    private readonly string? _languageCode;

    public TranscriptionJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        ILocalSpeechRecognizer recognizer,
        Func<ILocalJobExecutor>? audioExecutorFactory = null,
        string? languageCode = null,
        ILongFormAudioChunkExtractor? longFormChunkExtractor = null)
    {
        _paths = paths;
        _workspace = workspace;
        _project = project;
        _recognizer = recognizer;
        _audioExecutorFactory = audioExecutorFactory ?? (() => new AudioExtractionJobExecutor(paths, project));
        _languageCode = languageCode;
        _longFormChunkExtractor = longFormChunkExtractor;
    }

    public async Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var source = _project.SourceVideo
            ?? throw new LocalJobException("MEDIA_SOURCE_MISSING", "Dự án chưa có video nguồn.", retryable: false);
        if (!source.Metadata.HasAudio)
        {
            throw new LocalJobException(
                "MEDIA_AUDIO_MISSING",
                "Video không có audio để nhận dạng giọng nói.",
                retryable: false);
        }

        if (source.Metadata.DurationSeconds >= LongFormThresholdSeconds)
        {
            await ExecuteLongFormAsync(source, job, reportProgress, cancellationToken);
            return;
        }

        await ExecuteSingleAudioAsync(source, job, reportProgress, cancellationToken);
    }

    private async Task ExecuteSingleAudioAsync(
        LocalMediaReference source,
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var audio = FindSourceAudio();
        if (audio is null || audio.WorkspaceRelativePath is null
            || !File.Exists(_paths.GetProjectPath(_project.ProjectId, audio.WorkspaceRelativePath)))
        {
            var audioExecutor = _audioExecutorFactory();
            await audioExecutor.ExecuteAsync(
                job,
                update => reportProgress(update with { JobProgressPercent = update.StepProgressPercent * 0.2 }),
                cancellationToken);
            audio = FindSourceAudio();
        }
        else
        {
            await reportProgress(new JobProgressUpdate(
                "EXTRACT_AUDIO",
                100,
                20,
                "Audio 16 kHz đã sẵn sàng."));
        }

        if (audio?.WorkspaceRelativePath is null)
        {
            throw new LocalJobException("SPEECH_AUDIO_MISSING", "Không tìm thấy audio đã chuẩn hóa.");
        }

        var audioPath = _paths.GetProjectPath(_project.ProjectId, audio.WorkspaceRelativePath);
        var requestedLanguage = ResolveRequestedLanguage();
        var track = GetOrCreateWhisperTrack(requestedLanguage);
        if (job.AttemptCount <= 1)
        {
            track.Cues.RemoveAll(cue => !cue.OriginalLocked);
        }

        var checkpointMilliseconds = job.AttemptCount > 1
            ? track.Cues.Where(cue => !cue.OriginalLocked).Select(cue => cue.EndMilliseconds).DefaultIfEmpty(0).Max()
            : 0;
        var durationMilliseconds = Math.Max(1, (long)(source.Metadata.DurationSeconds * 1000));
        var recognized = 0;
        var unsaved = 0;
        await reportProgress(new JobProgressUpdate(
            "TRANSCRIBE",
            checkpointMilliseconds * 100d / durationMilliseconds,
            20 + checkpointMilliseconds * 80d / durationMilliseconds,
            checkpointMilliseconds > 0 ? "Đang tiếp tục từ checkpoint phụ đề." : "Whisper local đang nhận dạng giọng nói."));

        await foreach (var segment in _recognizer.RecognizeAsync(
            audioPath,
            requestedLanguage,
            cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.EndMilliseconds <= checkpointMilliseconds)
            {
                continue;
            }

            if (MergeRecognizedSegment(track, segment))
            {
                recognized++;
                unsaved++;
            }

            ApplyDetectedLanguage(track, requestedLanguage, segment.Language);
            if (unsaved < LongFormCheckpointCueBatchSize)
            {
                continue;
            }

            await SaveTranscriptBatchAsync(track, cancellationToken);
            unsaved = 0;
            var stepPercent = Math.Clamp(segment.EndMilliseconds * 100d / durationMilliseconds, 0, 99.5);
            await reportProgress(new JobProgressUpdate(
                "TRANSCRIBE",
                stepPercent,
                20 + stepPercent * 0.8,
                $"Đã lưu checkpoint {track.Cues.Count} phân đoạn."));
        }

        if (unsaved > 0)
        {
            await SaveTranscriptBatchAsync(track, cancellationToken);
        }

        await CompleteTranscriptAsync(track, job, recognized, reportProgress, cancellationToken);
    }

    private async Task ExecuteLongFormAsync(
        LocalMediaReference source,
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var requestedLanguage = ResolveRequestedLanguage();
        var track = GetOrCreateWhisperTrack(requestedLanguage);
        var checkpointStore = new TranscriptionChunkCheckpointStore(
            _paths,
            _project.ProjectId,
            job.JobId);
        if (job.AttemptCount <= 1)
        {
            track.Cues.RemoveAll(cue => !cue.OriginalLocked);
            checkpointStore.Reset();
        }

        var checkpoint = await checkpointStore.LoadAsync(cancellationToken);
        if (!string.Equals(checkpoint.SourceSha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            checkpoint = new TranscriptionChunkCheckpoint { SourceSha256 = source.Sha256 };
            if (job.AttemptCount > 1)
            {
                track.Cues.RemoveAll(cue => !cue.OriginalLocked);
            }
        }

        var durationMilliseconds = Math.Max(1, (long)Math.Ceiling(source.Metadata.DurationSeconds * 1000));
        var chunks = LongFormAudioChunkPlanner.Plan(durationMilliseconds);
        var extractor = _longFormChunkExtractor
            ?? new FfmpegLongFormAudioChunkExtractor(_paths, _project);
        var recognized = 0;
        await reportProgress(new JobProgressUpdate(
            "EXTRACT_AUDIO",
            100,
            20,
            $"Video dài được chia thành {chunks.Count} phần; audio sẽ được tạo theo nhu cầu."));

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkpoint.CompletedChunks.Contains(chunk.Index))
            {
                continue;
            }

            var chunkPath = await extractor.ExtractAsync(
                chunk,
                _ => ValueTask.CompletedTask,
                cancellationToken);
            var unsaved = 0;
            var highWatermark = checkpoint.HighWatermarks.GetValueOrDefault(
                chunk.Index,
                chunk.ExtractionStartMilliseconds);
            try
            {
                await foreach (var segment in _recognizer.RecognizeAsync(
                    chunkPath,
                    requestedLanguage,
                    cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var globalStart = Math.Clamp(
                        chunk.ExtractionStartMilliseconds + segment.StartMilliseconds,
                        0,
                        durationMilliseconds - 1);
                    var globalEnd = Math.Clamp(
                        chunk.ExtractionStartMilliseconds + segment.EndMilliseconds,
                        globalStart + 1,
                        durationMilliseconds);
                    if (globalEnd <= highWatermark || !chunk.Owns(globalStart, globalEnd))
                    {
                        continue;
                    }

                    if (MergeRecognizedSegment(track, segment with
                        {
                            StartMilliseconds = globalStart,
                            EndMilliseconds = globalEnd,
                        }))
                    {
                        recognized++;
                        unsaved++;
                    }

                    highWatermark = Math.Max(highWatermark, globalEnd);
                    ApplyDetectedLanguage(track, requestedLanguage, segment.Language);
                    if (unsaved < LongFormCheckpointCueBatchSize)
                    {
                        continue;
                    }

                    await SaveLongFormCheckpointAsync(
                        track,
                        checkpointStore,
                        checkpoint,
                        chunk,
                        highWatermark,
                        durationMilliseconds,
                        reportProgress,
                        cancellationToken);
                    unsaved = 0;
                }

                await SaveLongFormCheckpointAsync(
                    track,
                    checkpointStore,
                    checkpoint,
                    chunk,
                    highWatermark,
                    durationMilliseconds,
                    reportProgress,
                    cancellationToken,
                    completed: true);
            }
            finally
            {
                if (File.Exists(chunkPath))
                {
                    File.Delete(chunkPath);
                }
            }
        }

        await CompleteTranscriptAsync(track, job, recognized, reportProgress, cancellationToken);
    }

    private async Task SaveLongFormCheckpointAsync(
        SubtitleDocument track,
        TranscriptionChunkCheckpointStore checkpointStore,
        TranscriptionChunkCheckpoint checkpoint,
        LongFormAudioChunk chunk,
        long highWatermark,
        long durationMilliseconds,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        bool completed = false)
    {
        await SaveTranscriptBatchAsync(track, cancellationToken);
        checkpoint.HighWatermarks[chunk.Index] = highWatermark;
        if (completed)
        {
            checkpoint.CompletedChunks.Add(chunk.Index);
        }

        await checkpointStore.SaveAsync(checkpoint, cancellationToken);
        var completedMilliseconds = completed
            ? chunk.OwnershipEndMilliseconds
            : Math.Clamp(highWatermark, chunk.OwnershipStartMilliseconds, chunk.OwnershipEndMilliseconds);
        var stepPercent = Math.Clamp(completedMilliseconds * 100d / durationMilliseconds, 0, 99.5);
        await reportProgress(new JobProgressUpdate(
            "TRANSCRIBE",
            stepPercent,
            20 + stepPercent * 0.8,
            $"Đã lưu phần {chunk.Index + 1}; hiện có {track.Cues.Count} phân đoạn."));
    }

    private async Task CompleteTranscriptAsync(
        SubtitleDocument track,
        LocalJob job,
        int recognized,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        if (track.Cues.Count == 0)
        {
            throw new LocalJobException(
                "SPEECH_NOT_DETECTED",
                "Whisper không phát hiện lời nói trong audio.",
                retryable: false);
        }

        await WriteOutputAsync(track, job, cancellationToken);
        await reportProgress(new JobProgressUpdate(
            "TRANSCRIBE",
            100,
            100,
            $"Nhận dạng hoàn tất với {track.Cues.Count} phân đoạn ({recognized} phân đoạn mới)."));
    }

    private async Task SaveTranscriptBatchAsync(
        SubtitleDocument track,
        CancellationToken cancellationToken)
    {
        track.Cues = track.Cues
            .OrderBy(cue => cue.StartMilliseconds)
            .ThenBy(cue => cue.EndMilliseconds)
            .ToList();
        await _workspace.SaveAsync(_project, cancellationToken);
    }

    private async Task WriteOutputAsync(
        SubtitleDocument track,
        LocalJob job,
        CancellationToken cancellationToken)
    {
        var relativeOutput = Path.Combine("subtitles", $"whisper-{track.TrackId:N}.srt");
        var outputPath = _paths.GetProjectPath(_project.ProjectId, relativeOutput);
        var partialPath = outputPath + ".partial";
        try
        {
            await File.WriteAllTextAsync(
                partialPath,
                SrtService.Serialize(track.Cues),
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

        job.Steps.Single(item => item.Code == "TRANSCRIBE").OutputRelativePath = relativeOutput;
        await _workspace.SaveAsync(_project, cancellationToken);
    }

    private string? ResolveRequestedLanguage() =>
        LocalLanguageCodes.NormalizeSource(_languageCode)
        ?? LocalLanguageCodes.NormalizeSource(_project.SourceLanguageCode);

    private SubtitleDocument GetOrCreateWhisperTrack(string? requestedLanguage)
    {
        var track = _project.SubtitleTracks.LastOrDefault(item => item.Source == "WHISPER_LOCAL");
        if (track is null)
        {
            track = new SubtitleDocument
            {
                Source = "WHISPER_LOCAL",
                LanguageCode = requestedLanguage ?? "und",
            };
            _project.SubtitleTracks.Add(track);
        }

        if (requestedLanguage is not null)
        {
            _project.SourceLanguageCode = requestedLanguage;
            track.LanguageCode = requestedLanguage;
        }

        return track;
    }

    private void ApplyDetectedLanguage(
        SubtitleDocument track,
        string? requestedLanguage,
        string detectedLanguage)
    {
        if (requestedLanguage is null
            && LocalLanguageCodes.NormalizeSource(detectedLanguage) is { } normalized)
        {
            _project.SourceLanguageCode = normalized;
            track.LanguageCode = normalized;
        }
    }

    private static bool MergeRecognizedSegment(
        SubtitleDocument track,
        SpeechRecognitionSegment segment)
    {
        if (track.Cues.Any(cue => cue.OriginalLocked && Overlaps(cue, segment)))
        {
            return false;
        }

        track.Cues.RemoveAll(cue =>
            !cue.OriginalLocked
            && cue.StartMilliseconds < segment.EndMilliseconds
            && cue.EndMilliseconds > segment.StartMilliseconds);
        track.Cues.Add(new SubtitleCue
        {
            StartMilliseconds = segment.StartMilliseconds,
            EndMilliseconds = segment.EndMilliseconds,
            OriginalText = segment.Text,
        });
        return true;
    }

    private LocalMediaReference? FindSourceAudio() =>
        _project.AudioTracks.LastOrDefault(item => item.Role == "SOURCE_AUDIO");

    private static bool Overlaps(SubtitleCue cue, SpeechRecognitionSegment segment) =>
        cue.StartMilliseconds < segment.EndMilliseconds
        && cue.EndMilliseconds > segment.StartMilliseconds;
}

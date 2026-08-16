using System.Text;
using SubVid.App.Core;
using SubVid.App.LocalAi;
using SubVid.App.Subtitles;

namespace SubVid.App.Jobs;

public sealed class TranscriptionJobExecutor : ILocalJobExecutor
{
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly ILocalSpeechRecognizer _recognizer;
    private readonly Func<ILocalJobExecutor> _audioExecutorFactory;
    private readonly string? _languageCode;

    public TranscriptionJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        ILocalSpeechRecognizer recognizer,
        Func<ILocalJobExecutor>? audioExecutorFactory = null,
        string? languageCode = null)
    {
        _paths = paths;
        _workspace = workspace;
        _project = project;
        _recognizer = recognizer;
        _audioExecutorFactory = audioExecutorFactory ?? (() => new AudioExtractionJobExecutor(paths, project));
        _languageCode = languageCode;
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
        var requestedLanguage = LocalLanguageCodes.NormalizeSource(_languageCode)
            ?? LocalLanguageCodes.NormalizeSource(_project.SourceLanguageCode);
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

        if (job.AttemptCount <= 1)
        {
            track.Cues.RemoveAll(cue => !cue.OriginalLocked);
        }

        var checkpointMilliseconds = job.AttemptCount > 1
            ? track.Cues.Where(cue => !cue.OriginalLocked).Select(cue => cue.EndMilliseconds).DefaultIfEmpty(0).Max()
            : 0;
        var durationMilliseconds = Math.Max(1, (long)(source.Metadata.DurationSeconds * 1000));
        var recognized = 0;
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

            if (!track.Cues.Any(cue => cue.OriginalLocked && Overlaps(cue, segment)))
            {
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
                track.Cues = track.Cues
                    .OrderBy(cue => cue.StartMilliseconds)
                    .ThenBy(cue => cue.EndMilliseconds)
                    .ToList();
                recognized++;
            }

            if (requestedLanguage is null
                && LocalLanguageCodes.NormalizeSource(segment.Language) is { } detectedLanguage)
            {
                _project.SourceLanguageCode = detectedLanguage;
                track.LanguageCode = detectedLanguage;
            }

            await _workspace.SaveAsync(_project, cancellationToken);
            var stepPercent = Math.Clamp(segment.EndMilliseconds * 100d / durationMilliseconds, 0, 99.5);
            await reportProgress(new JobProgressUpdate(
                "TRANSCRIBE",
                stepPercent,
                20 + stepPercent * 0.8,
                $"Đã lưu checkpoint {track.Cues.Count} phân đoạn."));
        }

        if (track.Cues.Count == 0)
        {
            throw new LocalJobException(
                "SPEECH_NOT_DETECTED",
                "Whisper không phát hiện lời nói trong audio.",
                retryable: false);
        }

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
        await reportProgress(new JobProgressUpdate(
            "TRANSCRIBE",
            100,
            100,
            $"Nhận dạng hoàn tất với {track.Cues.Count} phân đoạn ({recognized} phân đoạn mới)."));
    }

    private LocalMediaReference? FindSourceAudio() =>
        _project.AudioTracks.LastOrDefault(item => item.Role == "SOURCE_AUDIO");

    private static bool Overlaps(SubtitleCue cue, SpeechRecognitionSegment segment) =>
        cue.StartMilliseconds < segment.EndMilliseconds
        && cue.EndMilliseconds > segment.StartMilliseconds;
}

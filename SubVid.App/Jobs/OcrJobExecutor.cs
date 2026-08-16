using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SubVid.App.Core;
using SubVid.App.LocalAi;
using SubVid.App.Media;
using SubVid.App.Subtitles;

namespace SubVid.App.Jobs;

public sealed class OcrJobExecutor : ILocalJobExecutor
{
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly string _ffmpegPath;
    private readonly FfmpegProgressRunner _runner;
    private readonly Func<ILocalOcrRecognizer> _recognizerFactory;
    private readonly string _languageCode;

    public OcrJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        string? ffmpegPath = null,
        FfmpegProgressRunner? runner = null,
        Func<ILocalOcrRecognizer>? recognizerFactory = null,
        string? languageCode = null)
    {
        _paths = paths;
        _workspace = workspace;
        _project = project;
        _ffmpegPath = ffmpegPath ?? MediaToolLocator.Locate(paths, "ffmpeg", "SUBVID_FFMPEG_PATH");
        _runner = runner ?? new FfmpegProgressRunner();
        _languageCode = LocalLanguageCodes.NormalizeSource(languageCode)
            ?? LocalLanguageCodes.NormalizeSource(project.Settings.OcrLanguageCode)
            ?? LocalLanguageCodes.ResolveProjectSource(project)
            ?? (recognizerFactory is not null
                ? "en"
                : throw new LocalModelException(
                    "OCR_LANGUAGE_REQUIRED",
                    "Hãy chọn tiếng Trung hoặc tiếng Anh trước khi chạy OCR."));
        _recognizerFactory = recognizerFactory ?? (() => new PaddleLocalOcrRecognizer(_languageCode));
    }

    public async Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var source = _project.SourceVideo
            ?? throw new LocalJobException("MEDIA_SOURCE_MISSING", "Dự án chưa có video nguồn.", retryable: false);
        var sourcePath = source.ImportMode == "COPY" && source.WorkspaceRelativePath is not null
            ? _paths.GetProjectPath(_project.ProjectId, source.WorkspaceRelativePath)
            : Path.GetFullPath(source.OriginalPath);
        var sourceFile = new FileInfo(sourcePath);
        if (!sourceFile.Exists || sourceFile.Length != source.SizeBytes)
        {
            throw new LocalJobException(
                "MEDIA_SOURCE_CHANGED",
                "Video nguồn đã bị di chuyển hoặc thay đổi kể từ khi nhập.",
                retryable: false);
        }

        if (string.IsNullOrWhiteSpace(source.Sha256)
            || !string.Equals(
                await CalculateHashAsync(sourcePath, cancellationToken),
                source.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalJobException(
                "MEDIA_SOURCE_CHANGED",
                "Nội dung video nguồn đã thay đổi kể từ khi nhập.",
                retryable: false);
        }

        var interval = Math.Clamp(_project.Settings.OcrSampleIntervalMilliseconds, 200, 5000);
        var cropTop = Math.Clamp(_project.Settings.OcrCropTopRatio, 0, 0.9);
        var temporaryDirectory = _paths.GetProjectPath(_project.ProjectId, "temp", $"ocr-{job.JobId:N}");
        Directory.CreateDirectory(temporaryDirectory);
        var framePattern = Path.Combine(temporaryDirectory, "frame-%08d.jpg");
        try
        {
            await reportProgress(new JobProgressUpdate("OCR_EXTRACT_FRAMES", 0, 0, "Đang trích vùng phụ đề từ video."));
            var fps = 1000d / interval;
            var cropHeight = 1 - cropTop;
            var filter = FormattableString.Invariant(
                $"fps={fps:0.######},crop=iw:ih*{cropHeight:0.######}:0:ih*{cropTop:0.######},scale='min(1280,iw)':-2");
            await _runner.RunAsync(
                _ffmpegPath,
                [
                    "-y",
                    "-v", "error",
                    "-i", sourcePath,
                    "-an",
                    "-vf", filter,
                    "-q:v", "3",
                    "-start_number", "0",
                    framePattern,
                ],
                source.Metadata.DurationSeconds,
                progress => reportProgress(new JobProgressUpdate(
                    "OCR_EXTRACT_FRAMES",
                    progress,
                    progress * 0.2,
                    progress >= 100 ? "Đã trích khung hình OCR." : null)),
                cancellationToken);

            var frames = Directory.EnumerateFiles(temporaryDirectory, "frame-*.jpg")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (frames.Length == 0)
            {
                throw new LocalJobException("OCR_FRAMES_EMPTY", "Không trích được khung hình để OCR.");
            }

            var track = _project.SubtitleTracks.LastOrDefault(item => item.Source == "PADDLE_OCR_LOCAL");
            if (track is null)
            {
                track = new SubtitleDocument { Source = "PADDLE_OCR_LOCAL", LanguageCode = _languageCode };
                _project.SubtitleTracks.Add(track);
            }

            track.LanguageCode = _languageCode;

            if (job.AttemptCount <= 1)
            {
                track.Cues.RemoveAll(cue => !cue.OriginalLocked);
            }

            var locked = track.Cues.Where(cue => cue.OriginalLocked).ToArray();
            var checkpoint = job.AttemptCount > 1
                ? track.Cues.Where(cue => !cue.OriginalLocked).Select(cue => cue.EndMilliseconds).DefaultIfEmpty(0).Max()
                : 0;
            var accumulator = new OcrCueAccumulator(interval);
            using var recognizer = _recognizerFactory();
            for (var index = 0; index < frames.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timestamp = (long)index * interval;
                if (timestamp < checkpoint)
                {
                    continue;
                }

                var lines = await recognizer.RecognizeAsync(frames[index], cancellationToken);
                var text = string.Join('\n', lines.Select(line => line.Text));
                var confidence = lines.Count == 0 ? 0 : lines.Average(line => line.Confidence);
                accumulator.Add(timestamp, text, confidence);
                if ((index + 1) % 10 == 0)
                {
                    track.Cues = MergeCues(locked, track.Cues, accumulator.Completed, checkpoint);
                    await _workspace.SaveAsync(_project, cancellationToken);
                }

                var stepPercent = (index + 1) * 100d / frames.Length;
                await reportProgress(new JobProgressUpdate(
                    "OCR_RECOGNIZE",
                    stepPercent,
                    20 + stepPercent * 0.8,
                    (index + 1) % 10 == 0 ? $"Đã OCR {index + 1}/{frames.Length} khung hình." : null));
            }

            accumulator.Complete();
            track.Cues = MergeCues(locked, track.Cues, accumulator.Completed, checkpoint);
            if (track.Cues.Count == 0)
            {
                throw new LocalJobException(
                    "OCR_TEXT_NOT_DETECTED",
                    "Không phát hiện phụ đề cứng trong vùng OCR.",
                    retryable: false);
            }

            var relativeOutput = Path.Combine("subtitles", $"ocr-{track.TrackId:N}.srt");
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

            job.Steps.Single(item => item.Code == "OCR_RECOGNIZE").OutputRelativePath = relativeOutput;
            await _workspace.SaveAsync(_project, cancellationToken);
            await reportProgress(new JobProgressUpdate(
                "OCR_RECOGNIZE",
                100,
                100,
                $"OCR hoàn tất với {track.Cues.Count} phân đoạn."));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static List<SubtitleCue> MergeCues(
        IReadOnlyCollection<SubtitleCue> locked,
        IReadOnlyCollection<SubtitleCue> existing,
        IReadOnlyCollection<SubtitleCue> recognized,
        long checkpoint)
    {
        var savedBeforeCheckpoint = existing.Where(cue =>
            !cue.OriginalLocked && cue.EndMilliseconds <= checkpoint);
        return locked
            .Concat(savedBeforeCheckpoint)
            .Concat(recognized.Where(candidate => !locked.Any(item =>
                item.StartMilliseconds < candidate.EndMilliseconds
                && item.EndMilliseconds > candidate.StartMilliseconds)))
            .OrderBy(cue => cue.StartMilliseconds)
            .ThenBy(cue => cue.EndMilliseconds)
            .ToList();
    }

    private static async Task<string> CalculateHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }
}

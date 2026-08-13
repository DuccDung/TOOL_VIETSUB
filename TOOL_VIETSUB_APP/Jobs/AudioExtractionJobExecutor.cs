using System.Security.Cryptography;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.Media;

namespace TOOL_VIETSUB_APP.Jobs;

public sealed class AudioExtractionJobExecutor : ILocalJobExecutor
{
    private readonly AppPaths _paths;
    private readonly ProjectManifest _project;
    private readonly string _ffmpegPath;
    private readonly FfmpegProgressRunner _runner;

    public AudioExtractionJobExecutor(
        AppPaths paths,
        ProjectManifest project,
        string? ffmpegPath = null,
        FfmpegProgressRunner? runner = null)
    {
        _paths = paths;
        _project = project;
        _ffmpegPath = ffmpegPath
            ?? MediaToolLocator.Locate(paths, "ffmpeg", "TOOL_VIETSUB_FFMPEG_PATH");
        _runner = runner ?? new FfmpegProgressRunner();
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

        var sourcePath = source.ImportMode == "COPY" && source.WorkspaceRelativePath is not null
            ? _paths.GetProjectPath(_project.ProjectId, source.WorkspaceRelativePath)
            : source.OriginalPath;
        await VerifySourceIntegrityAsync(source, sourcePath, cancellationToken);

        var relativeOutput = Path.Combine("audio", "source-16k-mono.wav");
        var outputPath = _paths.GetProjectPath(_project.ProjectId, relativeOutput);
        var partialPath = _paths.GetProjectPath(_project.ProjectId, "temp", $"audio-{job.JobId:N}.partial.wav");
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        try
        {
            await reportProgress(new JobProgressUpdate("EXTRACT_AUDIO", 0, 0, "Đang chuẩn hóa audio WAV mono 16 kHz."));
            await _runner.RunAsync(
                _ffmpegPath,
                [
                    "-y",
                    "-v", "error",
                    "-i", sourcePath,
                    "-map", "0:a:0",
                    "-vn",
                    "-ac", "1",
                    "-ar", "16000",
                    "-c:a", "pcm_s16le",
                    "-f", "wav",
                    partialPath,
                ],
                source.Metadata.DurationSeconds,
                progress => reportProgress(new JobProgressUpdate(
                    "EXTRACT_AUDIO",
                    progress,
                    progress,
                    progress >= 100 ? "Đã chuẩn hóa audio." : null)),
                cancellationToken);
            File.Move(partialPath, outputPath, overwrite: true);
            var outputInfo = new FileInfo(outputPath);
            var hash = await CalculateHashAsync(outputPath, cancellationToken);
            _project.AudioTracks.RemoveAll(item => item.Role == "SOURCE_AUDIO");
            _project.AudioTracks.Add(new LocalMediaReference
            {
                Role = "SOURCE_AUDIO",
                ImportMode = "GENERATED",
                OriginalPath = string.Empty,
                WorkspaceRelativePath = relativeOutput,
                FileName = outputInfo.Name,
                SizeBytes = outputInfo.Length,
                Sha256 = hash,
                SourceLastWriteAtUtc = outputInfo.LastWriteTimeUtc,
                Metadata = new MediaMetadata
                {
                    DurationSeconds = source.Metadata.DurationSeconds,
                    HasAudio = true,
                    AudioTrackCount = 1,
                    AudioCodec = "pcm_s16le",
                    AudioChannels = 1,
                    AudioSampleRate = 16000,
                    Container = "wav",
                },
            });
            job.Steps.Single(item => item.Code == "EXTRACT_AUDIO").OutputRelativePath = relativeOutput;
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private static async Task VerifySourceIntegrityAsync(
        LocalMediaReference source,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists || info.Length != source.SizeBytes)
        {
            throw new LocalJobException(
                "MEDIA_SOURCE_CHANGED",
                "Video nguồn đã bị di chuyển hoặc thay đổi kể từ khi nhập.",
                retryable: false);
        }

        var currentHash = await CalculateHashAsync(sourcePath, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(currentHash),
            Convert.FromHexString(source.Sha256)))
        {
            throw new LocalJobException(
                "MEDIA_SOURCE_CHANGED",
                "Nội dung video nguồn đã thay đổi kể từ khi nhập.",
                retryable: false);
        }
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
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

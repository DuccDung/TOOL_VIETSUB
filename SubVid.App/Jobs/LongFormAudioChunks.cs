using System.Text.Json;
using SubVid.App.Core;
using SubVid.App.Media;

namespace SubVid.App.Jobs;

public sealed record LongFormAudioChunk(
    int Index,
    long OwnershipStartMilliseconds,
    long OwnershipEndMilliseconds,
    long ExtractionStartMilliseconds,
    long ExtractionEndMilliseconds)
{
    public long ExtractionDurationMilliseconds =>
        ExtractionEndMilliseconds - ExtractionStartMilliseconds;

    public bool Owns(long startMilliseconds, long endMilliseconds)
    {
        var midpoint = startMilliseconds + Math.Max(0, endMilliseconds - startMilliseconds) / 2;
        return midpoint >= OwnershipStartMilliseconds && midpoint < OwnershipEndMilliseconds;
    }
}

public static class LongFormAudioChunkPlanner
{
    public const long DefaultChunkDurationMilliseconds = 10 * 60 * 1000;
    public const long DefaultOverlapMilliseconds = 2500;

    public static IReadOnlyList<LongFormAudioChunk> Plan(
        long durationMilliseconds,
        long chunkDurationMilliseconds = DefaultChunkDurationMilliseconds,
        long overlapMilliseconds = DefaultOverlapMilliseconds)
    {
        if (durationMilliseconds <= 0)
        {
            return [];
        }

        chunkDurationMilliseconds = Math.Clamp(chunkDurationMilliseconds, 60_000, 30 * 60 * 1000);
        overlapMilliseconds = Math.Clamp(overlapMilliseconds, 0, Math.Min(10_000, chunkDurationMilliseconds / 4));
        var result = new List<LongFormAudioChunk>();
        for (long ownershipStart = 0; ownershipStart < durationMilliseconds; ownershipStart += chunkDurationMilliseconds)
        {
            var ownershipEnd = Math.Min(durationMilliseconds, ownershipStart + chunkDurationMilliseconds);
            result.Add(new LongFormAudioChunk(
                result.Count,
                ownershipStart,
                ownershipEnd,
                Math.Max(0, ownershipStart - overlapMilliseconds),
                Math.Min(durationMilliseconds, ownershipEnd + overlapMilliseconds)));
        }

        return result;
    }
}

public interface ILongFormAudioChunkExtractor
{
    Task<string> ExtractAsync(
        LongFormAudioChunk chunk,
        Func<double, ValueTask> reportProgress,
        CancellationToken cancellationToken);
}

public sealed class FfmpegLongFormAudioChunkExtractor : ILongFormAudioChunkExtractor
{
    private readonly AppPaths _paths;
    private readonly ProjectManifest _project;
    private readonly string _ffmpegPath;
    private readonly FfmpegProgressRunner _runner;

    public FfmpegLongFormAudioChunkExtractor(
        AppPaths paths,
        ProjectManifest project,
        string? ffmpegPath = null,
        FfmpegProgressRunner? runner = null)
    {
        _paths = paths;
        _project = project;
        _ffmpegPath = ffmpegPath ?? MediaToolLocator.Locate(paths, "ffmpeg", "SUBVID_FFMPEG_PATH");
        _runner = runner ?? new FfmpegProgressRunner();
    }

    public async Task<string> ExtractAsync(
        LongFormAudioChunk chunk,
        Func<double, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var source = _project.SourceVideo
            ?? throw new LocalJobException("MEDIA_SOURCE_MISSING", "Dự án chưa có video nguồn.", retryable: false);
        var sourcePath = source.ImportMode == "COPY" && source.WorkspaceRelativePath is not null
            ? _paths.GetProjectPath(_project.ProjectId, source.WorkspaceRelativePath)
            : source.OriginalPath;
        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists || sourceInfo.Length != source.SizeBytes)
        {
            throw new LocalJobException(
                "MEDIA_SOURCE_CHANGED",
                "Video nguồn đã bị di chuyển hoặc thay đổi kể từ khi nhập.",
                retryable: false);
        }

        var chunkDirectory = _paths.GetProjectPath(_project.ProjectId, "temp", "transcription-chunks");
        Directory.CreateDirectory(chunkDirectory);
        var outputPath = Path.Combine(chunkDirectory, $"chunk-{chunk.Index:D4}.wav");
        var partialPath = outputPath + ".partial.wav";
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        try
        {
            var startSeconds = chunk.ExtractionStartMilliseconds / 1000d;
            var durationSeconds = Math.Max(0.001, chunk.ExtractionDurationMilliseconds / 1000d);
            await _runner.RunAsync(
                _ffmpegPath,
                [
                    "-y",
                    "-v", "error",
                    "-ss", startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-i", sourcePath,
                    "-t", durationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-map", "0:a:0",
                    "-vn",
                    "-ac", "1",
                    "-ar", "16000",
                    "-c:a", "pcm_s16le",
                    "-f", "wav",
                    partialPath,
                ],
                durationSeconds,
                reportProgress,
                cancellationToken);
            File.Move(partialPath, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }
}

internal sealed class TranscriptionChunkCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private readonly string _path;

    public TranscriptionChunkCheckpointStore(AppPaths paths, Guid projectId, Guid jobId)
    {
        _path = paths.GetProjectPath(
            projectId,
            "cache",
            "transcription",
            $"job-{jobId:N}.json");
    }

    public async Task<TranscriptionChunkCheckpoint> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new TranscriptionChunkCheckpoint();
        }

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<TranscriptionChunkCheckpoint>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                ?? new TranscriptionChunkCheckpoint();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new TranscriptionChunkCheckpoint();
        }
    }

    public async Task SaveAsync(
        TranscriptionChunkCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var partialPath = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(partialPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    public void Reset()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

internal sealed class TranscriptionChunkCheckpoint
{
    public string SourceSha256 { get; set; } = string.Empty;

    public Dictionary<int, long> HighWatermarks { get; set; } = [];

    public HashSet<int> CompletedChunks { get; set; } = [];
}

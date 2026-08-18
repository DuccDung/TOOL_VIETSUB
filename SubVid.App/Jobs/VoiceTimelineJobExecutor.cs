using System.Globalization;
using System.Security.Cryptography;
using SubVid.App.Core;
using SubVid.App.Media;

namespace SubVid.App.Jobs;

public sealed class VoiceTimelineJobExecutor : ILocalJobExecutor
{
    private const int MaximumCueInputs = 500;
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly string _ffmpegPath;
    private readonly IFfmpegProgressRunner _runner;

    public VoiceTimelineJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        string? ffmpegPath = null,
        IFfmpegProgressRunner? runner = null)
    {
        _paths = paths;
        _workspace = workspace;
        _project = project;
        _ffmpegPath = ffmpegPath ?? MediaToolLocator.Locate(paths, "ffmpeg", "SUBVID_FFMPEG_PATH");
        _runner = runner ?? new FfmpegProgressRunner();
    }

    public async Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var source = _project.SourceVideo
            ?? throw new LocalJobException("MEDIA_SOURCE_MISSING", "Dự án chưa có video nguồn.", retryable: false);
        var track = _project.SubtitleTracks.LastOrDefault(item => item.Cues.Count > 0)
            ?? throw new LocalJobException("SUBTITLE_TRACK_MISSING", "Chưa có timeline phụ đề.", retryable: false);
        var voiceByCue = _project.AudioTracks
            .Where(item => item.Role == "VOICE_CUE" && item.CueId.HasValue)
            .GroupBy(item => item.CueId!.Value)
            .ToDictionary(group => group.Key, group => group.Last());
        var inputs = track.Cues
            .Where(cue => voiceByCue.ContainsKey(cue.CueId))
            .Select(cue => new CueInput(cue, voiceByCue[cue.CueId]))
            .ToArray();
        if (inputs.Length == 0)
        {
            throw new LocalJobException("VOICE_CUES_MISSING", "Chưa có giọng Việt theo từng phân đoạn.", retryable: false);
        }

        if (inputs.Length > MaximumCueInputs)
        {
            throw new LocalJobException(
                "VOICE_TIMELINE_TOO_MANY_CUES",
                $"Bản hiện tại hỗ trợ tối đa {MaximumCueInputs} đoạn giọng trong một lần đồng bộ.",
                retryable: false);
        }

        var projectDirectory = _paths.GetProjectDirectory(_project.ProjectId);
        var arguments = new List<string> { "-y", "-v", "error" };
        var filters = new List<string>(inputs.Length + 1);
        for (var index = 0; index < inputs.Length; index++)
        {
            var input = inputs[index];
            if (input.Media.WorkspaceRelativePath is null)
            {
                throw new LocalJobException("VOICE_FILE_MISSING", "Thiếu file giọng Việt trong workspace.");
            }

            var path = _paths.GetProjectPath(_project.ProjectId, input.Media.WorkspaceRelativePath);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != input.Media.SizeBytes)
            {
                throw new LocalJobException("VOICE_FILE_CHANGED", "File giọng Việt đã bị thay đổi hoặc xóa.");
            }

            var currentHash = await CalculateHashAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(input.Media.Sha256)
                || !string.Equals(currentHash, input.Media.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalJobException(
                    "VOICE_FILE_CHANGED",
                    "Ná»™i dung file giá»ng Viá»‡t Ä‘Ã£ bá»‹ thay Ä‘á»•i.");
            }

            // ProcessStartInfo.ArgumentList is flattened into one command line on Windows.
            // Keeping every generated input relative prevents large projects from exceeding
            // the 32,767-character CreateProcess limit.
            arguments.AddRange(["-i", Path.GetRelativePath(projectDirectory, path)]);
            var targetDuration = Math.Max(0.1, (input.Cue.EndMilliseconds - input.Cue.StartMilliseconds) / 1000d);
            var sourceDuration = Math.Max(0.01, input.Media.Metadata.DurationSeconds);
            var tempo = BuildAtempo(sourceDuration / targetDuration);
            var delay = Math.Max(0, input.Cue.StartMilliseconds);
            filters.Add(FormattableString.Invariant(
                $"[{index}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,{tempo},apad,atrim=0:{targetDuration:0.######},adelay={delay}:all=1[v{index}]")
                .Replace(",,", ",", StringComparison.Ordinal));
        }

        var labels = string.Concat(Enumerable.Range(0, inputs.Length).Select(index => $"[v{index}]"));
        var projectDuration = Math.Max(0.1, source.Metadata.DurationSeconds);
        filters.Add(FormattableString.Invariant(
            $"{labels}amix=inputs={inputs.Length}:duration=longest:normalize=0,alimiter=limit=0.95,apad,atrim=0:{projectDuration:0.######}[voice]") );
        var relativeFilterScript = Path.Combine("temp", $"voice-filter-{job.JobId:N}.txt");
        var relativePartialPath = Path.Combine("temp", $"voice-timeline-{job.JobId:N}.partial.wav");
        var filterScript = _paths.GetProjectPath(_project.ProjectId, relativeFilterScript);
        var partialPath = _paths.GetProjectPath(_project.ProjectId, relativePartialPath);
        var relativeOutput = Path.Combine("voice", "voice-timeline.wav");
        var outputPath = _paths.GetProjectPath(_project.ProjectId, relativeOutput);
        try
        {
            await File.WriteAllTextAsync(filterScript, string.Join(";", filters), cancellationToken);
            arguments.AddRange([
                "-/filter_complex", relativeFilterScript,
                "-map", "[voice]",
                "-ar", "48000",
                "-ac", "2",
                "-c:a", "pcm_s16le",
                relativePartialPath,
            ]);
            await reportProgress(new JobProgressUpdate("SYNC_VOICE", 0, 0, "Đang khớp giọng Việt với timeline."));
            await _runner.RunAsync(
                _ffmpegPath,
                arguments,
                projectDuration,
                progress => reportProgress(new JobProgressUpdate("SYNC_VOICE", progress, progress)),
                cancellationToken,
                workingDirectory: projectDirectory);
            _ = WaveFileMetadata.Read(partialPath);
            File.Move(partialPath, outputPath, overwrite: true);
            var file = new FileInfo(outputPath);
            var hash = await CalculateHashAsync(outputPath, cancellationToken);
            _project.AudioTracks.RemoveAll(item => item.Role == "VOICE_TIMELINE");
            _project.AudioTracks.Add(new LocalMediaReference
            {
                Role = "VOICE_TIMELINE",
                ImportMode = "GENERATED",
                WorkspaceRelativePath = relativeOutput,
                FileName = file.Name,
                SizeBytes = file.Length,
                Sha256 = hash,
                SourceLastWriteAtUtc = file.LastWriteTimeUtc,
                Metadata = new MediaMetadata
                {
                    DurationSeconds = projectDuration,
                    HasAudio = true,
                    AudioTrackCount = 1,
                    AudioCodec = "pcm_s16le",
                    AudioChannels = 2,
                    AudioSampleRate = 48000,
                    Container = "wav",
                },
            });
            job.Steps.Single(item => item.Code == "SYNC_VOICE").OutputRelativePath = relativeOutput;
            await _workspace.SaveAsync(_project, cancellationToken);
        }
        finally
        {
            if (File.Exists(filterScript)) File.Delete(filterScript);
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    public static string BuildAtempo(double factor)
    {
        var stages = new List<double>();
        var remaining = Math.Clamp(factor, 0.0625, 16);
        while (remaining > 2)
        {
            stages.Add(2);
            remaining /= 2;
        }

        while (remaining < 0.5)
        {
            stages.Add(0.5);
            remaining /= 0.5;
        }

        stages.Add(remaining);
        return string.Join(',', stages.Select(value =>
            "atempo=" + value.ToString("0.######", CultureInfo.InvariantCulture)));
    }

    private static async Task<string> CalculateHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private sealed record CueInput(SubtitleCue Cue, LocalMediaReference Media);
}

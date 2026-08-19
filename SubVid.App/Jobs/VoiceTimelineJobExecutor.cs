using System.Globalization;
using System.Security.Cryptography;
using SubVid.App.Core;
using SubVid.App.Media;

namespace SubVid.App.Jobs;

public sealed class VoiceTimelineJobExecutor : ILocalJobExecutor
{
    private const int MaximumInputsPerPartition = 500;
    private const long TargetPartitionDurationMilliseconds = 10 * 60 * 1000;
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
        var orderedCues = track.Cues
            .OrderBy(cue => cue.StartMilliseconds)
            .ThenBy(cue => cue.EndMilliseconds)
            .ToArray();
        var cueById = orderedCues.ToDictionary(cue => cue.CueId);
        var phrasePlans = VoicePhrasePlanner.Plan(
            _project,
            orderedCues,
            _project.Settings.VoicePhraseGapMilliseconds,
            _project.Settings.VoicePhraseMaximumDurationSeconds);
        var currentPhrases = phrasePlans
            .Where(phrase => phrase.Cues.Count > 1)
            .ToDictionary(phrase => phrase.PhraseId, StringComparer.Ordinal);
        var phraseInputs = (_project.Settings.VoicePhraseSynthesisEnabled
                ? _project.AudioTracks
                : [])
            .Where(item => item.Role == "VOICE_PHRASE"
                && item.VoicePhraseId is { } phraseId
                && currentPhrases.TryGetValue(phraseId, out var phrase)
                && item.CueIds.SequenceEqual(phrase.Cues.Select(cue => cue.CueId))
                && string.Equals(
                    item.ContentFingerprint,
                    VoiceSynthesisJobExecutor.BuildPhraseFingerprint(_project, phrase),
                    StringComparison.Ordinal))
            .GroupBy(item => item.VoicePhraseId!, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Select(media => new
            {
                Cues = media.CueIds.Select(cueId => cueById[cueId])
                    .OrderBy(cue => cue.StartMilliseconds)
                    .ToArray(),
                Media = media,
                PhraseId = media.VoicePhraseId,
            })
            .ToArray();
        var phraseCoveredCueIds = phraseInputs
            .SelectMany(input => input.Cues)
            .Select(cue => cue.CueId)
            .ToHashSet();
        var phrasePlannedCueIds = _project.Settings.VoicePhraseSynthesisEnabled
            ? currentPhrases
                .SelectMany(item => item.Value.Cues)
                .Select(cue => cue.CueId)
                .ToHashSet()
            : [];
        var rawInputs = phraseInputs
            .Select(input => new RawCueInput(input.Cues, input.Media, input.PhraseId))
            .Concat(orderedCues
                .Where(cue => !phraseCoveredCueIds.Contains(cue.CueId)
                    && !phrasePlannedCueIds.Contains(cue.CueId)
                    && voiceByCue.ContainsKey(cue.CueId))
                .Select(cue => new RawCueInput(
                    [cue],
                    voiceByCue[cue.CueId],
                    null)))
            .OrderBy(input => input.Cues[0].StartMilliseconds)
            .ThenBy(input => input.Cues[^1].EndMilliseconds)
            .ToArray();
        var inputs = rawInputs
            .Select((input, index) => new CueInput(
                input.Cues,
                input.Media,
                index + 1 < rawInputs.Length
                    ? rawInputs[index + 1].Cues[0].StartMilliseconds
                    : null,
                input.PhraseId))
            .ToArray();
        if (inputs.Length == 0)
        {
            throw new LocalJobException("VOICE_CUES_MISSING", "Chưa có giọng Việt theo từng phân đoạn.", retryable: false);
        }

        job.VoiceMetrics ??= new VoiceSynthesisJobMetrics
        {
            TotalCues = inputs.Sum(input => input.Cues.Count),
        };

        var projectDirectory = _paths.GetProjectDirectory(_project.ProjectId);
        var preparedInputs = new List<PreparedCueInput>(inputs.Length);
        var maximumTempo = VoiceTimelineFitPolicy.NormalizeMaximumTempo(
            _project.Settings.VoiceTimelineMaximumTempo);
        var preferredTempo = VoiceTimelineFitPolicy.NormalizePreferredTempo(
            _project.Settings.VoiceTimelinePreferredTempo,
            maximumTempo);
        var projectDuration = Math.Max(0.1, source.Metadata.DurationSeconds);
        var projectDurationMilliseconds = (long)Math.Round(projectDuration * 1_000);
        var estimatedPcmBytes = (long)Math.Ceiling(projectDuration * 48_000 * 2 * sizeof(short));
        var estimatedTimelineBytes = checked(estimatedPcmBytes * 2 + 512L * 1024 * 1024);
        DiskSpaceGuard.EnsureAvailable(
            _paths.GetProjectDirectory(_project.ProjectId),
            estimatedTimelineBytes,
            "dựng timeline giọng Việt");
        var maximumBorrowMilliseconds = Math.Clamp(
            _project.Settings.VoiceTimelineMaximumBorrowMilliseconds,
            0,
            2_000);
        var minimumGapMilliseconds = Math.Clamp(
            _project.Settings.VoiceTimelineMinimumGapMilliseconds,
            0,
            500);
        var reviewRequiredCount = 0;
        var invalidDurationCount = 0;
        await reportProgress(new JobProgressUpdate(
            "SYNC_VOICE",
            0,
            0,
            "Đang kiểm tra thời lượng từng câu trước khi khớp timeline."));
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

            var inputStartMilliseconds = input.Cues[0].StartMilliseconds;
            var inputEndMilliseconds = input.Cues[^1].EndMilliseconds;
            var targetDuration = (inputEndMilliseconds - inputStartMilliseconds) / 1000d;
            var activity = _project.Settings.VoiceTrimSilenceEnabled
                ? VoiceActivityAnalyzer.Analyze(path, input.Media.Metadata.DurationSeconds)
                : VoiceActivityAnalysis.UseWholeFile(input.Media.Metadata.DurationSeconds);
            var nextBoundary = input.NextStartMilliseconds ?? projectDurationMilliseconds;
            var maximumExtendedEnd = inputEndMilliseconds + maximumBorrowMilliseconds;
            var safeEnd = Math.Min(maximumExtendedEnd, nextBoundary - minimumGapMilliseconds);
            safeEnd = Math.Clamp(
                Math.Max(inputEndMilliseconds, safeEnd),
                inputEndMilliseconds,
                Math.Max(inputEndMilliseconds, projectDurationMilliseconds));
            var borrowedGap = Math.Max(0, safeEnd - inputEndMilliseconds) / 1000d;
            var effectiveWindow = Math.Max(
                targetDuration,
                (safeEnd - inputStartMilliseconds) / 1000d);
            var timing = VoiceTimelineFitPolicy.Analyze(new VoiceTimelineFitInput(
                activity.RawDurationSeconds,
                activity.PlayableDurationSeconds,
                targetDuration,
                effectiveWindow,
                input.Cues.Sum(cue => cue.TranslatedText.Trim().Length),
                maximumTempo,
                preferredTempo,
                activity.LeadingSilenceSeconds,
                activity.TrailingSilenceSeconds,
                activity.TrimStartSeconds,
                activity.TrimEndSeconds,
                borrowedGap,
                Math.Clamp(_project.Settings.VoiceSpeed, -3, 3),
                input.PhraseId));
            foreach (var cue in input.Cues)
            {
                cue.VoiceTiming = timing with { };
            }
            if (timing.Status == VoiceTimingStatuses.ReviewRequired)
            {
                reviewRequiredCount += input.Cues.Count;
            }
            else if (timing.Status == VoiceTimingStatuses.Invalid)
            {
                invalidDurationCount += input.Cues.Count;
            }

            var filterChain = new List<string>();
            if (activity.IsReliable
                && (activity.TrimStartSeconds > 0.001
                    || activity.TrimEndSeconds < activity.RawDurationSeconds - 0.001))
            {
                filterChain.Add(FormattableString.Invariant(
                    $"atrim=start={activity.TrimStartSeconds:0.######}:end={activity.TrimEndSeconds:0.######}"));
                filterChain.Add("asetpts=PTS-STARTPTS");
            }

            filterChain.Add("aresample=48000");
            filterChain.Add("aformat=sample_fmts=fltp:channel_layouts=stereo");
            if (timing.AppliedTempo is > 1)
            {
                filterChain.Add(BuildAtempo(timing.AppliedTempo.Value));
            }

            filterChain.Add("apad");
            filterChain.Add(FormattableString.Invariant(
                $"atrim=0:{Math.Max(0.01, timing.RenderDurationSeconds):0.######}"));
            preparedInputs.Add(new PreparedCueInput(
                input,
                Path.GetRelativePath(projectDirectory, path),
                filterChain));
        }

        await _workspace.SaveAsync(_project, cancellationToken);
        if (invalidDurationCount > 0)
        {
            throw new LocalJobException(
                "VOICE_DURATION_INVALID",
                $"Có {invalidDurationCount} câu có thời lượng WAV hoặc phụ đề không hợp lệ. Hãy kiểm tra timeline trước khi thử lại.",
                retryable: false);
        }

        if (job.VoiceMetrics is not null)
        {
            job.VoiceMetrics.TimingWarningCues = reviewRequiredCount;
        }

        var relativePartialPath = Path.Combine("temp", $"voice-timeline-{job.JobId:N}.partial.wav");
        var partialPath = _paths.GetProjectPath(_project.ProjectId, relativePartialPath);
        var relativeOutput = Path.Combine("voice", "voice-timeline.wav");
        var outputPath = _paths.GetProjectPath(_project.ProjectId, relativeOutput);
        try
        {
            await reportProgress(new JobProgressUpdate("SYNC_VOICE", 0, 0, "Đang khớp giọng Việt với timeline ở tốc độ an toàn."));
            await RenderPartitionedTimelineAsync(
                preparedInputs,
                projectDurationMilliseconds,
                job.JobId,
                relativePartialPath,
                reportProgress,
                cancellationToken);
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
                IsStale = false,
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
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    private async Task RenderPartitionedTimelineAsync(
        IReadOnlyList<PreparedCueInput> inputs,
        long projectDurationMilliseconds,
        Guid jobId,
        string relativeFinalPath,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var partitions = BuildPartitions(inputs, projectDurationMilliseconds);
        var stemPaths = new List<string>(partitions.Count);
        try
        {
            for (var partitionIndex = 0; partitionIndex < partitions.Count; partitionIndex++)
            {
                var partition = partitions[partitionIndex];
                var isOnlyPartition = partitions.Count == 1;
                var relativeFilterScript = Path.Combine(
                    "temp",
                    isOnlyPartition
                        ? $"voice-filter-{jobId:N}.txt"
                        : $"voice-filter-{jobId:N}-{partitionIndex:D4}.txt");
                var relativeStemPath = isOnlyPartition
                    ? relativeFinalPath
                    : Path.Combine("temp", $"voice-stem-{jobId:N}-{partitionIndex:D4}.wav");
                var filterScript = _paths.GetProjectPath(_project.ProjectId, relativeFilterScript);
                var stemPath = _paths.GetProjectPath(_project.ProjectId, relativeStemPath);
                stemPaths.Add(stemPath);
                try
                {
                    var arguments = new List<string> { "-y", "-v", "error" };
                    var filters = new List<string>(partition.Inputs.Count + 1);
                    for (var inputIndex = 0; inputIndex < partition.Inputs.Count; inputIndex++)
                    {
                        var input = partition.Inputs[inputIndex];
                        arguments.AddRange(["-i", input.RelativePath]);
                        var delay = Math.Max(
                            0,
                            input.Input.Cues[0].StartMilliseconds - partition.StartMilliseconds);
                        filters.Add(FormattableString.Invariant(
                            $"[{inputIndex}:a]{string.Join(',', input.FilterChain)},adelay={delay}:all=1[v{inputIndex}]"));
                    }

                    var partitionDuration = Math.Max(
                        0.01,
                        (partition.EndMilliseconds - partition.StartMilliseconds) / 1000d);
                    var labels = string.Concat(Enumerable.Range(0, partition.Inputs.Count).Select(index => $"[v{index}]"));
                    filters.Add(FormattableString.Invariant(
                        $"{labels}amix=inputs={partition.Inputs.Count}:duration=longest:normalize=0,alimiter=limit=0.95,apad,atrim=0:{partitionDuration:0.######}[voice]"));
                    await File.WriteAllTextAsync(filterScript, string.Join(";", filters), cancellationToken);
                    arguments.AddRange([
                        "-/filter_complex", relativeFilterScript,
                        "-map", "[voice]",
                        "-ar", "48000",
                        "-ac", "2",
                        "-c:a", "pcm_s16le",
                        relativeStemPath,
                    ]);
                    await _runner.RunAsync(
                        _ffmpegPath,
                        arguments,
                        partitionDuration,
                        progress => reportProgress(new JobProgressUpdate(
                            "SYNC_VOICE",
                            (partitionIndex + progress / 100d) * 100d / partitions.Count,
                            (partitionIndex + progress / 100d) * 100d / partitions.Count,
                            partitions.Count > 1
                                ? $"Đang dựng phần giọng {partitionIndex + 1}/{partitions.Count}."
                                : null)),
                        cancellationToken,
                        workingDirectory: _paths.GetProjectDirectory(_project.ProjectId));
                    _ = WaveFileMetadata.Read(stemPath);
                }
                finally
                {
                    if (File.Exists(filterScript))
                    {
                        File.Delete(filterScript);
                    }
                }
            }

            if (partitions.Count > 1)
            {
                await WavePcmConcatenator.ConcatenateAsync(
                    stemPaths,
                    _paths.GetProjectPath(_project.ProjectId, relativeFinalPath),
                    cancellationToken);
            }
        }
        finally
        {
            if (partitions.Count > 1)
            {
                foreach (var stemPath in stemPaths)
                {
                    if (File.Exists(stemPath))
                    {
                        File.Delete(stemPath);
                    }
                }
            }
        }
    }

    private static IReadOnlyList<VoiceTimelinePartition> BuildPartitions(
        IReadOnlyList<PreparedCueInput> inputs,
        long projectDurationMilliseconds)
    {
        var partitions = new List<VoiceTimelinePartition>();
        var startIndex = 0;
        var partitionStart = 0L;
        for (var index = 1; index < inputs.Count; index++)
        {
            var nextStart = inputs[index].Input.Cues[0].StartMilliseconds;
            var inputLimitReached = index - startIndex >= MaximumInputsPerPartition;
            var durationLimitReached = nextStart - partitionStart >= TargetPartitionDurationMilliseconds;
            if (!inputLimitReached && !durationLimitReached)
            {
                continue;
            }

            partitions.Add(new VoiceTimelinePartition(
                partitionStart,
                nextStart,
                inputs.Skip(startIndex).Take(index - startIndex).ToArray()));
            partitionStart = nextStart;
            startIndex = index;
        }

        partitions.Add(new VoiceTimelinePartition(
            partitionStart,
            Math.Max(partitionStart + 1, projectDurationMilliseconds),
            inputs.Skip(startIndex).ToArray()));
        return partitions;
    }

    public static string BuildAtempo(double factor)
    {
        if (!double.IsFinite(factor)
            || factor < 1
            || factor > VoiceTimelineFitPolicy.MaximumMaximumAutomaticTempo + 0.000001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(factor),
                factor,
                $"Tốc độ khớp timeline phải nằm trong khoảng 1.0x đến {VoiceTimelineFitPolicy.MaximumMaximumAutomaticTempo:0.##}x.");
        }

        return "atempo=" + factor.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static async Task<string> CalculateHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private sealed record RawCueInput(
        IReadOnlyList<SubtitleCue> Cues,
        LocalMediaReference Media,
        string? PhraseId);

    private sealed record CueInput(
        IReadOnlyList<SubtitleCue> Cues,
        LocalMediaReference Media,
        long? NextStartMilliseconds,
        string? PhraseId);

    private sealed record PreparedCueInput(
        CueInput Input,
        string RelativePath,
        IReadOnlyList<string> FilterChain);

    private sealed record VoiceTimelinePartition(
        long StartMilliseconds,
        long EndMilliseconds,
        IReadOnlyList<PreparedCueInput> Inputs);
}

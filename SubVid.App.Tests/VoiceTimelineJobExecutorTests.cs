using System.Security.Cryptography;
using System.Text;
using SubVid.App.Core;
using SubVid.App.Jobs;
using SubVid.App.Media;

namespace SubVid.App.Tests;

public sealed class VoiceTimelineJobExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_VOICE_TIMELINE_TEST",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExecuteAsync_With260Cues_UsesShortRelativeFfmpegInputs()
    {
        const int cueCount = 260;
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Voice timeline test");
        project.Settings.VoicePhraseSynthesisEnabled = false;
        project.SourceVideo = new LocalMediaReference
        {
            FileName = "source.mp4",
            Metadata = new MediaMetadata { DurationSeconds = cueCount + 1 },
        };

        var track = new SubtitleDocument { LanguageCode = "vi" };
        var waveBytes = CreateWave(sampleRate: 16_000, durationMilliseconds: 100);
        var waveHash = Convert.ToHexString(SHA256.HashData(waveBytes)).ToLowerInvariant();
        for (var index = 0; index < cueCount; index++)
        {
            var cue = new SubtitleCue
            {
                StartMilliseconds = index * 1_000L,
                EndMilliseconds = (index * 1_000L) + 900,
                TranslatedText = $"Câu {index + 1}",
            };
            track.Cues.Add(cue);

            var relativePath = Path.Combine("voice", $"cue-{cue.CueId:N}.wav");
            var absolutePath = paths.GetProjectPath(project.ProjectId, relativePath);
            await File.WriteAllBytesAsync(absolutePath, waveBytes);
            project.AudioTracks.Add(new LocalMediaReference
            {
                CueId = cue.CueId,
                Role = "VOICE_CUE",
                ImportMode = "GENERATED",
                WorkspaceRelativePath = relativePath,
                FileName = Path.GetFileName(absolutePath),
                SizeBytes = waveBytes.Length,
                Sha256 = waveHash,
                Metadata = new MediaMetadata
                {
                    DurationSeconds = 0.1,
                    HasAudio = true,
                    AudioSampleRate = 16_000,
                    AudioChannels = 1,
                },
            });
        }

        project.SubtitleTracks.Add(track);
        await workspace.SaveAsync(project);
        var runner = new RecordingFfmpegRunner();
        var job = new LocalJob
        {
            JobType = "SYNTHESIZE_VOICE_LOCAL",
            Steps = [new LocalJobStep { Code = "SYNC_VOICE" }],
        };

        await new VoiceTimelineJobExecutor(
                paths,
                workspace,
                project,
                ffmpegPath: "ffmpeg-test.exe",
                runner)
            .ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        var projectDirectory = paths.GetProjectDirectory(project.ProjectId);
        Assert.Equal(projectDirectory, runner.WorkingDirectory);
        var inputPaths = runner.Arguments
            .Select((argument, index) => (argument, index))
            .Where(item => item.index > 0 && runner.Arguments[item.index - 1] == "-i")
            .Select(item => item.argument)
            .ToArray();
        Assert.Equal(cueCount, inputPaths.Length);
        Assert.All(inputPaths, path => Assert.False(Path.IsPathRooted(path), path));
        Assert.All(inputPaths, path => Assert.StartsWith($"voice{Path.DirectorySeparatorChar}", path));
        Assert.False(Path.IsPathRooted(runner.Arguments[^1]));
        Assert.DoesNotContain("atempo=", runner.FilterScriptContents, StringComparison.Ordinal);
        Assert.All(track.Cues, cue =>
        {
            Assert.Equal(VoiceTimingStatuses.Padded, cue.VoiceTiming?.Status);
            Assert.Equal(1, cue.VoiceTiming?.AppliedTempo);
        });

        var effectiveArguments = new[] { "-progress", "pipe:1", "-nostats" }
            .Concat(runner.Arguments)
            .ToArray();
        var commandLength = FfmpegProgressRunner.EstimateWindowsCommandLineLength(
            @"C:\Users\Admin\AppData\Local\TOOL_VIETSUB\Tools\ffmpeg\ffmpeg.exe",
            effectiveArguments);
        Assert.True(commandLength < FfmpegProgressRunner.SafeWindowsCommandLineLimit, commandLength.ToString());
        Assert.True(File.Exists(paths.GetProjectPath(project.ProjectId, "voice", "voice-timeline.wav")));
        var timeline = Assert.Single(project.AudioTracks, item => item.Role == "VOICE_TIMELINE");
        Assert.True(timeline.SizeBytes > 0);
        Assert.False(File.Exists(paths.GetProjectPath(
            project.ProjectId,
            "temp",
            $"voice-filter-{job.JobId:N}.txt")));
        Assert.False(File.Exists(paths.GetProjectPath(
            project.ProjectId,
            "temp",
            $"voice-timeline-{job.JobId:N}.partial.wav")));
    }

    [Fact]
    public void FiveHundredRelativeVoiceInputs_StayBelowSafeWindowsCommandLineLimit()
    {
        var arguments = new List<string> { "-progress", "pipe:1", "-nostats", "-y", "-v", "error" };
        for (var index = 0; index < 500; index++)
        {
            arguments.Add("-i");
            arguments.Add(Path.Combine("voice", $"cue-{Guid.NewGuid():N}.wav"));
        }

        arguments.AddRange([
            "-/filter_complex", Path.Combine("temp", "voice-filter.txt"),
            "-map", "[voice]",
            "-ar", "48000",
            "-ac", "2",
            "-c:a", "pcm_s16le",
            Path.Combine("temp", "voice-timeline.partial.wav"),
        ]);

        var length = FfmpegProgressRunner.EstimateWindowsCommandLineLength(
            @"C:\Users\Admin\AppData\Local\TOOL_VIETSUB\Tools\ffmpeg\ffmpeg.exe",
            arguments);

        Assert.True(length < FfmpegProgressRunner.SafeWindowsCommandLineLimit, length.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_WithMoreThanFiveHundredInputs_RendersPartitionedTimeline()
    {
        const int cueCount = 501;
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Partitioned voice timeline");
        project.Settings.VoicePhraseSynthesisEnabled = false;
        project.SourceVideo = new LocalMediaReference
        {
            FileName = "source.mp4",
            Metadata = new MediaMetadata { DurationSeconds = cueCount + 1 },
        };
        var waveBytes = CreateWave(sampleRate: 16_000, durationMilliseconds: 100);
        var waveHash = Convert.ToHexString(SHA256.HashData(waveBytes)).ToLowerInvariant();
        var relativePath = Path.Combine("voice", "shared-cue.wav");
        await File.WriteAllBytesAsync(paths.GetProjectPath(project.ProjectId, relativePath), waveBytes);
        var track = new SubtitleDocument { LanguageCode = "vi" };
        for (var index = 0; index < cueCount; index++)
        {
            var cue = new SubtitleCue
            {
                StartMilliseconds = index * 1_000L,
                EndMilliseconds = index * 1_000L + 900,
                TranslatedText = $"Câu {index + 1}",
            };
            track.Cues.Add(cue);
            project.AudioTracks.Add(new LocalMediaReference
            {
                CueId = cue.CueId,
                Role = "VOICE_CUE",
                WorkspaceRelativePath = relativePath,
                SizeBytes = waveBytes.Length,
                Sha256 = waveHash,
                Metadata = new MediaMetadata { DurationSeconds = 0.1, HasAudio = true },
            });
        }

        project.SubtitleTracks.Add(track);
        await workspace.SaveAsync(project);
        var runner = new RecordingFfmpegRunner();
        var job = new LocalJob
        {
            JobType = "SYNTHESIZE_VOICE_LOCAL",
            Steps = [new LocalJobStep { Code = "SYNC_VOICE" }],
        };

        await new VoiceTimelineJobExecutor(paths, workspace, project, "ffmpeg-test.exe", runner)
            .ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(2, runner.CallCount);
        Assert.True(File.Exists(paths.GetProjectPath(project.ProjectId, "voice", "voice-timeline.wav")));
        Assert.Empty(Directory.EnumerateFiles(
            paths.GetProjectPath(project.ProjectId, "temp"),
            $"voice-stem-{job.JobId:N}-*.wav"));
        Assert.Empty(Directory.EnumerateFiles(
            paths.GetProjectPath(project.ProjectId, "temp"),
            $"voice-filter-{job.JobId:N}-*.txt"));
    }

    [Fact]
    public async Task WavePcmConcatenator_JoinsStereoStemsWithoutASecondFfmpegPass()
    {
        Directory.CreateDirectory(_root);
        var first = Path.Combine(_root, "stem-1.wav");
        var second = Path.Combine(_root, "stem-2.wav");
        var output = Path.Combine(_root, "joined.wav");
        await File.WriteAllBytesAsync(first, CreateWave(48_000, 100, channels: 2));
        await File.WriteAllBytesAsync(second, CreateWave(48_000, 200, channels: 2));

        await WavePcmConcatenator.ConcatenateAsync([first, second], output, CancellationToken.None);

        var metadata = WaveFileMetadata.Read(output);
        Assert.Equal(0.3, metadata.DurationSeconds, 3);
        Assert.Equal(48_000, metadata.SampleRate);
        Assert.Equal(2, metadata.Channels);
    }

    [Fact]
    public async Task RunAsync_WhenExecutableIsMissing_ReturnsSpecificError()
    {
        var runner = new FfmpegProgressRunner();

        var exception = await Assert.ThrowsAsync<LocalJobException>(() => runner.RunAsync(
            Path.Combine(_root, "missing-ffmpeg.exe"),
            [],
            1,
            _ => ValueTask.CompletedTask,
            CancellationToken.None));

        Assert.Equal("FFMPEG_NOT_FOUND", exception.Code);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCueNeedsUnsafeTempo_CreatesTimelineAndPersistsWarning()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Voice timing review");
        project.Settings.VoiceTimelineMaximumBorrowMilliseconds = 0;
        project.SourceVideo = new LocalMediaReference
        {
            FileName = "source.mp4",
            Metadata = new MediaMetadata { DurationSeconds = 3 },
        };
        var cue = new SubtitleCue
        {
            StartMilliseconds = 0,
            EndMilliseconds = 1_000,
            TranslatedText = "Đây là một câu quá dài",
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "vi", Cues = [cue] });
        var waveBytes = CreateWave(sampleRate: 16_000, durationMilliseconds: 1_300);
        var relativeCuePath = Path.Combine("voice", $"cue-{cue.CueId:N}.wav");
        var cuePath = paths.GetProjectPath(project.ProjectId, relativeCuePath);
        await File.WriteAllBytesAsync(cuePath, waveBytes);
        project.AudioTracks.Add(new LocalMediaReference
        {
            CueId = cue.CueId,
            Role = "VOICE_CUE",
            WorkspaceRelativePath = relativeCuePath,
            SizeBytes = waveBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(waveBytes)).ToLowerInvariant(),
            Metadata = new MediaMetadata { DurationSeconds = 1.3, HasAudio = true },
        });
        var previousTimelinePath = paths.GetProjectPath(project.ProjectId, "voice", "voice-timeline.wav");
        var previousTimeline = CreateWave(sampleRate: 48_000, durationMilliseconds: 500);
        await File.WriteAllBytesAsync(previousTimelinePath, previousTimeline);
        project.AudioTracks.Add(new LocalMediaReference
        {
            Role = "VOICE_TIMELINE",
            WorkspaceRelativePath = Path.Combine("voice", "voice-timeline.wav"),
            SizeBytes = previousTimeline.Length,
        });
        await workspace.SaveAsync(project);
        var runner = new RecordingFfmpegRunner();
        var job = new LocalJob
        {
            JobType = "SYNTHESIZE_VOICE_LOCAL",
            Steps = [new LocalJobStep { Code = "SYNC_VOICE" }],
        };

        await new VoiceTimelineJobExecutor(paths, workspace, project, "ffmpeg-test.exe", runner)
            .ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.True(runner.WasCalled);
        Assert.Equal(VoiceTimingStatuses.ReviewRequired, cue.VoiceTiming?.Status);
        Assert.Equal(VoiceTimelineFitPolicy.DefaultMaximumAutomaticTempo, cue.VoiceTiming?.AppliedTempo);
        Assert.Contains("atempo=1.2", runner.FilterScriptContents, StringComparison.Ordinal);
        Assert.NotEqual(previousTimeline.Length, (await File.ReadAllBytesAsync(previousTimelinePath)).Length);
        Assert.Equal(1, job.VoiceMetrics?.TimingWarningCues);
        Assert.Single(project.AudioTracks, item => item.Role == "VOICE_TIMELINE");
        var reopened = await workspace.OpenAsync(project.ProjectId);
        Assert.Equal(
            VoiceTimingStatuses.ReviewRequired,
            Assert.Single(Assert.Single(reopened.SubtitleTracks).Cues).VoiceTiming?.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCueNeedsSafeCompression_UsesOnlyRequiredTempo()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Voice timing compression");
        project.Settings.VoiceTimelineMaximumBorrowMilliseconds = 0;
        project.SourceVideo = new LocalMediaReference
        {
            FileName = "source.mp4",
            Metadata = new MediaMetadata { DurationSeconds = 2 },
        };
        var cue = new SubtitleCue
        {
            StartMilliseconds = 0,
            EndMilliseconds = 1_000,
            TranslatedText = "Câu dài hơn một chút",
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "vi", Cues = [cue] });
        var waveBytes = CreateWave(sampleRate: 16_000, durationMilliseconds: 1_100);
        var relativeCuePath = Path.Combine("voice", $"cue-{cue.CueId:N}.wav");
        var cuePath = paths.GetProjectPath(project.ProjectId, relativeCuePath);
        await File.WriteAllBytesAsync(cuePath, waveBytes);
        project.AudioTracks.Add(new LocalMediaReference
        {
            CueId = cue.CueId,
            Role = "VOICE_CUE",
            WorkspaceRelativePath = relativeCuePath,
            SizeBytes = waveBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(waveBytes)).ToLowerInvariant(),
            Metadata = new MediaMetadata { DurationSeconds = 1.1, HasAudio = true },
        });
        await workspace.SaveAsync(project);
        var runner = new RecordingFfmpegRunner();
        var job = new LocalJob
        {
            JobType = "SYNTHESIZE_VOICE_LOCAL",
            Steps = [new LocalJobStep { Code = "SYNC_VOICE" }],
        };

        await new VoiceTimelineJobExecutor(paths, workspace, project, "ffmpeg-test.exe", runner)
            .ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.True(runner.WasCalled);
        Assert.Contains("atempo=1.1", runner.FilterScriptContents, StringComparison.Ordinal);
        Assert.DoesNotContain("atempo=0.", runner.FilterScriptContents, StringComparison.Ordinal);
        Assert.Equal(VoiceTimingStatuses.Compressed, cue.VoiceTiming?.Status);
        Assert.Equal(1.1, cue.VoiceTiming!.AppliedTempo!.Value, 6);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFollowingGapIsAvailable_KeepsNaturalTempoAndBorrowsOnlySafeGap()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Voice natural gap fit");
        project.SourceVideo = new LocalMediaReference
        {
            FileName = "source.mp4",
            Metadata = new MediaMetadata { DurationSeconds = 3 },
        };
        var cue = new SubtitleCue
        {
            StartMilliseconds = 0,
            EndMilliseconds = 1_000,
            TranslatedText = "Câu dài hơn ô phụ đề nhưng vẫn còn khoảng trống",
        };
        var nextCue = new SubtitleCue
        {
            StartMilliseconds = 1_700,
            EndMilliseconds = 2_500,
            TranslatedText = string.Empty,
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "vi", Cues = [cue, nextCue] });
        var waveBytes = CreateWave(sampleRate: 16_000, durationMilliseconds: 1_300);
        var relativeCuePath = Path.Combine("voice", $"cue-{cue.CueId:N}.wav");
        var cuePath = paths.GetProjectPath(project.ProjectId, relativeCuePath);
        await File.WriteAllBytesAsync(cuePath, waveBytes);
        project.AudioTracks.Add(new LocalMediaReference
        {
            CueId = cue.CueId,
            Role = "VOICE_CUE",
            WorkspaceRelativePath = relativeCuePath,
            SizeBytes = waveBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(waveBytes)).ToLowerInvariant(),
            Metadata = new MediaMetadata { DurationSeconds = 1.3, HasAudio = true },
        });
        await workspace.SaveAsync(project);
        var runner = new RecordingFfmpegRunner();
        var job = new LocalJob
        {
            JobType = "SYNTHESIZE_VOICE_LOCAL",
            Steps = [new LocalJobStep { Code = "SYNC_VOICE" }],
        };

        await new VoiceTimelineJobExecutor(paths, workspace, project, "ffmpeg-test.exe", runner)
            .ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.True(runner.WasCalled);
        Assert.DoesNotContain("atempo=", runner.FilterScriptContents, StringComparison.Ordinal);
        Assert.Equal(VoiceTimingStatuses.GapFitted, cue.VoiceTiming?.Status);
        Assert.Equal(1, cue.VoiceTiming?.AppliedTempo);
        Assert.Equal(0.3, cue.VoiceTiming!.BorrowedGapSeconds, 6);
        Assert.Equal("BORROW_GAP", cue.VoiceTiming.ResolutionAction);
    }

    [Fact]
    public async Task ExecuteAsync_WithPhraseAudio_UsesOneInputAndAppliesDiagnosticsToEveryCue()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Voice phrase timeline");
        project.SourceVideo = new LocalMediaReference
        {
            FileName = "source.mp4",
            Metadata = new MediaMetadata { DurationSeconds = 4 },
        };
        var first = new SubtitleCue
        {
            StartMilliseconds = 0,
            EndMilliseconds = 1_000,
            TranslatedText = "Xin chào",
        };
        var second = new SubtitleCue
        {
            StartMilliseconds = 1_200,
            EndMilliseconds = 2_400,
            TranslatedText = "Bạn khỏe không?",
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "vi", Cues = [first, second] });
        var waveBytes = CreateWave(sampleRate: 16_000, durationMilliseconds: 2_000);
        var phrase = Assert.Single(VoicePhrasePlanner.Plan(
            project,
            [first, second],
            project.Settings.VoicePhraseGapMilliseconds,
            project.Settings.VoicePhraseMaximumDurationSeconds));
        var phraseId = phrase.PhraseId;
        var relativePath = Path.Combine("voice", $"phrase-{phraseId}.wav");
        var absolutePath = paths.GetProjectPath(project.ProjectId, relativePath);
        await File.WriteAllBytesAsync(absolutePath, waveBytes);
        project.AudioTracks.Add(new LocalMediaReference
        {
            CueIds = [first.CueId, second.CueId],
            VoicePhraseId = phraseId,
            Role = "VOICE_PHRASE",
            WorkspaceRelativePath = relativePath,
            SizeBytes = waveBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(waveBytes)).ToLowerInvariant(),
            ContentFingerprint = VoiceSynthesisJobExecutor.BuildPhraseFingerprint(project, phrase),
            Metadata = new MediaMetadata { DurationSeconds = 2, HasAudio = true },
        });
        project.AudioTracks.Add(new LocalMediaReference
        {
            CueId = first.CueId,
            Role = "VOICE_CUE",
            WorkspaceRelativePath = relativePath,
            SizeBytes = waveBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(waveBytes)).ToLowerInvariant(),
            Metadata = new MediaMetadata { DurationSeconds = 1, HasAudio = true },
        });
        project.AudioTracks.Add(new LocalMediaReference
        {
            CueId = second.CueId,
            Role = "VOICE_CUE",
            WorkspaceRelativePath = relativePath,
            SizeBytes = waveBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(waveBytes)).ToLowerInvariant(),
            Metadata = new MediaMetadata { DurationSeconds = 1, HasAudio = true },
        });
        await workspace.SaveAsync(project);
        var runner = new RecordingFfmpegRunner();
        var job = new LocalJob
        {
            JobType = "SYNTHESIZE_VOICE_LOCAL",
            Steps = [new LocalJobStep { Code = "SYNC_VOICE" }],
        };

        await new VoiceTimelineJobExecutor(paths, workspace, project, "ffmpeg-test.exe", runner)
            .ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(1, runner.Arguments.Select((value, index) => (value, index))
            .Count(item => item.index > 0 && runner.Arguments[item.index - 1] == "-i"));
        Assert.Equal(VoiceTimingStatuses.Padded, first.VoiceTiming?.Status);
        Assert.Equal(first.VoiceTiming, second.VoiceTiming);
        Assert.Equal(phraseId, first.VoiceTiming?.PhraseId);
    }

    [Fact]
    public async Task RunAsync_WhenCommandIsTooLong_ReturnsSpecificErrorBeforeStartingProcess()
    {
        var runner = new FfmpegProgressRunner();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
        var longArgument = new string('a', FfmpegProgressRunner.SafeWindowsCommandLineLimit);

        var exception = await Assert.ThrowsAsync<LocalJobException>(() => runner.RunAsync(
            executable,
            [longArgument],
            1,
            _ => ValueTask.CompletedTask,
            CancellationToken.None));

        Assert.Equal("FFMPEG_COMMAND_TOO_LONG", exception.Code);
        Assert.False(exception.Retryable);
    }

    private static byte[] CreateWave(int sampleRate, int durationMilliseconds, short channels = 1)
    {
        var sampleCount = sampleRate * durationMilliseconds / 1_000;
        var dataSize = sampleCount * sizeof(short) * channels;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short) * channels);
        writer.Write((short)(sizeof(short) * channels));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var index = 0; index < sampleCount * channels; index++) writer.Write((short)0);
        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingFfmpegRunner : IFfmpegProgressRunner
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public string? WorkingDirectory { get; private set; }

        public string FilterScriptContents { get; private set; } = string.Empty;

        public bool WasCalled { get; private set; }

        public async Task RunAsync(
            string ffmpegPath,
            IReadOnlyList<string> arguments,
            double durationSeconds,
            Func<double, ValueTask> reportProgress,
            CancellationToken cancellationToken,
            string? workingDirectory = null)
        {
            CallCount++;
            WasCalled = true;
            Arguments = arguments.ToArray();
            WorkingDirectory = workingDirectory;
            var filterIndex = Arguments.ToList().IndexOf("-/filter_complex");
            if (filterIndex >= 0 && filterIndex + 1 < Arguments.Count)
            {
                var filterPath = Path.IsPathRooted(Arguments[filterIndex + 1])
                    ? Arguments[filterIndex + 1]
                    : Path.Combine(workingDirectory!, Arguments[filterIndex + 1]);
                FilterScriptContents = await File.ReadAllTextAsync(filterPath, cancellationToken);
            }
            var outputPath = Path.IsPathRooted(arguments[^1])
                ? arguments[^1]
                : Path.Combine(workingDirectory!, arguments[^1]);
            await File.WriteAllBytesAsync(outputPath, CreateWave(48_000, 100, channels: 2), cancellationToken);
            await reportProgress(100);
        }
    }
}

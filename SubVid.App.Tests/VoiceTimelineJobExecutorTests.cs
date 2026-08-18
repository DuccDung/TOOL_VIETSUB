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

    private static byte[] CreateWave(int sampleRate, int durationMilliseconds)
    {
        var sampleCount = sampleRate * durationMilliseconds / 1_000;
        var dataSize = sampleCount * sizeof(short);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var index = 0; index < sampleCount; index++) writer.Write((short)0);
        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingFfmpegRunner : IFfmpegProgressRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public string? WorkingDirectory { get; private set; }

        public async Task RunAsync(
            string ffmpegPath,
            IReadOnlyList<string> arguments,
            double durationSeconds,
            Func<double, ValueTask> reportProgress,
            CancellationToken cancellationToken,
            string? workingDirectory = null)
        {
            Arguments = arguments.ToArray();
            WorkingDirectory = workingDirectory;
            var outputPath = Path.IsPathRooted(arguments[^1])
                ? arguments[^1]
                : Path.Combine(workingDirectory!, arguments[^1]);
            await File.WriteAllBytesAsync(outputPath, CreateWave(48_000, 100), cancellationToken);
            await reportProgress(100);
        }
    }
}

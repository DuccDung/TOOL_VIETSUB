using System.Runtime.CompilerServices;
using SubVid.App.Core;
using SubVid.App.Jobs;
using SubVid.App.LocalAi;

namespace SubVid.App.Tests;

public sealed class TranscriptionJobExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Execute_CreatesCheckpointedTranscriptAndPreservesLockedCue()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Whisper test");
        project.SourceVideo = CreateSourceVideo();
        var waveRelativePath = Path.Combine("audio", "source-16k-mono.wav");
        var wavePath = paths.GetProjectPath(project.ProjectId, waveRelativePath);
        await File.WriteAllBytesAsync(wavePath, new byte[128]);
        project.AudioTracks.Add(CreateSourceAudio(waveRelativePath));
        var locked = new SubtitleCue
        {
            StartMilliseconds = 3000,
            EndMilliseconds = 4000,
            OriginalText = "Tên riêng đã duyệt",
            OriginalLocked = true,
        };
        project.SubtitleTracks.Add(new SubtitleDocument
        {
            Source = "WHISPER_LOCAL",
            Cues =
            [
                new SubtitleCue { StartMilliseconds = 0, EndMilliseconds = 500, OriginalText = "cũ" },
                locked,
            ],
        });
        var job = CreateJob(attemptCount: 1);
        var updates = new List<JobProgressUpdate>();
        var recognizer = new FakeRecognizer(
        [
            new(0, 1200, "Hello world", "en", 0.9f),
            new(3100, 3800, "must not overwrite", "en", 0.8f),
            new(5000, 6500, "Second sentence", "en", 0.85f),
        ]);
        var executor = new TranscriptionJobExecutor(paths, workspace, project, recognizer);

        await executor.ExecuteAsync(
            job,
            update =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        var track = Assert.Single(project.SubtitleTracks);
        Assert.Equal("en", project.SourceLanguageCode);
        Assert.Equal(3, track.Cues.Count);
        Assert.Same(locked, track.Cues[1]);
        Assert.DoesNotContain(track.Cues, cue => cue.OriginalText == "cũ" || cue.OriginalText == "must not overwrite");
        Assert.Equal(100, updates[^1].JobProgressPercent);
        var output = job.Steps.Single(step => step.Code == "TRANSCRIBE").OutputRelativePath;
        Assert.NotNull(output);
        Assert.True(File.Exists(paths.GetProjectPath(project.ProjectId, output!)));
        Assert.False(File.Exists(paths.GetProjectPath(project.ProjectId, output!) + ".partial"));
    }

    [Fact]
    public async Task Execute_OnRetry_ContinuesAfterSavedCheckpoint()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Checkpoint test");
        project.SourceVideo = CreateSourceVideo();
        var waveRelativePath = Path.Combine("audio", "source-16k-mono.wav");
        await File.WriteAllBytesAsync(paths.GetProjectPath(project.ProjectId, waveRelativePath), new byte[128]);
        project.AudioTracks.Add(CreateSourceAudio(waveRelativePath));
        project.SubtitleTracks.Add(new SubtitleDocument
        {
            Source = "WHISPER_LOCAL",
            Cues = [new SubtitleCue { StartMilliseconds = 0, EndMilliseconds = 1200, OriginalText = "Saved" }],
        });
        var job = CreateJob(attemptCount: 2);
        var executor = new TranscriptionJobExecutor(
            paths,
            workspace,
            project,
            new FakeRecognizer(
            [
                new(0, 1200, "Duplicate", "en", 0.9f),
                new(1500, 2500, "Continued", "en", 0.9f),
            ]));

        await executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        var cues = Assert.Single(project.SubtitleTracks).Cues;
        Assert.Equal(["Saved", "Continued"], cues.Select(cue => cue.OriginalText));
    }

    [Fact]
    public async Task Execute_WithChineseSource_PassesLanguageHintToWhisper()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Chinese speech");
        project.SourceLanguageCode = "zh-CN";
        project.SourceVideo = CreateSourceVideo();
        var waveRelativePath = Path.Combine("audio", "source-16k-mono.wav");
        await File.WriteAllBytesAsync(paths.GetProjectPath(project.ProjectId, waveRelativePath), new byte[128]);
        project.AudioTracks.Add(CreateSourceAudio(waveRelativePath));
        var recognizer = new FakeRecognizer(
        [
            new(0, 1200, "你好", "zh", 0.9f),
        ]);
        var executor = new TranscriptionJobExecutor(paths, workspace, project, recognizer);

        await executor.ExecuteAsync(
            CreateJob(attemptCount: 1),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal("zh", recognizer.LastLanguageCode);
        Assert.Equal("zh", project.SourceLanguageCode);
        Assert.Equal("zh", Assert.Single(project.SubtitleTracks).LanguageCode);
    }

    [Fact]
    public async Task Execute_LongVideo_ProcessesIndependentChunksAndUsesGlobalTimestamps()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Long transcription");
        project.SourceVideo = CreateSourceVideo(durationSeconds: 35 * 60);
        var extractor = new FakeChunkExtractor(paths, project.ProjectId);
        var recognizer = new FakeRecognizer(
        [
            new(4_000, 5_000, "Chunk sentence", "en", 0.9f),
        ]);
        var executor = new TranscriptionJobExecutor(
            paths,
            workspace,
            project,
            recognizer,
            languageCode: "en",
            longFormChunkExtractor: extractor);

        await executor.ExecuteAsync(
            CreateJob(attemptCount: 1),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(4, extractor.ExtractedChunks.Count);
        var cues = Assert.Single(project.SubtitleTracks).Cues;
        Assert.Equal(4, cues.Count);
        Assert.Equal([4_000L, 601_500L, 1_201_500L, 1_801_500L], cues.Select(cue => cue.StartMilliseconds));
        Assert.Empty(Directory.EnumerateFiles(
            paths.GetProjectPath(project.ProjectId, "temp"),
            "fake-chunk-*.wav"));
    }

    [Fact]
    public void LongFormChunkPlanner_OwnsEveryTimelinePositionOnce()
    {
        var chunks = LongFormAudioChunkPlanner.Plan(25 * 60 * 1000);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(0, chunks[0].OwnershipStartMilliseconds);
        Assert.Equal(600_000, chunks[0].OwnershipEndMilliseconds);
        Assert.Equal(597_500, chunks[1].ExtractionStartMilliseconds);
        Assert.Equal(1_500_000, chunks[^1].OwnershipEndMilliseconds);
        Assert.Single(chunks, chunk => chunk.Owns(600_000, 600_001));
        Assert.Single(chunks, chunk => chunk.Owns(1_200_000, 1_200_001));
    }

    [Fact]
    public void LongFormChunkPlanner_FourHoursHasBoundedCompleteCoverage()
    {
        const long durationMilliseconds = 4 * 60 * 60 * 1000;
        var chunks = LongFormAudioChunkPlanner.Plan(durationMilliseconds);

        Assert.Equal(24, chunks.Count);
        Assert.Equal(0, chunks[0].ExtractionStartMilliseconds);
        Assert.Equal(durationMilliseconds, chunks[^1].ExtractionEndMilliseconds);
        Assert.All(chunks, chunk =>
            Assert.InRange(
                chunk.ExtractionDurationMilliseconds,
                1,
                LongFormAudioChunkPlanner.DefaultChunkDurationMilliseconds
                    + 2 * LongFormAudioChunkPlanner.DefaultOverlapMilliseconds));
        for (var index = 1; index < chunks.Count; index++)
        {
            Assert.Equal(
                chunks[index - 1].OwnershipEndMilliseconds,
                chunks[index].OwnershipStartMilliseconds);
        }
    }

    private static LocalJob CreateJob(int attemptCount) => new()
    {
        JobType = "TRANSCRIBE_LOCAL",
        AttemptCount = attemptCount,
        Steps =
        [
            new LocalJobStep { Code = "EXTRACT_AUDIO" },
            new LocalJobStep { Code = "TRANSCRIBE" },
        ],
    };

    private static LocalMediaReference CreateSourceVideo(double durationSeconds = 10) => new()
    {
        FileName = "source.mp4",
        SizeBytes = 100,
        Sha256 = new string('0', 64),
        Metadata = new MediaMetadata
        {
            DurationSeconds = durationSeconds,
            HasVideo = true,
            HasAudio = true,
        },
    };

    private static LocalMediaReference CreateSourceAudio(string relativePath) => new()
    {
        Role = "SOURCE_AUDIO",
        ImportMode = "GENERATED",
        WorkspaceRelativePath = relativePath,
        FileName = "source-16k-mono.wav",
        SizeBytes = 128,
        Metadata = new MediaMetadata { DurationSeconds = 10, HasAudio = true },
    };

    private sealed class FakeRecognizer(IReadOnlyList<SpeechRecognitionSegment> segments) : ILocalSpeechRecognizer
    {
        public string? LastLanguageCode { get; private set; }

        public async IAsyncEnumerable<SpeechRecognitionSegment> RecognizeAsync(
            string wavePath,
            string? languageCode,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastLanguageCode = languageCode;
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return segment;
            }
        }
    }

    private sealed class FakeChunkExtractor(AppPaths paths, Guid projectId) : ILongFormAudioChunkExtractor
    {
        public List<LongFormAudioChunk> ExtractedChunks { get; } = [];

        public async Task<string> ExtractAsync(
            LongFormAudioChunk chunk,
            Func<double, ValueTask> reportProgress,
            CancellationToken cancellationToken)
        {
            ExtractedChunks.Add(chunk);
            var path = paths.GetProjectPath(projectId, "temp", $"fake-chunk-{chunk.Index:D4}.wav");
            await File.WriteAllBytesAsync(path, new byte[128], cancellationToken);
            await reportProgress(100);
            return path;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

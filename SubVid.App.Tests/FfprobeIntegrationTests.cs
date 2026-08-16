using SubVid.App.Core;
using SubVid.App.Jobs;
using SubVid.App.Media;

namespace SubVid.App.Tests;

public sealed class FfprobeIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SUBVID_TESTS", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GeneratedVideo_IsProbedAndImportedWithRealFfmpegTools()
    {
        Directory.CreateDirectory(_root);
        var defaultPaths = new AppPaths();
        var ffmpegPath = Path.Combine(defaultPaths.ToolsDirectory, "ffmpeg", "ffmpeg.exe");
        Assert.True(File.Exists(ffmpegPath), "FFmpeg test prerequisite is missing.");
        var sourcePath = Path.Combine(_root, "video kiểm thử.mp4");
        var runner = new ExternalProcessRunner();
        var generated = await runner.RunAsync(
            ffmpegPath,
            [
                "-y",
                "-f", "lavfi",
                "-i", "color=c=0x123456:s=320x180:r=25",
                "-f", "lavfi",
                "-i", "sine=frequency=880:sample_rate=48000",
                "-t", "1.5",
                "-c:v", "libx264",
                "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                "-shortest",
                sourcePath,
            ],
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.Equal(0, generated.ExitCode);

        var inspector = new FfprobeMediaInspector(defaultPaths, runner);
        var metadata = await inspector.InspectAsync(sourcePath, CancellationToken.None);

        Assert.True(metadata.HasVideo);
        Assert.True(metadata.HasAudio);
        Assert.Equal(320, metadata.Width);
        Assert.Equal(180, metadata.Height);
        Assert.InRange(metadata.DurationSeconds, 1.4, 1.7);
        Assert.Equal("h264", metadata.VideoCodec);
        Assert.Equal("aac", metadata.AudioCodec);

        var testPaths = new AppPaths(Path.Combine(_root, "app-data"));
        var projects = new ProjectWorkspaceService(testPaths);
        var project = await projects.CreateAsync(Guid.NewGuid(), "FFmpeg integration");
        var importer = new MediaImportService(testPaths, projects, inspector);
        var imported = await importer.ImportAsync(
            project,
            sourcePath,
            MediaImportMode.Copy,
            maxVideoMinutes: 20);

        Assert.Equal(metadata.DurationSeconds, imported.Metadata.DurationSeconds, precision: 3);
        Assert.True(File.Exists(testPaths.GetProjectPath(project.ProjectId, imported.WorkspaceRelativePath!)));
    }

    [Fact]
    public async Task AudioExtractionJob_ProducesCheckpointAndCompletes()
    {
        Directory.CreateDirectory(_root);
        var defaultPaths = new AppPaths();
        var ffmpegPath = Path.Combine(defaultPaths.ToolsDirectory, "ffmpeg", "ffmpeg.exe");
        var sourcePath = Path.Combine(_root, "pipeline.mp4");
        var runner = new ExternalProcessRunner();
        var generated = await runner.RunAsync(
            ffmpegPath,
            [
                "-y", "-f", "lavfi", "-i", "color=c=black:s=320x180:r=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", sourcePath,
            ],
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.Equal(0, generated.ExitCode);

        var paths = new AppPaths(Path.Combine(_root, "job-app-data"));
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Audio extraction");
        var inspector = new FfprobeMediaInspector(defaultPaths, runner);
        var importer = new MediaImportService(paths, workspace, inspector);
        await importer.ImportAsync(project, sourcePath, MediaImportMode.Copy, 20);
        await using var jobs = new PersistentJobManager(workspace, paths);
        var job = await jobs.EnqueueAsync(project, "EXTRACT_AUDIO", ["EXTRACT_AUDIO"]);
        var executor = new AudioExtractionJobExecutor(paths, project, ffmpegPath);

        await jobs.StartAsync(project, job.JobId, executor);
        await jobs.WaitForCompletionAsync(job.JobId).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(LocalJobStatus.Completed, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        var audio = Assert.Single(project.AudioTracks, item => item.Role == "SOURCE_AUDIO");
        var outputPath = paths.GetProjectPath(project.ProjectId, audio.WorkspaceRelativePath!);
        Assert.True(File.Exists(outputPath));
        var audioMetadata = await inspector.InspectAsync(outputPath, CancellationToken.None);
        Assert.True(audioMetadata.HasAudio);
        Assert.False(audioMetadata.HasVideo);
        Assert.Equal(16000, audioMetadata.AudioSampleRate);
        Assert.Equal(1, audioMetadata.AudioChannels);
        Assert.False(Directory.EnumerateFiles(paths.GetProjectPath(project.ProjectId, "temp"), "*.partial.*").Any());
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}

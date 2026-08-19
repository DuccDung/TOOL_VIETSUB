using System.Diagnostics;
using System.Security.Cryptography;
using SubVid.App.Core;
using SubVid.App.Jobs;
using SubVid.App.LocalAi;
using SubVid.App.Media;

namespace SubVid.App.Tests;

public sealed class FullLocalPipelineIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_FULL_PIPELINE_TEST",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LocalPipeline_TranscribesTranslatesVoicesAndExportsWithoutChangingSource()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SUBVID_FFMPEG_PATH");
        var speechWave = Environment.GetEnvironmentVariable("SUBVID_TEST_SPEECH_WAV");
        if (string.IsNullOrWhiteSpace(ffmpeg)
            || string.IsNullOrWhiteSpace(speechWave)
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUBVID_FFPROBE_PATH"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUBVID_MODEL_ROOT"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUBVID_PYTHON_PATH")))
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "speech-source.mp4");
        await RunAsync(ffmpeg,
        [
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "color=c=0x16233d:s=640x360:r=25",
            "-i", speechWave,
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest", sourcePath,
        ]);
        var sourceBefore = await File.ReadAllBytesAsync(sourcePath);
        var paths = new AppPaths(Path.Combine(_root, "app"));
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Full local pipeline");
        var metadata = await new FfprobeMediaInspector(paths)
            .InspectAsync(sourcePath, CancellationToken.None);
        project.SourceVideo = new LocalMediaReference
        {
            OriginalPath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            ImportMode = "LINK",
            SizeBytes = sourceBefore.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(sourceBefore)).ToLowerInvariant(),
            SourceLastWriteAtUtc = File.GetLastWriteTimeUtc(sourcePath),
            Metadata = metadata,
        };
        await workspace.SaveAsync(project);

        using var models = new LocalModelManager(paths);
        var transcription = new LocalJob
        {
            JobType = "TRANSCRIBE_LOCAL",
            AttemptCount = 1,
            Steps =
            [
                new LocalJobStep { Code = "EXTRACT_AUDIO" },
                new LocalJobStep { Code = "TRANSCRIBE" },
            ],
        };
        await new TranscriptionJobExecutor(
            paths,
            workspace,
            project,
            new WhisperLocalSpeechRecognizer(models, threads: 4))
            .ExecuteAsync(transcription, _ => ValueTask.CompletedTask, CancellationToken.None);

        var translation = new LocalJob
        {
            JobType = "TRANSLATE_LOCAL",
            AttemptCount = 1,
            Steps = [new LocalJobStep { Code = "TRANSLATE" }],
        };
        await new TranslationJobExecutor(
            paths,
            workspace,
            project,
            new ArgosLocalTranslator(paths, models))
            .ExecuteAsync(translation, _ => ValueTask.CompletedTask, CancellationToken.None);

        var synthesis = new LocalJob
        {
            JobType = "SYNTHESIZE_VOICE_LOCAL",
            AttemptCount = 1,
            Steps = [new LocalJobStep { Code = "SYNTHESIZE_VOICE" }],
        };
        await new VoiceSynthesisJobExecutor(
            paths,
            workspace,
            project,
            new PiperLocalVoiceSynthesizer(paths, models))
            .ExecuteAsync(synthesis, _ => ValueTask.CompletedTask, CancellationToken.None);

        var destination = Path.Combine(_root, "finished.mp4");
        var export = new LocalJob
        {
            JobType = "EXPORT_VIDEO_LOCAL",
            AttemptCount = 1,
            Steps =
            [
                new LocalJobStep { Code = "SYNC_VOICE" },
                new LocalJobStep { Code = "EXPORT_VIDEO" },
            ],
            Parameters = new Dictionary<string, string>
            {
                [VideoExportJobExecutor.DestinationParameter] = destination,
            },
        };
        await new FullExportJobExecutor(
            new VoiceTimelineJobExecutor(paths, workspace, project, ffmpeg),
            new VideoExportJobExecutor(paths, workspace, project, ffmpeg))
            .ExecuteAsync(export, _ => ValueTask.CompletedTask, CancellationToken.None);

        var cues = Assert.Single(project.SubtitleTracks).Cues;
        Assert.NotEmpty(cues);
        Assert.All(cues, cue => Assert.False(string.IsNullOrWhiteSpace(cue.TranslatedText)));
        var voicedCueIds = project.AudioTracks
            .Where(item => item.Role is "VOICE_CUE" or "VOICE_PHRASE")
            .SelectMany(item => item.CueId is Guid cueId ? item.CueIds.Append(cueId) : item.CueIds)
            .ToHashSet();
        Assert.Equal(cues.Count, voicedCueIds.Count);
        var outputMetadata = await new FfprobeMediaInspector(paths)
            .InspectAsync(destination, CancellationToken.None);
        Assert.True(outputMetadata.HasVideo);
        Assert.True(outputMetadata.HasAudio);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(sourcePath));
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cannot start FFmpeg.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using OpenCvSharp;
using SubVid.App.Core;
using SubVid.App.Jobs;
using SubVid.App.LocalAi;

namespace SubVid.App.Tests;

public sealed class PaddleOcrIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_OCR_TEST",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Recognize_LocalPaddleRuntime_ReadsGeneratedFrame()
    {
        Directory.CreateDirectory(_root);
        var imagePath = Path.Combine(_root, "subtitle.png");
        using (var image = new Mat(new Size(1200, 240), MatType.CV_8UC3, Scalar.White))
        {
            Cv2.PutText(
                image,
                "HELLO SUBVID",
                new Point(90, 155),
                HersheyFonts.HersheyDuplex,
                2.2,
                Scalar.Black,
                5,
                LineTypes.AntiAlias);
            Assert.True(Cv2.ImWrite(imagePath, image));
        }

        using var recognizer = new PaddleLocalOcrRecognizer();
        var lines = await recognizer.RecognizeAsync(imagePath, CancellationToken.None);

        Assert.NotEmpty(lines);
        Assert.Contains("SUBVID", string.Join(' ', lines.Select(line => line.Text)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChineseV5_LocalModel_CanInitialize()
    {
        using var recognizer = new PaddleLocalOcrRecognizer("zh");

        Assert.NotNull(recognizer);
    }

    [Fact]
    public async Task OcrJob_WithRealVideo_CreatesTimedSubtitleTrack()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SUBVID_FFMPEG_PATH");
        if (string.IsNullOrWhiteSpace(ffmpeg)) return;
        Directory.CreateDirectory(_root);
        var imagePath = Path.Combine(_root, "video-subtitle.png");
        using (var image = new Mat(new Size(1280, 720), MatType.CV_8UC3, Scalar.Black))
        {
            Cv2.PutText(
                image,
                "HELLO SUBVID",
                new Point(280, 620),
                HersheyFonts.HersheyDuplex,
                2.2,
                Scalar.White,
                5,
                LineTypes.AntiAlias);
            Assert.True(Cv2.ImWrite(imagePath, image));
        }

        var sourcePath = Path.Combine(_root, "hard-subtitle.mp4");
        await RunAsync(ffmpeg,
        [
            "-y", "-v", "error", "-loop", "1", "-i", imagePath,
            "-t", "2", "-r", "25", "-c:v", "libx264", "-pix_fmt", "yuv420p", sourcePath,
        ]);
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var paths = new AppPaths(Path.Combine(_root, "app"));
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "OCR video");
        project.SourceLanguageCode = "en";
        project.Settings.OcrCropTopRatio = 0.6;
        project.Settings.OcrSampleIntervalMilliseconds = 500;
        project.SourceVideo = new LocalMediaReference
        {
            OriginalPath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            ImportMode = "LINK",
            SizeBytes = sourceBytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(),
            Metadata = new MediaMetadata { DurationSeconds = 2, HasVideo = true },
        };
        await workspace.SaveAsync(project);
        var job = new LocalJob
        {
            AttemptCount = 1,
            Steps =
            [
                new LocalJobStep { Code = "OCR_EXTRACT_FRAMES" },
                new LocalJobStep { Code = "OCR_RECOGNIZE" },
            ],
        };

        await new OcrJobExecutor(paths, workspace, project, ffmpeg)
            .ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        var cues = Assert.Single(project.SubtitleTracks).Cues;
        Assert.NotEmpty(cues);
        Assert.Contains("SUBVID", string.Join(' ', cues.Select(cue => cue.OriginalText)), StringComparison.OrdinalIgnoreCase);
        Assert.True(cues[0].EndMilliseconds > cues[0].StartMilliseconds);
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
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

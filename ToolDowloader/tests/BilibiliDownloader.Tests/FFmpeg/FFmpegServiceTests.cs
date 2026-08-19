using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.FFmpeg;
using BilibiliDownloader.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BilibiliDownloader.Tests.FFmpeg;

public sealed class FFmpegServiceTests
{
    [Fact]
    public async Task ValidateAsync_FfmpegNotFound_ReturnsInvalid()
    {
        var service = CreateService(new FakeRunner(), Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.exe"));

        var result = await service.ValidateAsync(Path.Combine(Path.GetTempPath(), "missing_ffmpeg.exe"), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Không thể chạy", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergeVideoAudioAsync_InvalidInput_Throws()
    {
        using var directory = new TemporaryDirectory();
        var executable = CreateFile(directory.Path, "ffmpeg.exe", [1]);
        var service = CreateService(new FakeRunner(), executable);

        var exception = await Assert.ThrowsAsync<AppException>(() => service.MergeVideoAudioAsync(
            Path.Combine(directory.Path, "missing-video"),
            Path.Combine(directory.Path, "missing-audio"),
            Path.Combine(directory.Path, "output.mp4"),
            CancellationToken.None));

        Assert.Equal("FFMPEG_ERROR", exception.PublicCode);
    }

    [Fact]
    public async Task MergeVideoAudioAsync_ProcessSucceeds_CreatesOutput()
    {
        using var directory = new TemporaryDirectory();
        var executable = CreateFile(directory.Path, "ffmpeg.exe", [1]);
        var video = CreateFile(directory.Path, "video.part", [1, 2]);
        var audio = CreateFile(directory.Path, "audio.part", [3, 4]);
        var output = Path.Combine(directory.Path, "output.mp4");
        var runner = new FakeRunner((_, arguments, _) =>
        {
            File.WriteAllBytes(arguments[^1], [1, 2, 3, 4]);
            return Task.FromResult(new FFmpegRunResult(0, "ffmpeg version test", string.Empty));
        });
        var service = CreateService(runner, executable);

        var result = await service.MergeVideoAudioAsync(video, audio, output, CancellationToken.None);

        Assert.Equal(output, result);
        Assert.True(File.Exists(output));
        Assert.Contains("-map", runner.LastArguments);
        Assert.Contains("copy", runner.LastArguments);
    }

    [Fact]
    public async Task MergeVideoAudioAsync_ProcessFails_ThrowsClearError()
    {
        using var directory = new TemporaryDirectory();
        var executable = CreateFile(directory.Path, "ffmpeg.exe", [1]);
        var video = CreateFile(directory.Path, "video.part", [1]);
        var audio = CreateFile(directory.Path, "audio.part", [2]);
        var runner = new FakeRunner((_, _, _) =>
            Task.FromResult(new FFmpegRunResult(1, string.Empty, "Invalid input data")));
        var service = CreateService(runner, executable);

        var exception = await Assert.ThrowsAsync<AppException>(() => service.MergeVideoAudioAsync(
            video,
            audio,
            Path.Combine(directory.Path, "output.mp4"),
            CancellationToken.None));

        Assert.Equal("FFMPEG_ERROR", exception.PublicCode);
        Assert.Contains("Invalid input data", exception.Message, StringComparison.Ordinal);
    }

    private static FFmpegService CreateService(IFFmpegProcessRunner runner, string ffmpegPath)
    {
        var discovery = new FakeDiscoveryService(runner);
        return new FFmpegService(
            runner,
            new FakeProvisioningService(ffmpegPath),
            discovery,
            new FakeSettingsService(ffmpegPath),
            NullLogger<FFmpegService>.Instance);
    }

    private static string CreateFile(string directory, string name, byte[] contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private sealed class FakeRunner(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<FFmpegRunResult>>? callback = null)
        : IFFmpegProcessRunner
    {
        public IReadOnlyList<string> LastArguments { get; private set; } = [];

        public Task<FFmpegRunResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            LastArguments = arguments;
            return callback?.Invoke(executablePath, arguments, cancellationToken) ??
                Task.FromResult(new FFmpegRunResult(0, "ffmpeg version test", string.Empty));
        }
    }

    private sealed class FakeSettingsService(string ffmpegPath) : ISettingsService
    {
        public event EventHandler<AppSettings>? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings { DownloadFolder = Path.GetTempPath(), FfmpegPath = ffmpegPath });

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeProvisioningService(string executablePath) : IFFmpegProvisioningService
    {
        public Task<FFmpegProvisioningResultDto?> FindAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<FFmpegProvisioningResultDto?>(File.Exists(executablePath)
                ? new FFmpegProvisioningResultDto
                {
                    ExecutablePath = executablePath,
                    Version = "test",
                    Source = FFmpegSource.Custom
                }
                : null);

        public async Task<FFmpegProvisioningResultDto> EnsureAvailableAsync(
            IProgress<FFmpegProvisioningProgressDto>? progress = null,
            CancellationToken cancellationToken = default) =>
            await FindAvailableAsync(cancellationToken) ?? throw new InvalidOperationException("FFmpeg missing.");
    }

    private sealed class FakeDiscoveryService(IFFmpegProcessRunner runner) : IFFmpegDiscoveryService
    {
        public Task<FFmpegProvisioningResultDto?> FindAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult<FFmpegProvisioningResultDto?>(null);

        public async Task<FFmpegProvisioningResultDto?> ValidateCandidateAsync(
            string executablePath,
            FFmpegSource source,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(executablePath))
            {
                return null;
            }

            var run = await runner.RunAsync(executablePath, ["-version"], cancellationToken);
            return run.ExitCode == 0
                ? new FFmpegProvisioningResultDto
                {
                    ExecutablePath = executablePath,
                    Version = "test",
                    Source = source
                }
                : null;
        }
    }
}

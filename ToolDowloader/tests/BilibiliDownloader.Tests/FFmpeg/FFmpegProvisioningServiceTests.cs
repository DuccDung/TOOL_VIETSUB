using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Configuration;
using BilibiliDownloader.Infrastructure.FFmpeg;
using BilibiliDownloader.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Tests.FFmpeg;

public sealed class FFmpegProvisioningServiceTests
{
    [Fact]
    public async Task EnsureAvailableAsync_ExistingFfmpeg_DoesNotDownload()
    {
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        await File.WriteAllBytesAsync(executable, [1]);
        var existing = Result(executable, FFmpegSource.System);
        var discovery = new FakeDiscoveryService(existing);
        var downloader = new FakeDownloader();
        using var service = CreateService(directory.Path, discovery, downloader);

        var result = await service.EnsureAvailableAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(existing, result);
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task EnsureAvailableAsync_MissingFfmpeg_InstallsAndPersistsManagedPath()
    {
        using var directory = new TemporaryDirectory();
        var settings = new TestSettingsService(new AppSettings { DownloadFolder = directory.Path });
        var discovery = new FakeDiscoveryService(null);
        var downloader = new FakeDownloader();
        var verifier = new FakeVerifier();
        using var service = CreateService(
            directory.Path,
            discovery,
            downloader,
            settings,
            verifier: verifier);

        var result = await service.EnsureAvailableAsync(cancellationToken: CancellationToken.None);
        var savedSettings = await settings.GetAsync();

        Assert.True(result.WasDownloaded);
        Assert.Equal(FFmpegSource.Managed, result.Source);
        Assert.True(File.Exists(result.ExecutablePath));
        Assert.True(File.Exists(result.ProbePath));
        Assert.Equal(result.ExecutablePath, savedSettings.FfmpegPath);
        Assert.Equal(1, downloader.Calls);
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ConcurrentCallers_DownloadOnlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var discovery = new FakeDiscoveryService(null);
        var downloader = new FakeDownloader(TimeSpan.FromMilliseconds(100));
        using var service = CreateService(directory.Path, discovery, downloader);

        var first = service.EnsureAvailableAsync(cancellationToken: CancellationToken.None);
        var second = service.EnsureAvailableAsync(cancellationToken: CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, downloader.Calls);
        Assert.Equal(results[0].ExecutablePath, results[1].ExecutablePath);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ReportsOrderedStates()
    {
        using var directory = new TemporaryDirectory();
        using var service = CreateService(
            directory.Path,
            new FakeDiscoveryService(null),
            new FakeDownloader());
        var states = new List<FFmpegProvisioningState>();

        await service.EnsureAvailableAsync(
            new InlineProgress<FFmpegProvisioningProgressDto>(value => states.Add(value.State)),
            CancellationToken.None);

        Assert.Contains(FFmpegProvisioningState.Checking, states);
        Assert.Contains(FFmpegProvisioningState.Downloading, states);
        Assert.Contains(FFmpegProvisioningState.Verifying, states);
        Assert.Contains(FFmpegProvisioningState.Extracting, states);
        Assert.Contains(FFmpegProvisioningState.Validating, states);
        Assert.Equal(FFmpegProvisioningState.Ready, states[^1]);
    }

    [Fact]
    public async Task EnsureAvailableAsync_PostActivationValidationFails_RestoresPreviousVersion()
    {
        using var directory = new TemporaryDirectory();
        var files = new TestFileService(directory.Path);
        var existingExecutable = Path.Combine(
            files.ToolsDirectory,
            "ffmpeg",
            "9.0.1",
            "bin",
            "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(existingExecutable)!);
        await File.WriteAllBytesAsync(existingExecutable, [9]);
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(existingExecutable)!, "ffprobe.exe"), [9]);
        using var service = CreateService(
            directory.Path,
            new StagedOnlyDiscoveryService(),
            new FakeDownloader());

        var exception = await Assert.ThrowsAsync<BilibiliDownloader.Application.Errors.AppException>(() =>
            service.EnsureAvailableAsync(cancellationToken: CancellationToken.None));

        Assert.Equal("FFMPEG_VALIDATION_ERROR", exception.PublicCode);
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(existingExecutable));
    }

    private static FFmpegProvisioningService CreateService(
        string root,
        IFFmpegDiscoveryService discovery,
        IFFmpegPackageDownloader downloader,
        TestSettingsService? settings = null,
        IFFmpegPackageVerifier? verifier = null)
    {
        var files = new TestFileService(root);
        settings ??= new TestSettingsService(new AppSettings { DownloadFolder = files.DefaultDownloadDirectory });
        var options = Options.Create(new FFmpegOptions
        {
            Version = "9.0.1",
            DownloadUrl = "https://packages.test/ffmpeg.zip",
            Sha256 = new string('0', 64),
            ArchiveRootDirectoryName = "ffmpeg-package",
            FfmpegRelativePath = "bin/ffmpeg.exe",
            FfprobeRelativePath = "bin/ffprobe.exe",
            AllowedHosts = ["packages.test"],
            MaximumDownloadBytes = 1024,
            MaximumExtractedBytes = 4096,
            DownloadTimeoutMinutes = 1,
            MaximumRetries = 0
        });
        return new FFmpegProvisioningService(
            discovery,
            downloader,
            verifier ?? new FakeVerifier(),
            new FakeExtractor(),
            settings,
            files,
            options,
            NullLogger<FFmpegProvisioningService>.Instance);
    }

    private static FFmpegProvisioningResultDto Result(string path, FFmpegSource source) => new()
    {
        ExecutablePath = path,
        Version = "9.0.1",
        Source = source
    };

    private sealed class FakeDiscoveryService(FFmpegProvisioningResultDto? available) : IFFmpegDiscoveryService
    {
        private FFmpegProvisioningResultDto? _available = available;

        public Task<FFmpegProvisioningResultDto?> FindAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_available is not null && File.Exists(_available.ExecutablePath) ? _available : null);

        public Task<FFmpegProvisioningResultDto?> ValidateCandidateAsync(
            string executablePath,
            FFmpegSource source,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(executablePath))
            {
                return Task.FromResult<FFmpegProvisioningResultDto?>(null);
            }

            _available = new FFmpegProvisioningResultDto
            {
                ExecutablePath = executablePath,
                ProbePath = Path.Combine(Path.GetDirectoryName(executablePath)!, "ffprobe.exe"),
                Version = "9.0.1",
                Source = source
            };
            return Task.FromResult<FFmpegProvisioningResultDto?>(_available);
        }
    }

    private sealed class StagedOnlyDiscoveryService : IFFmpegDiscoveryService
    {
        public Task<FFmpegProvisioningResultDto?> FindAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult<FFmpegProvisioningResultDto?>(null);

        public Task<FFmpegProvisioningResultDto?> ValidateCandidateAsync(
            string executablePath,
            FFmpegSource source,
            CancellationToken cancellationToken) => Task.FromResult<FFmpegProvisioningResultDto?>(
                executablePath.Contains("ffmpeg-install-", StringComparison.OrdinalIgnoreCase)
                    ? new FFmpegProvisioningResultDto
                    {
                        ExecutablePath = executablePath,
                        Version = "9.0.1",
                        Source = source
                    }
                    : null);
    }

    private sealed class FakeDownloader(TimeSpan? delay = null) : IFFmpegPackageDownloader
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task DownloadAsync(
            string url,
            string destinationPath,
            IProgress<FFmpegPackageDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (delay is not null)
            {
                await Task.Delay(delay.Value, cancellationToken);
            }

            await File.WriteAllBytesAsync(destinationPath, [1, 2, 3], cancellationToken);
            progress?.Report(new FFmpegPackageDownloadProgress(3, 3, 100));
        }
    }

    private sealed class FakeVerifier : IFFmpegPackageVerifier
    {
        public int Calls { get; private set; }

        public Task VerifySha256Async(
            string filePath,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExtractor : ISecureArchiveExtractor
    {
        public async Task ExtractAsync(
            string archivePath,
            string destinationDirectory,
            long maximumExtractedBytes,
            CancellationToken cancellationToken)
        {
            var bin = Path.Combine(destinationDirectory, "ffmpeg-package", "bin");
            Directory.CreateDirectory(bin);
            await File.WriteAllBytesAsync(Path.Combine(bin, "ffmpeg.exe"), [1], cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(bin, "ffprobe.exe"), [1], cancellationToken);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}

using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Configuration;
using BilibiliDownloader.Infrastructure.FFmpeg;
using BilibiliDownloader.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Tests.FFmpeg;

public sealed class FFmpegDiscoveryServiceTests
{
    [Fact]
    public async Task FindAvailableAsync_ValidCustomPath_IsPreferred()
    {
        using var directory = new TemporaryDirectory();
        var executable = CreateExecutable(directory.Path);
        var service = CreateService(directory.Path, executable);

        var result = await service.FindAvailableAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FFmpegSource.Custom, result.Source);
        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
    }

    [Fact]
    public async Task FindAvailableAsync_InvalidCustomPath_FallsBackToManaged()
    {
        using var directory = new TemporaryDirectory();
        var fileService = new TestFileService(directory.Path);
        var managed = Path.Combine(fileService.ToolsDirectory, "ffmpeg", "9.0.1", "bin", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
        await File.WriteAllBytesAsync(managed, [1]);
        var settings = new TestSettingsService(new AppSettings
        {
            DownloadFolder = directory.Path,
            FfmpegPath = Path.Combine(directory.Path, "missing.exe")
        });
        var service = new FFmpegDiscoveryService(
            settings,
            fileService,
            new TestFFmpegRunner(),
            new TestFFmpegEnvironment(directory.Path),
            Options.Create(new FFmpegOptions()),
            NullLogger<FFmpegDiscoveryService>.Instance);

        var result = await service.FindAvailableAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FFmpegSource.Managed, result.Source);
        Assert.Equal(Path.GetFullPath(managed), result.ExecutablePath);
    }

    [Fact]
    public async Task FindAvailableAsync_BundledExecutable_ReturnsBundledSource()
    {
        using var directory = new TemporaryDirectory();
        var bundledDirectory = Path.Combine(directory.Path, "Tools", "ffmpeg");
        var executable = CreateExecutable(bundledDirectory);
        var files = new TestFileService(Path.Combine(directory.Path, "app-data"));
        var service = new FFmpegDiscoveryService(
            new TestSettingsService(new AppSettings { DownloadFolder = directory.Path }),
            files,
            new TestFFmpegRunner(),
            new TestFFmpegEnvironment(directory.Path),
            Options.Create(new FFmpegOptions()),
            NullLogger<FFmpegDiscoveryService>.Instance);

        var result = await service.FindAvailableAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FFmpegSource.Bundled, result.Source);
        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
    }

    [Fact]
    public async Task FindAvailableAsync_PathExecutable_ReturnsSystemSource()
    {
        using var directory = new TemporaryDirectory();
        var systemDirectory = Path.Combine(directory.Path, "system");
        var executable = CreateExecutable(systemDirectory);
        var fileService = new TestFileService(Path.Combine(directory.Path, "app"));
        var service = new FFmpegDiscoveryService(
            new TestSettingsService(new AppSettings { DownloadFolder = directory.Path }),
            fileService,
            new TestFFmpegRunner(),
            new TestFFmpegEnvironment(directory.Path, [systemDirectory]),
            Options.Create(new FFmpegOptions()),
            NullLogger<FFmpegDiscoveryService>.Instance);

        var result = await service.FindAvailableAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FFmpegSource.System, result.Source);
        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
    }

    private static FFmpegDiscoveryService CreateService(string root, string configuredPath)
    {
        var files = new TestFileService(Path.Combine(root, "app"));
        return new FFmpegDiscoveryService(
            new TestSettingsService(new AppSettings
            {
                DownloadFolder = root,
                FfmpegPath = configuredPath
            }),
            files,
            new TestFFmpegRunner(),
            new TestFFmpegEnvironment(root),
            Options.Create(new FFmpegOptions()),
            NullLogger<FFmpegDiscoveryService>.Instance);
    }

    private static string CreateExecutable(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ffmpeg.exe");
        File.WriteAllBytes(path, [1]);
        return path;
    }
}

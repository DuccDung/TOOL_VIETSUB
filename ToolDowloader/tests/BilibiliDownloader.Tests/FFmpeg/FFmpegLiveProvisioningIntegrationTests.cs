using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Infrastructure.Configuration;
using BilibiliDownloader.Infrastructure.FFmpeg;
using BilibiliDownloader.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Tests.FFmpeg;

public sealed class FFmpegLiveProvisioningIntegrationTests
{
    [Fact]
    [Trait("Category", "LiveIntegration")]
    public async Task EnsureAvailableAsync_PinnedPackage_InstallsRunnableFfmpeg()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_FFMPEG_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var files = new TestFileService(directory.Path);
        var settings = new TestSettingsService(new AppSettings
        {
            DownloadFolder = files.DefaultDownloadDirectory
        });
        var configured = new FFmpegOptions();
        var options = Options.Create(configured);
        using var httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BilibiliDownloader.Tests/1.0");
        var runner = new FFmpegProcessRunner();
        var discovery = new FFmpegDiscoveryService(
            settings,
            files,
            runner,
            new TestFFmpegEnvironment(directory.Path),
            options,
            NullLogger<FFmpegDiscoveryService>.Instance);
        using var service = new FFmpegProvisioningService(
            discovery,
            new FFmpegPackageDownloader(
                httpClient,
                options,
                NullLogger<FFmpegPackageDownloader>.Instance),
            new FFmpegPackageVerifier(),
            new SecureZipExtractor(),
            settings,
            files,
            options,
            NullLogger<FFmpegProvisioningService>.Instance);

        var result = await service.EnsureAvailableAsync(cancellationToken: CancellationToken.None);

        Assert.True(result.WasDownloaded);
        Assert.True(File.Exists(result.ExecutablePath));
        Assert.True(File.Exists(result.ProbePath));
        Assert.StartsWith(files.ToolsDirectory, result.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }
}

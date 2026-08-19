using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure;
using BilibiliDownloader.Infrastructure.Bilibili;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BilibiliDownloader.Tests.Bilibili;

public sealed class BilibiliLiveIntegrationTests
{
    [Fact]
    [Trait("Category", "LiveIntegration")]
    public async Task AnalyzeAsync_PublicVideo_ReturnsMetadataAndStreams()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_BILIBILI_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var values = new Dictionary<string, string?>
        {
            ["Bilibili:ApiBaseUrl"] = "https://api.bilibili.com/",
            ["Bilibili:RequestTimeoutSeconds"] = "30",
            ["Bilibili:MaximumResponseBytes"] = "4194304"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBilibiliDownloaderInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IBilibiliService>();
        var uriValidator = provider.GetRequiredService<IRemoteUriValidator>();

        var video = await service.AnalyzeAsync(
            "https://www.bilibili.com/video/BV1BLBeB9E9c/",
            CancellationToken.None);

        Assert.Equal("BV1BLBeB9E9c", video.Id);
        Assert.NotEmpty(video.Title);
        Assert.NotEmpty(video.Streams);
        Assert.All(video.Streams, stream => Assert.StartsWith("https://", stream.VideoUrl, StringComparison.Ordinal));
        var mediaUri = await uriValidator.ValidateMediaAsync(video.Streams[0].VideoUrl, CancellationToken.None);
        Assert.False(mediaUri.IsLoopback);
    }

    [Fact]
    [Trait("Category", "LiveDownload")]
    public async Task DownloadAsync_PublicVideo_CreatesPlayableMp4()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_BILIBILI_DOWNLOAD_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var videoUrl = GetRequiredEnvironmentVariable("BILIBILI_TEST_VIDEO_URL");
        var ffmpegPath = Path.GetFullPath(GetRequiredEnvironmentVariable("BILIBILI_TEST_FFMPEG_PATH"));
        var outputDirectory = Path.GetFullPath(GetRequiredEnvironmentVariable("BILIBILI_TEST_OUTPUT_DIRECTORY"));
        Assert.True(File.Exists(ffmpegPath), $"FFmpeg was not found: {ffmpegPath}");

        var values = new Dictionary<string, string?>
        {
            ["Bilibili:ApiBaseUrl"] = "https://api.bilibili.com/",
            ["Bilibili:RequestTimeoutSeconds"] = "30",
            ["Bilibili:MaximumResponseBytes"] = "4194304",
            ["Download:BufferSize"] = "131072",
            ["Download:ProgressIntervalMilliseconds"] = "200"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBilibiliDownloaderInfrastructure(configuration);
        services.AddSingleton<ISettingsService>(new FixedSettingsService(ffmpegPath, outputDirectory));

        await using var provider = services.BuildServiceProvider();
        var bilibiliService = provider.GetRequiredService<IBilibiliService>();
        var downloadService = provider.GetRequiredService<IDownloadService>();
        var qualitySelection = provider.GetRequiredService<IQualitySelectionService>();
        var fileService = provider.GetRequiredService<IFileService>();

        var video = await bilibiliService.AnalyzeAsync(videoUrl, CancellationToken.None);
        var stream = qualitySelection.SelectBest(video.Streams, VideoQuality.BestAvailable);
        var outputPath = fileService.CreateUniqueOutputPath(outputDirectory, video.Title, "mp4");
        var progress = new ProgressRecorder();
        var request = new DownloadRequestDto
        {
            JobId = Guid.NewGuid(),
            VideoId = video.Id,
            SourceUrl = video.SourceUrl,
            Title = video.Title,
            Author = video.Author,
            ThumbnailUrl = video.ThumbnailUrl,
            Stream = stream,
            OutputDirectory = outputDirectory,
            OutputPath = outputPath
        };

        await downloadService.DownloadAsync(request, progress, CancellationToken.None);

        var output = new FileInfo(outputPath);
        Assert.True(output.Exists, $"Output file was not created: {outputPath}");
        Assert.True(output.Length > 0, "Output file is empty.");
        Assert.Equal(DownloadStage.Completed, progress.LastStage);
        Assert.Equal(100, progress.LastPercentage);
    }

    private static string GetRequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is required.");

    private sealed class FixedSettingsService(string ffmpegPath, string outputDirectory) : ISettingsService
    {
        private readonly AppSettings _settings = new()
        {
            DownloadFolder = outputDirectory,
            FfmpegPath = ffmpegPath,
            DeleteTemporaryFiles = true,
            MaxFileSizeBytes = 2L * 1024 * 1024 * 1024,
            MaxRetryCount = 3,
            NetworkTimeoutSeconds = 120,
            FfmpegTimeoutMinutes = 10
        };

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_settings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SettingsChanged?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }

    private sealed class ProgressRecorder : IProgress<DownloadProgressDto>
    {
        public DownloadStage LastStage { get; private set; }
        public double LastPercentage { get; private set; }

        public void Report(DownloadProgressDto value)
        {
            LastStage = value.Stage;
            LastPercentage = value.Percentage;
        }
    }
}

using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Application.Services;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Configuration;
using BilibiliDownloader.Infrastructure.Download;
using BilibiliDownloader.Infrastructure.Storage;
using BilibiliDownloader.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Tests.Download;

public sealed class DownloadManagerTests
{
    [Fact]
    public async Task Queue_RespectsMaximumConcurrentDownloads()
    {
        using var directory = new TemporaryDirectory();
        var downloader = new ControlledDownloadService(TimeSpan.FromMilliseconds(120));
        using var manager = CreateManager(directory.Path, downloader, concurrency: 2);
        await manager.StartAsync(CancellationToken.None);
        try
        {
            await manager.EnqueueAsync(CreateRequest(directory.Path, "Video 1"));
            await manager.EnqueueAsync(CreateRequest(directory.Path, "Video 2"));
            await manager.EnqueueAsync(CreateRequest(directory.Path, "Video 3"));

            await WaitUntilAsync(
                () => manager.GetJobs().Count(job => job.Status == DownloadStatus.Completed) == 3,
                TimeSpan.FromSeconds(5));

            Assert.Equal(2, downloader.MaximumActive);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cancel_OnlyCancelsSelectedJob()
    {
        using var directory = new TemporaryDirectory();
        var downloader = new ControlledDownloadService(TimeSpan.FromMilliseconds(600));
        using var manager = CreateManager(directory.Path, downloader, concurrency: 2);
        await manager.StartAsync(CancellationToken.None);
        try
        {
            var first = await manager.EnqueueAsync(CreateRequest(directory.Path, "Cancelled"));
            var second = await manager.EnqueueAsync(CreateRequest(directory.Path, "Completed"));
            await WaitUntilAsync(
                () => manager.GetJobs().Any(job => job.Id == first && job.Status == DownloadStatus.Downloading),
                TimeSpan.FromSeconds(3));

            Assert.True(manager.Cancel(first));
            await WaitUntilAsync(
                () => manager.GetJobs().Any(job => job.Id == second && job.Status == DownloadStatus.Completed),
                TimeSpan.FromSeconds(5));

            Assert.Equal(DownloadStatus.Cancelled, manager.GetJobs().Single(job => job.Id == first).Status);
            Assert.Equal(DownloadStatus.Completed, manager.GetJobs().Single(job => job.Id == second).Status);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    private static DownloadManager CreateManager(
        string directory,
        IDownloadService downloadService,
        int concurrency)
    {
        var settings = new FakeSettingsService(directory, concurrency);
        return new DownloadManager(
            new FakeBilibiliService(),
            downloadService,
            new QualitySelectionService(),
            new FileStorageService(NullLogger<FileStorageService>.Instance),
            settings,
            new FakeHistoryService(),
            Options.Create(new DownloadOptions { QueueCapacity = 10 }),
            NullLogger<DownloadManager>.Instance);
    }

    private static DownloadRequestDto CreateRequest(string directory, string title) => new()
    {
        VideoId = "BV1BLBeB9E9c",
        SourceUrl = "https://www.bilibili.com/video/BV1BLBeB9E9c/",
        Title = title,
        Author = "Author",
        ThumbnailUrl = "https://i0.hdslb.com/image.jpg",
        OutputDirectory = directory,
        Format = "mp4",
        Stream = FakeBilibiliService.Stream
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(20, cancellation.Token);
        }
    }

    private sealed class ControlledDownloadService(TimeSpan duration) : IDownloadService
    {
        private int _active;
        private int _maximumActive;

        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public async Task DownloadAsync(
            DownloadRequestDto request,
            IProgress<DownloadProgressDto> progress,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                progress.Report(new DownloadProgressDto
                {
                    JobId = request.JobId,
                    Stage = DownloadStage.DownloadingVideo,
                    Percentage = 25
                });
                await Task.Delay(duration, cancellationToken);
                await File.WriteAllBytesAsync(request.OutputPath!, [1, 2, 3], cancellationToken);
                progress.Report(new DownloadProgressDto
                {
                    JobId = request.JobId,
                    Stage = DownloadStage.Completed,
                    Percentage = 100
                });
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActive);
                if (current >= value || Interlocked.CompareExchange(ref _maximumActive, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class FakeBilibiliService : IBilibiliService
    {
        public static BilibiliStreamDto Stream { get; } = new()
        {
            Id = "80:avc",
            QualityId = 80,
            Width = 1920,
            Height = 1080,
            Quality = "1080P",
            VideoUrl = "https://media.example/video",
            AudioUrl = "https://media.example/audio"
        };

        public Task<BilibiliVideoDto> AnalyzeAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(new BilibiliVideoDto
            {
                Id = "BV1BLBeB9E9c",
                SourceUrl = url,
                Title = "Video",
                Author = "Author",
                ThumbnailUrl = string.Empty,
                Duration = TimeSpan.FromMinutes(1),
                Streams = [Stream]
            });
    }

    private sealed class FakeSettingsService(string directory, int concurrency) : ISettingsService
    {
        public event EventHandler<AppSettings>? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings
            {
                DownloadFolder = directory,
                MaximumConcurrentDownloads = concurrency
            });

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeHistoryService : IHistoryService
    {
        public Task AddAsync(DownloadHistory history, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateStatusAsync(
            Guid id,
            DownloadStatus status,
            string? filePath = null,
            long? fileSize = null,
            string? errorCode = null,
            string? errorMessage = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DownloadHistory>> GetRecentAsync(
            int maximumCount = 500,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DownloadHistory>>([]);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkRunningAsInterruptedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

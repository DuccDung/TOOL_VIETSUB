using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Database;
using BilibiliDownloader.Infrastructure.Storage;
using BilibiliDownloader.Tests.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BilibiliDownloader.Tests.Database;

public sealed class PersistenceTests
{
    [Fact]
    public async Task SettingsService_SaveAndReload_PersistsValues()
    {
        using var directory = new TemporaryDirectory();
        var factory = CreateFactory(directory.Path);
        await EnsureCreatedAsync(factory);
        using var settings = new SettingsService(
            factory,
            new FileStorageService(NullLogger<FileStorageService>.Instance));
        var downloadFolder = Path.Combine(directory.Path, "downloads");

        await settings.SaveAsync(new AppSettings
        {
            DownloadFolder = downloadFolder,
            MaximumConcurrentDownloads = 4,
            DefaultQuality = VideoQuality.P1080,
            MaxFileSizeBytes = 5L * 1024 * 1024 * 1024,
            NetworkTimeoutSeconds = 60,
            FfmpegTimeoutMinutes = 10
        });
        var loaded = await settings.GetAsync();

        Assert.Equal(4, loaded.MaximumConcurrentDownloads);
        Assert.Equal(VideoQuality.P1080, loaded.DefaultQuality);
        Assert.Equal(Path.GetFullPath(downloadFolder), loaded.DownloadFolder);
    }

    [Fact]
    public async Task HistoryService_AddUpdateDelete_TracksLifecycle()
    {
        using var directory = new TemporaryDirectory();
        var factory = CreateFactory(directory.Path);
        await EnsureCreatedAsync(factory);
        var service = new HistoryService(factory);
        var id = Guid.NewGuid();
        await service.AddAsync(new DownloadHistory
        {
            Id = id,
            VideoId = "BV1BLBeB9E9c",
            SourceUrl = "https://www.bilibili.com/video/BV1BLBeB9E9c/",
            Title = "Video",
            Quality = "1080P",
            Format = "MP4",
            Status = DownloadStatus.Queued,
            CreatedAtUtc = DateTime.UtcNow
        });

        await service.UpdateStatusAsync(id, DownloadStatus.Completed, "C:\\Video.mp4", 1234);
        var item = Assert.Single(await service.GetRecentAsync());
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(1234, item.FileSize);
        Assert.NotNull(item.CompletedAtUtc);

        await service.DeleteAsync(id);
        Assert.Empty(await service.GetRecentAsync());
    }

    private static TestDbContextFactory CreateFactory(string directory)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "test.db")};Pooling=False")
            .Options;
        return new TestDbContextFactory(options);
    }

    private static async Task EnsureCreatedAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

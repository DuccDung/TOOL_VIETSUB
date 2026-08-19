using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Download;
using BilibiliDownloader.Tests.FFmpeg;
using BilibiliDownloader.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BilibiliDownloader.Tests.Download;

public sealed class DownloadServicePreflightTests
{
    [Fact]
    public async Task DownloadAsync_FfmpegUnavailable_DoesNotRequestMedia()
    {
        using var directory = new TemporaryDirectory();
        var downloadClient = new RecordingDownloadClient();
        var settings = new TestSettingsService(new AppSettings
        {
            DownloadFolder = directory.Path,
            DeleteTemporaryFiles = true
        });
        var service = new DownloadService(
            downloadClient,
            new NoOpFFmpegService(),
            new FailingProvisioningService(),
            new TestFileService(directory.Path),
            settings,
            NullLogger<DownloadService>.Instance);
        var request = CreateRequest(directory.Path);

        var exception = await Assert.ThrowsAsync<AppException>(() => service.DownloadAsync(
            request,
            new InlineProgress<DownloadProgressDto>(_ => { }),
            CancellationToken.None));

        Assert.Equal("FFMPEG_SOURCE_UNAVAILABLE", exception.PublicCode);
        Assert.Equal(0, downloadClient.Calls);
        Assert.False(File.Exists(request.OutputPath));
    }

    private static DownloadRequestDto CreateRequest(string directory) => new()
    {
        JobId = Guid.NewGuid(),
        VideoId = "BV1BLBeB9E9c",
        SourceUrl = "https://www.bilibili.com/video/BV1BLBeB9E9c/",
        Title = "Video",
        Author = "Author",
        ThumbnailUrl = string.Empty,
        OutputDirectory = directory,
        OutputPath = Path.Combine(directory, "output.mp4"),
        Stream = new BilibiliStreamDto
        {
            Id = "480:avc",
            QualityId = 32,
            Width = 852,
            Height = 480,
            Quality = "480P",
            VideoUrl = "https://media.test/video",
            AudioUrl = "https://media.test/audio"
        }
    };

    private sealed class RecordingDownloadClient : IHttpDownloadClient
    {
        public int Calls { get; private set; }

        public Task<long> DownloadFileAsync(
            string url,
            string destinationPath,
            long maximumFileSize,
            int maximumRetries,
            TimeSpan inactivityTimeout,
            IProgress<TransferProgress> progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(0L);
        }
    }

    private sealed class FailingProvisioningService : IFFmpegProvisioningService
    {
        public Task<FFmpegProvisioningResultDto?> FindAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<FFmpegProvisioningResultDto?>(null);

        public Task<FFmpegProvisioningResultDto> EnsureAvailableAsync(
            IProgress<FFmpegProvisioningProgressDto>? progress = null,
            CancellationToken cancellationToken = default) => throw new AppException(
                AppErrorCode.FfmpegSourceUnavailable,
                "FFmpeg source unavailable.");
    }

    private sealed class NoOpFFmpegService : IFFmpegService
    {
        public Task<string> MergeVideoAudioAsync(
            string videoPath,
            string audioPath,
            string outputPath,
            CancellationToken cancellationToken) => Task.FromResult(outputPath);

        public Task<(bool IsValid, string Message)> ValidateAsync(
            string? configuredPath,
            CancellationToken cancellationToken) => Task.FromResult((true, "OK"));
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}

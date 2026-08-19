using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BilibiliDownloader.Infrastructure.Download;

public sealed class DownloadService(
    IHttpDownloadClient downloadClient,
    IFFmpegService ffmpegService,
    IFFmpegProvisioningService ffmpegProvisioningService,
    IFileService fileService,
    ISettingsService settingsService,
    ILogger<DownloadService> logger) : IDownloadService
{
    public async Task DownloadAsync(
        DownloadRequestDto request,
        IProgress<DownloadProgressDto> progress,
        CancellationToken cancellationToken)
    {
        if (request.JobId == Guid.Empty || string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new AppException(AppErrorCode.DownloadError, "Download request chưa có output path hợp lệ.");
        }

        var settings = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.Stream.AudioUrl))
        {
            progress.Report(new DownloadProgressDto
            {
                JobId = request.JobId,
                Stage = DownloadStage.Resolving,
                Percentage = 0,
                Message = "Đang chuẩn bị FFmpeg..."
            });
            await ffmpegProvisioningService
                .EnsureAvailableAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        EnsureDiskSpace(request.OutputDirectory, request.Stream.FileSize);
        var jobDirectory = fileService.CreateJobTempDirectory(request.JobId);
        var videoPath = Path.Combine(jobDirectory, "video.part");
        var audioPath = Path.Combine(jobDirectory, "audio.part");
        var mergedPath = Path.Combine(jobDirectory, "merged.mp4");
        long videoBytes = 0;
        long audioBytes = 0;
        var lastLoggedProgressBucket = -1;

        try
        {
            logger.LogInformation("Download started for job {JobId} video {VideoId}", request.JobId, request.VideoId);
            var videoProgress = new InlineProgress<TransferProgress>(value =>
            {
                videoBytes = value.DownloadedBytes;
                ReportTransfer(progress, request.JobId, DownloadStage.DownloadingVideo, value, 0, request.Stream.AudioUrl is null ? 95 : 70);
                LogProgress(value, 0, request.Stream.AudioUrl is null ? 95 : 70);
            });
            await downloadClient.DownloadFileAsync(
                request.Stream.VideoUrl,
                videoPath,
                settings.MaxFileSizeBytes,
                settings.MaxRetryCount,
                TimeSpan.FromSeconds(settings.NetworkTimeoutSeconds),
                videoProgress,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.Stream.AudioUrl))
            {
                var audioProgress = new InlineProgress<TransferProgress>(value =>
                {
                    audioBytes = value.DownloadedBytes;
                    ReportTransfer(progress, request.JobId, DownloadStage.DownloadingAudio, value, 70, 20, videoBytes);
                    LogProgress(value, 70, 20);
                });
                await downloadClient.DownloadFileAsync(
                    request.Stream.AudioUrl,
                    audioPath,
                    Math.Max(1, settings.MaxFileSizeBytes - videoBytes),
                    settings.MaxRetryCount,
                    TimeSpan.FromSeconds(settings.NetworkTimeoutSeconds),
                    audioProgress,
                    cancellationToken).ConfigureAwait(false);

                progress.Report(new DownloadProgressDto
                {
                    JobId = request.JobId,
                    Stage = DownloadStage.Merging,
                    DownloadedBytes = videoBytes + audioBytes,
                    Percentage = 92,
                    Message = "Đang ghép video và audio..."
                });
                await ffmpegService.MergeVideoAudioAsync(videoPath, audioPath, mergedPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Move(videoPath, mergedPath, overwrite: false);
            }

            progress.Report(new DownloadProgressDto
            {
                JobId = request.JobId,
                Stage = DownloadStage.Finalizing,
                DownloadedBytes = videoBytes + audioBytes,
                Percentage = 98,
                Message = "Đang hoàn tất file..."
            });
            File.Move(mergedPath, request.OutputPath, overwrite: false);
            progress.Report(new DownloadProgressDto
            {
                JobId = request.JobId,
                Stage = DownloadStage.Completed,
                DownloadedBytes = videoBytes + audioBytes,
                TotalBytes = videoBytes + audioBytes,
                Percentage = 100,
                Message = "Hoàn tất"
            });
            logger.LogInformation("Download completed for job {JobId}", request.JobId);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Download cancelled for job {JobId}", request.JobId);
            throw;
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            throw new AppException(AppErrorCode.DiskFull, "Ổ đĩa không còn đủ dung lượng.", exception);
        }
        finally
        {
            if (settings.DeleteTemporaryFiles)
            {
                try
                {
                    await fileService.CleanupJobTempDirectoryAsync(request.JobId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Unable to clean temp files for job {JobId}", request.JobId);
                }
            }
        }

        void LogProgress(TransferProgress value, double offset, double weight)
        {
            if (value.TotalBytes is not > 0)
            {
                return;
            }

            var percentage = offset + (Math.Clamp((double)value.DownloadedBytes / value.TotalBytes.Value, 0, 1) * weight);
            var bucket = (int)(percentage / 10);
            if (bucket <= lastLoggedProgressBucket)
            {
                return;
            }

            lastLoggedProgressBucket = bucket;
            logger.LogInformation("Download progress for job {JobId}: {Percentage:0}%", request.JobId, percentage);
        }
    }

    private static void ReportTransfer(
        IProgress<DownloadProgressDto> target,
        Guid jobId,
        DownloadStage stage,
        TransferProgress value,
        double offset,
        double weight,
        long previousBytes = 0)
    {
        var fraction = value.TotalBytes is > 0
            ? Math.Clamp((double)value.DownloadedBytes / value.TotalBytes.Value, 0, 1)
            : 0;
        TimeSpan? remaining = value.TotalBytes is > 0 && value.SpeedBytesPerSecond > 0
            ? TimeSpan.FromSeconds((value.TotalBytes.Value - value.DownloadedBytes) / value.SpeedBytesPerSecond)
            : null;
        target.Report(new DownloadProgressDto
        {
            JobId = jobId,
            Stage = stage,
            DownloadedBytes = previousBytes + value.DownloadedBytes,
            TotalBytes = value.TotalBytes is null ? null : previousBytes + value.TotalBytes,
            Percentage = offset + (fraction * weight),
            SpeedBytesPerSecond = value.SpeedBytesPerSecond,
            RemainingTime = remaining
        });
    }

    private static void EnsureDiskSpace(string outputDirectory, long? expectedBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(outputDirectory));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new AppException(AppErrorCode.DownloadError, "Không xác định được ổ đĩa output.");
        }

        var required = expectedBytes is > 0 ? expectedBytes.Value * 2 : 256L * 1024 * 1024;
        if (new DriveInfo(root).AvailableFreeSpace < required)
        {
            throw new AppException(AppErrorCode.DiskFull, "Ổ đĩa không đủ dung lượng dự kiến cho download.");
        }
    }

    private static bool IsDiskFull(IOException exception) =>
        (exception.HResult & 0xFFFF) is 0x27 or 0x70;

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

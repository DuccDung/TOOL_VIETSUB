using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BilibiliDownloader.Infrastructure.FFmpeg;

public sealed class FFmpegService(
    IFFmpegProcessRunner processRunner,
    IFFmpegProvisioningService provisioningService,
    IFFmpegDiscoveryService discoveryService,
    ISettingsService settingsService,
    ILogger<FFmpegService> logger) : IFFmpegService
{
    public async Task<string> MergeVideoAudioAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        EnsureInputFile(videoPath, "video");
        EnsureInputFile(audioPath, "audio");

        var provisioned = await provisioningService
            .EnsureAvailableAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var settings = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        string[] arguments =
        [
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i", Path.GetFullPath(videoPath),
            "-i", Path.GetFullPath(audioPath),
            "-map", "0:v:0",
            "-map", "1:a:0",
            "-c", "copy",
            "-movflags", "+faststart",
            "-progress", "pipe:1",
            Path.GetFullPath(outputPath)
        ];

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(settings.FfmpegTimeoutMinutes));
        logger.LogInformation("FFmpeg merge started with {Source}", provisioned.Source);
        try
        {
            var result = await processRunner
                .RunAsync(provisioned.ExecutablePath, arguments, timeoutSource.Token)
                .ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                var detail = LastNonEmptyLine(result.StandardError);
                throw new AppException(
                    AppErrorCode.FfmpegError,
                    string.IsNullOrWhiteSpace(detail)
                        ? "FFmpeg không thể ghép video và audio."
                        : $"FFmpeg: {detail}");
            }

            logger.LogInformation("FFmpeg merge completed");
            return outputPath;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppException(AppErrorCode.Timeout, "FFmpeg vượt quá thời gian cho phép.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new AppException(AppErrorCode.FfmpegError, "Không thể chạy FFmpeg.", exception);
        }
    }

    public async Task<(bool IsValid, string Message)> ValidateAsync(
        string? configuredPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            var available = await provisioningService.FindAvailableAsync(cancellationToken).ConfigureAwait(false);
            return available is null
                ? (false, "Chưa tìm thấy FFmpeg. Ứng dụng có thể tự tải bản portable.")
                : (true, $"FFmpeg {available.Version} ({available.Source})");
        }

        var validated = await discoveryService
            .ValidateCandidateAsync(configuredPath, FFmpegSource.Custom, cancellationToken)
            .ConfigureAwait(false);
        return validated is null
            ? (false, "Không thể chạy FFmpeg tại đường dẫn đã chọn.")
            : (true, $"FFmpeg {validated.Version} hợp lệ.");
    }

    private static void EnsureInputFile(string path, string kind)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.GetFullPath(path)))
        {
            throw new AppException(AppErrorCode.FfmpegError, $"File {kind} đầu vào không tồn tại.");
        }
    }

    private static string LastNonEmptyLine(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault() ?? string.Empty;
}

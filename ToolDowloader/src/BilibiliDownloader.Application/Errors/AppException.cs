using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Application.Errors;

public sealed class AppException : Exception
{
    public AppException(AppErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public AppErrorCode Code { get; }

    public string PublicCode => Code switch
    {
        AppErrorCode.InvalidUrl => "INVALID_URL",
        AppErrorCode.VideoNotFound => "VIDEO_NOT_FOUND",
        AppErrorCode.VideoNotAvailable => "VIDEO_NOT_AVAILABLE",
        AppErrorCode.NetworkError => "NETWORK_ERROR",
        AppErrorCode.Timeout => "TIMEOUT",
        AppErrorCode.DownloadError => "DOWNLOAD_ERROR",
        AppErrorCode.FfmpegNotFound => "FFMPEG_NOT_FOUND",
        AppErrorCode.FfmpegError => "FFMPEG_ERROR",
        AppErrorCode.FfmpegDownloadError => "FFMPEG_DOWNLOAD_ERROR",
        AppErrorCode.FfmpegChecksumMismatch => "FFMPEG_CHECKSUM_MISMATCH",
        AppErrorCode.FfmpegExtractionError => "FFMPEG_EXTRACTION_ERROR",
        AppErrorCode.FfmpegValidationError => "FFMPEG_VALIDATION_ERROR",
        AppErrorCode.FfmpegSourceUnavailable => "FFMPEG_SOURCE_UNAVAILABLE",
        AppErrorCode.DiskFull => "DISK_FULL",
        AppErrorCode.FileTooLarge => "FILE_TOO_LARGE",
        AppErrorCode.Cancelled => "CANCELLED",
        _ => "UNKNOWN_ERROR"
    };
}

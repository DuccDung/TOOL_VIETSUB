namespace BilibiliDownloader.Domain.Enums;

public enum AppErrorCode
{
    InvalidUrl,
    VideoNotFound,
    VideoNotAvailable,
    NetworkError,
    Timeout,
    DownloadError,
    FfmpegNotFound,
    FfmpegError,
    FfmpegDownloadError,
    FfmpegChecksumMismatch,
    FfmpegExtractionError,
    FfmpegValidationError,
    FfmpegSourceUnavailable,
    DiskFull,
    FileTooLarge,
    Cancelled,
    UnknownError
}

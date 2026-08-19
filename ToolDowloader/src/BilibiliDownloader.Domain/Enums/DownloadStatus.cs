namespace BilibiliDownloader.Domain.Enums;

public enum DownloadStatus
{
    Queued,
    Resolving,
    Downloading,
    Merging,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

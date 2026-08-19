namespace BilibiliDownloader.Domain.Enums;

public enum DownloadStage
{
    Waiting,
    Resolving,
    DownloadingVideo,
    DownloadingAudio,
    Merging,
    Finalizing,
    Completed,
    Failed,
    Cancelled
}

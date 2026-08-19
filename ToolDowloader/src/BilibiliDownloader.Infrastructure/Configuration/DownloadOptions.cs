namespace BilibiliDownloader.Infrastructure.Configuration;

public sealed class DownloadOptions
{
    public const string SectionName = "Download";

    public int BufferSize { get; set; } = 128 * 1024;
    public int ProgressIntervalMilliseconds { get; set; } = 200;
    public int QueueCapacity { get; set; } = 100;
    public int StaleTemporaryFileHours { get; set; } = 24;
}

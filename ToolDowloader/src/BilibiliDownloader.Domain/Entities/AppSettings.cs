using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Domain.Entities;

public sealed class AppSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string DownloadFolder { get; set; } = string.Empty;
    public string? FfmpegPath { get; set; }
    public int MaximumConcurrentDownloads { get; set; } = 2;
    public VideoQuality DefaultQuality { get; set; } = VideoQuality.BestAvailable;
    public string DefaultFormat { get; set; } = "MP4";
    public bool AutoOpenFolder { get; set; }
    public bool DeleteTemporaryFiles { get; set; } = true;
    public bool StartDownloadAutomatically { get; set; }
    public long MaxFileSizeBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public int MaxRetryCount { get; set; } = 3;
    public int NetworkTimeoutSeconds { get; set; } = 120;
    public int FfmpegTimeoutMinutes { get; set; } = 30;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

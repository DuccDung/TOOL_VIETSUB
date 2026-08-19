using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Domain.Entities;

public sealed class DownloadHistory
{
    public Guid Id { get; set; }
    public string VideoId { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public string Format { get; set; } = "MP4";
    public string? FilePath { get; set; }
    public long? FileSize { get; set; }
    public DownloadStatus Status { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

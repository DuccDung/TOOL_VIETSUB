using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Application.DTOs;

public sealed record DownloadProgressDto
{
    public Guid JobId { get; init; }
    public DownloadStage Stage { get; init; }
    public long DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public double Percentage { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public TimeSpan? RemainingTime { get; init; }
    public string? Message { get; init; }
}

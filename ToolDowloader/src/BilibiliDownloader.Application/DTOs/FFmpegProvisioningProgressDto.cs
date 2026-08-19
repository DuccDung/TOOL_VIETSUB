using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Application.DTOs;

public sealed record FFmpegProvisioningProgressDto
{
    public required FFmpegProvisioningState State { get; init; }
    public required string Message { get; init; }
    public long DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public double? Percentage { get; init; }
}

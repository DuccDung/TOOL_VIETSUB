using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Application.DTOs;

public sealed record FFmpegProvisioningResultDto
{
    public required string ExecutablePath { get; init; }
    public string? ProbePath { get; init; }
    public required string Version { get; init; }
    public required FFmpegSource Source { get; init; }
    public bool WasDownloaded { get; init; }
}

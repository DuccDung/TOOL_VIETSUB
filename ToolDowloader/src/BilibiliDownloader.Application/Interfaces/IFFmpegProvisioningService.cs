using BilibiliDownloader.Application.DTOs;

namespace BilibiliDownloader.Application.Interfaces;

public interface IFFmpegProvisioningService
{
    Task<FFmpegProvisioningResultDto?> FindAvailableAsync(CancellationToken cancellationToken = default);

    Task<FFmpegProvisioningResultDto> EnsureAvailableAsync(
        IProgress<FFmpegProvisioningProgressDto>? progress = null,
        CancellationToken cancellationToken = default);
}

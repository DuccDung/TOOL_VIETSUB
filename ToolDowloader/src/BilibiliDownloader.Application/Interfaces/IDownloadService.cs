using BilibiliDownloader.Application.DTOs;

namespace BilibiliDownloader.Application.Interfaces;

public interface IDownloadService
{
    Task DownloadAsync(
        DownloadRequestDto request,
        IProgress<DownloadProgressDto> progress,
        CancellationToken cancellationToken);
}

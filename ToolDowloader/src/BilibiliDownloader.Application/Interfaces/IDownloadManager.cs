using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Domain.Models;

namespace BilibiliDownloader.Application.Interfaces;

public interface IDownloadManager
{
    event EventHandler<DownloadJobSnapshot>? JobChanged;

    ValueTask<Guid> EnqueueAsync(DownloadRequestDto request, CancellationToken cancellationToken = default);
    bool Cancel(Guid jobId);
    IReadOnlyList<DownloadJobSnapshot> GetJobs();
}

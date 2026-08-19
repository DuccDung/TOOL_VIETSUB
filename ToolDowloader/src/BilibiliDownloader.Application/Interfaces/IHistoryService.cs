using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Application.Interfaces;

public interface IHistoryService
{
    Task AddAsync(DownloadHistory history, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(
        Guid id,
        DownloadStatus status,
        string? filePath = null,
        long? fileSize = null,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloadHistory>> GetRecentAsync(int maximumCount = 500, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task MarkRunningAsInterruptedAsync(CancellationToken cancellationToken = default);
}

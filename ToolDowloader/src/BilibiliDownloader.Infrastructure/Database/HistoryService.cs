using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BilibiliDownloader.Infrastructure.Database;

public sealed class HistoryService(IDbContextFactory<AppDbContext> contextFactory) : IHistoryService
{
    public async Task AddAsync(DownloadHistory history, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.DownloadHistories.Add(history);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(
        Guid id,
        DownloadStatus status,
        string? filePath = null,
        long? fileSize = null,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var history = await context.DownloadHistories.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (history is null)
        {
            return;
        }

        history.Status = status;
        history.FilePath = filePath ?? history.FilePath;
        history.FileSize = fileSize ?? history.FileSize;
        history.ErrorCode = errorCode;
        history.ErrorMessage = errorMessage;
        if (status is (DownloadStatus.Downloading or DownloadStatus.Resolving) && history.StartedAtUtc is null)
        {
            history.StartedAtUtc = DateTime.UtcNow;
        }

        if (status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled or DownloadStatus.Interrupted)
        {
            history.CompletedAtUtc = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadHistory>> GetRecentAsync(
        int maximumCount = 500,
        CancellationToken cancellationToken = default)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 5_000);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.DownloadHistories
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var item = await context.DownloadHistories.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        context.DownloadHistories.Remove(item);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkRunningAsInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var running = await context.DownloadHistories
            .Where(item => item.Status == DownloadStatus.Resolving ||
                           item.Status == DownloadStatus.Downloading ||
                           item.Status == DownloadStatus.Merging)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in running)
        {
            item.Status = DownloadStatus.Interrupted;
            item.ErrorCode = "INTERRUPTED";
            item.ErrorMessage = "Ứng dụng đã đóng trước khi download hoàn tất.";
            item.CompletedAtUtc = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

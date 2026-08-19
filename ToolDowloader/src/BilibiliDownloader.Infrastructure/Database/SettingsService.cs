using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Application.Validators;
using BilibiliDownloader.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BilibiliDownloader.Infrastructure.Database;

public sealed class SettingsService(
    IDbContextFactory<AppDbContext> contextFactory,
    IFileService fileService) : ISettingsService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings? _cached;

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return Clone(_cached);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return Clone(_cached);
            }

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await context.AppSettings
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == AppSettings.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (settings is null)
            {
                settings = CreateDefaults();
                context.AppSettings.Add(settings);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            _cached = Clone(settings);
            return Clone(settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SettingsValidator.Validate(settings);
        var normalized = Clone(settings);
        normalized.Id = AppSettings.SingletonId;
        normalized.DownloadFolder = Path.GetFullPath(normalized.DownloadFolder);
        normalized.FfmpegPath = string.IsNullOrWhiteSpace(normalized.FfmpegPath)
            ? null
            : Path.GetFullPath(normalized.FfmpegPath);
        normalized.UpdatedAtUtc = DateTime.UtcNow;
        Directory.CreateDirectory(normalized.DownloadFolder);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await context.AppSettings
                .SingleOrDefaultAsync(item => item.Id == AppSettings.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                context.AppSettings.Add(normalized);
            }
            else
            {
                context.Entry(existing).CurrentValues.SetValues(normalized);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _cached = Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, Clone(normalized));
    }

    private AppSettings CreateDefaults() => new()
    {
        DownloadFolder = fileService.DefaultDownloadDirectory
    };

    private static AppSettings Clone(AppSettings source) => new()
    {
        Id = source.Id,
        DownloadFolder = source.DownloadFolder,
        FfmpegPath = source.FfmpegPath,
        MaximumConcurrentDownloads = source.MaximumConcurrentDownloads,
        DefaultQuality = source.DefaultQuality,
        DefaultFormat = source.DefaultFormat,
        AutoOpenFolder = source.AutoOpenFolder,
        DeleteTemporaryFiles = source.DeleteTemporaryFiles,
        StartDownloadAutomatically = source.StartDownloadAutomatically,
        MaxFileSizeBytes = source.MaxFileSizeBytes,
        MaxRetryCount = source.MaxRetryCount,
        NetworkTimeoutSeconds = source.NetworkTimeoutSeconds,
        FfmpegTimeoutMinutes = source.FfmpegTimeoutMinutes,
        UpdatedAtUtc = source.UpdatedAtUtc
    };

    public void Dispose() => _gate.Dispose();
}

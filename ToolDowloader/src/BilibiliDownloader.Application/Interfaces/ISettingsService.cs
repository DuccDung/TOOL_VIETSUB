using BilibiliDownloader.Domain.Entities;

namespace BilibiliDownloader.Application.Interfaces;

public interface ISettingsService
{
    event EventHandler<AppSettings>? SettingsChanged;

    Task<AppSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

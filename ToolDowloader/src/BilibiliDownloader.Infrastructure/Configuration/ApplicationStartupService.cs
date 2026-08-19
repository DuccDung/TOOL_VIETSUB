using BilibiliDownloader.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Infrastructure.Configuration;

public sealed class ApplicationStartupService(
    IDatabaseInitializer databaseInitializer,
    IFileService fileService,
    IOptions<DownloadOptions> options,
    ILogger<ApplicationStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await databaseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await fileService.CleanupStaleTempDirectoriesAsync(
            TimeSpan.FromHours(options.Value.StaleTemporaryFileHours),
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Application startup tasks completed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

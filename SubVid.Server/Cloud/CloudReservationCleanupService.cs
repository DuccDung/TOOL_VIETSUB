using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;

namespace SubVid.Server.Cloud;

public sealed class CloudReservationCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudReservationCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpireAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not expire Cloud quota reservations.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task ExpireAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<SubVidDbContext>();
        var nowUtc = DateTime.UtcNow;
        var count = await database.CloudUsageReservations
            .Where(item => item.StatusCode == "HELD" && item.ExpiresAtUtc <= nowUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.StatusCode, "EXPIRED")
                .SetProperty(item => item.UpdatedAtUtc, nowUtc),
                cancellationToken);
        if (count > 0)
        {
            logger.LogInformation("Expired {Count} Cloud quota reservations.", count);
        }
    }
}

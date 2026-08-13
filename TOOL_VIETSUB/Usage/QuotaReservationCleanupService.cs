using Microsoft.EntityFrameworkCore;
using TOOL_VIETSUB.Data;

namespace TOOL_VIETSUB.Usage;

public sealed class QuotaReservationCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotaReservationCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unable to expire quota reservations.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ToolVietSubDbContext>();
        var nowUtc = DateTime.UtcNow;
        var count = await database.UsageReservations
            .Where(item => item.StatusCode == "HELD" && item.ExpiresAtUtc <= nowUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.StatusCode, "EXPIRED")
                .SetProperty(item => item.UpdatedAtUtc, nowUtc),
                cancellationToken);
        if (count > 0)
        {
            logger.LogInformation("Expired {ReservationCount} quota reservations.", count);
        }
    }
}

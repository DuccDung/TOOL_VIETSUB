using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;

namespace SubVid.Server.Registration;

public sealed class RegistrationCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<RegistrationCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CleanupAsync(stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<SubVidDbContext>();
            var nowUtc = DateTime.UtcNow;
            await database.RegistrationChallenges
                .Where(item => item.StatusCode == "PENDING" && item.ExpiresAtUtc <= nowUtc)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(item => item.StatusCode, "EXPIRED")
                        .SetProperty(item => item.UpdatedAtUtc, nowUtc),
                    cancellationToken);
            await database.PasswordResetChallenges
                .Where(item => item.StatusCode == "PENDING" && item.ExpiresAtUtc <= nowUtc)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(item => item.StatusCode, "EXPIRED")
                        .SetProperty(item => item.UpdatedAtUtc, nowUtc),
                    cancellationToken);
            var deleteBeforeUtc = nowUtc.AddDays(-7);
            var deletedRegistrations = await database.RegistrationChallenges
                .Where(item => item.StatusCode != "PENDING" && item.UpdatedAtUtc < deleteBeforeUtc)
                .ExecuteDeleteAsync(cancellationToken);
            var deletedPasswordResets = await database.PasswordResetChallenges
                .Where(item => item.StatusCode != "PENDING" && item.UpdatedAtUtc < deleteBeforeUtc)
                .ExecuteDeleteAsync(cancellationToken);
            if (deletedRegistrations + deletedPasswordResets > 0)
            {
                logger.LogInformation(
                    "Removed {RegistrationCount} registration and {PasswordResetCount} password-reset challenges.",
                    deletedRegistrations,
                    deletedPasswordResets);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Registration challenge cleanup failed.");
        }
    }
}

using System.Data;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;

namespace SubVid.Server.Cloud;

public sealed class CloudAllocationReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudAllocationReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not reconcile Cloud credential allocations.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<SubVidDbContext>();
        var nowUtc = DateTime.UtcNow;
        var dedicatedPlanIds = await database.ServicePlanCloudPolicies.AsNoTracking()
            .Where(item => item.IsActive
                && item.AllocationMode == CloudCredentialAllocationModes.Dedicated)
            .Select(item => item.PlanId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        var candidates = await database.UserSubscriptions.AsNoTracking()
            .Where(item => dedicatedPlanIds.Contains(item.PlanId)
                && item.StatusCode == "ACTIVE"
                && item.StartsAtUtc <= nowUtc
                && (item.EndsAtUtc == null || item.EndsAtUtc > nowUtc))
            .Select(item => item.UserId)
            .Union(database.CloudProviderCredentials.AsNoTracking()
                .Where(item => item.AllocationMode == CloudCredentialAllocationModes.Dedicated
                    && item.AllocationSourceCode == CloudCredentialAllocationSources.Plan
                    && item.AssignedUserId != null)
                .Select(item => item.AssignedUserId!.Value))
            .Distinct()
            .Take(500)
            .ToArrayAsync(cancellationToken);
        var freePlanId = await database.ServicePlans.AsNoTracking()
            .Where(item => item.PlanCode == "FREE" && item.IsActive)
            .Select(item => (Guid?)item.PlanId)
            .SingleOrDefaultAsync(cancellationToken);
        if (freePlanId is Guid freeId && dedicatedPlanIds.Contains(freeId))
        {
            var freeUsers = await database.Users.AsNoTracking()
                .Where(item => item.StatusCode == "ACTIVE"
                    && item.DeletedAtUtc == null
                    && !item.UserSubscriptions.Any(subscription =>
                        subscription.StatusCode == "ACTIVE"
                        && subscription.StartsAtUtc <= nowUtc
                        && (subscription.EndsAtUtc == null || subscription.EndsAtUtc > nowUtc)
                        && subscription.Plan.IsActive))
                .Select(item => item.UserId)
                .Take(500)
                .ToArrayAsync(cancellationToken);
            candidates = candidates.Concat(freeUsers).Distinct().Take(500).ToArray();
        }
        if (candidates.Length == 0)
        {
            return;
        }
        var changed = 0;
        foreach (var userId in candidates)
        {
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                var planId = await database.UserSubscriptions.AsNoTracking()
                    .Where(item => item.UserId == userId
                        && item.StatusCode == "ACTIVE"
                        && item.StartsAtUtc <= nowUtc
                        && (item.EndsAtUtc == null || item.EndsAtUtc > nowUtc)
                        && item.Plan.IsActive)
                    .OrderByDescending(item => item.StartsAtUtc)
                    .Select(item => (Guid?)item.PlanId)
                    .FirstOrDefaultAsync(cancellationToken) ?? freePlanId;
                if (planId is not null)
                {
                    var result = await new CloudCredentialAllocationService(database)
                        .SynchronizeForPlanAsync(
                            userId,
                            planId.Value,
                            null,
                            "Đối soát định kỳ subscription và API key.",
                            cancellationToken);
                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    changed += result.AllocatedCredentialIds.Count + result.ReleasedCredentialIds.Count;
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
            }
            catch (Exception exception)
            {
                database.ChangeTracker.Clear();
                logger.LogWarning(exception, "Could not reconcile Cloud allocation for user {UserId}.", userId);
            }
        }

        if (changed > 0)
        {
            logger.LogInformation("Reconciled {Count} Cloud credential allocation changes.", changed);
        }
    }
}

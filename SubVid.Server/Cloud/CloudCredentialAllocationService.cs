using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Cloud;

public sealed class CloudCredentialAllocationService(SubVidDbContext database)
{
    public async Task<CloudPlanAllocationResult> SynchronizeForPlanAsync(
        Guid userId,
        Guid planId,
        Guid? actorUserId,
        string reason,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var requiredProviders = await database.ServicePlanCloudPolicies
            .AsNoTracking()
            .Where(item => item.PlanId == planId
                && item.IsActive
                && item.AllocationMode == CloudCredentialAllocationModes.Dedicated)
            .Select(item => item.ProviderCode)
            .ToArrayAsync(cancellationToken);
        var required = requiredProviders.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var released = new List<Guid>();
        var allocated = new List<Guid>();
        var unavailable = new List<string>();
        var planManaged = await database.CloudProviderCredentials
            .Where(item => item.AssignedUserId == userId
                && item.AllocationMode == CloudCredentialAllocationModes.Dedicated
                && item.AllocationSourceCode == CloudCredentialAllocationSources.Plan)
            .ToArrayAsync(cancellationToken);

        foreach (var credential in planManaged.Where(item => !required.Contains(item.ProviderCode)))
        {
            Release(credential, nowUtc);
            AddHistory(credential, "RELEASED", actorUserId, reason, nowUtc);
            released.Add(credential.CredentialId);
        }

        foreach (var credential in planManaged.Where(item =>
                     required.Contains(item.ProviderCode) && item.AllocationPlanId != planId))
        {
            credential.AllocationPlanId = planId;
            credential.UpdatedAtUtc = nowUtc;
            AddHistory(credential, "MOVED", actorUserId, reason, nowUtc);
        }

        foreach (var provider in required.Order(StringComparer.OrdinalIgnoreCase))
        {
            var alreadyAssigned = await database.CloudProviderCredentials.AnyAsync(
                item => item.AssignedUserId == userId
                    && item.ProviderCode == provider
                    && item.AllocationMode == CloudCredentialAllocationModes.Dedicated
                    && item.StatusCode == "ACTIVE",
                cancellationToken);
            if (alreadyAssigned)
            {
                continue;
            }

            var credential = await database.CloudProviderCredentials
                .FromSqlInterpolated($$"""
                    SELECT TOP (1) *
                    FROM dbo.cloud_provider_credentials WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                    WHERE provider_code = {{provider}}
                      AND status_code = 'ACTIVE'
                      AND allocation_mode = 'UNASSIGNED'
                    ORDER BY priority, last_issued_at_utc, created_at_utc
                    """)
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
            if (credential is null)
            {
                unavailable.Add(provider);
                continue;
            }

            credential.AllocationMode = CloudCredentialAllocationModes.Dedicated;
            credential.AssignedUserId = userId;
            credential.PoolId = null;
            credential.AllocationPlanId = planId;
            credential.AllocationSourceCode = CloudCredentialAllocationSources.Plan;
            credential.AllocatedAtUtc = nowUtc;
            credential.UpdatedAtUtc = nowUtc;
            AddHistory(credential, "ASSIGNED", actorUserId, reason, nowUtc);
            allocated.Add(credential.CredentialId);
        }

        return new CloudPlanAllocationResult(allocated, released, unavailable);
    }

    public async Task AssignAsync(
        CloudProviderCredential credential,
        string allocationMode,
        Guid? poolId,
        Guid? assignedUserId,
        Guid? planId,
        string sourceCode,
        Guid? actorUserId,
        string reason,
        CancellationToken cancellationToken)
    {
        var mode = allocationMode.Trim().ToUpperInvariant();
        if (!CloudCredentialAllocationModes.IsValid(mode))
        {
            throw new InvalidOperationException("Chế độ phân bổ API key không hợp lệ.");
        }

        CloudKeyPool? pool = null;
        if (mode == CloudCredentialAllocationModes.Shared)
        {
            if (poolId is null)
            {
                throw new InvalidOperationException("Key dùng chung phải được gắn vào một pool.");
            }

            pool = await database.CloudKeyPools.SingleOrDefaultAsync(
                item => item.PoolId == poolId && item.StatusCode == CloudKeyPoolStatuses.Active,
                cancellationToken)
                ?? throw new InvalidOperationException("Pool key không tồn tại hoặc đã bị tắt.");
            if (!string.Equals(pool.ProviderCode, credential.ProviderCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Pool và API key phải thuộc cùng nhà cung cấp.");
            }
        }
        else if (mode == CloudCredentialAllocationModes.Dedicated && assignedUserId is null)
        {
            throw new InvalidOperationException("Key riêng phải được gắn với một người dùng.");
        }

        var nowUtc = DateTime.UtcNow;
        var previousMode = credential.AllocationMode;
        var changed = previousMode != mode
            || credential.PoolId != pool?.PoolId
            || credential.AssignedUserId != assignedUserId
            || credential.AllocationPlanId != planId;
        if (!changed)
        {
            return;
        }

        credential.AllocationMode = mode;
        credential.PoolId = mode == CloudCredentialAllocationModes.Shared ? pool!.PoolId : null;
        credential.AssignedUserId = mode == CloudCredentialAllocationModes.Dedicated ? assignedUserId : null;
        credential.AllocationPlanId = mode == CloudCredentialAllocationModes.Dedicated ? planId : null;
        credential.AllocationSourceCode = mode == CloudCredentialAllocationModes.Unassigned ? null : sourceCode;
        credential.AllocatedAtUtc = mode == CloudCredentialAllocationModes.Unassigned ? null : nowUtc;
        credential.UpdatedAtUtc = nowUtc;
        AddHistory(
            credential,
            mode == CloudCredentialAllocationModes.Unassigned
                ? "RELEASED"
                : previousMode == CloudCredentialAllocationModes.Unassigned ? "ASSIGNED" : "MOVED",
            actorUserId,
            reason,
            nowUtc);
    }

    private void AddHistory(
        CloudProviderCredential credential,
        string eventCode,
        Guid? actorUserId,
        string reason,
        DateTime nowUtc) => database.CloudCredentialAllocationHistory.Add(new CloudCredentialAllocationHistory
        {
            AllocationHistoryId = Guid.NewGuid(),
            CredentialId = credential.CredentialId,
            EventCode = eventCode,
            AllocationMode = credential.AllocationMode,
            PoolId = credential.PoolId,
            AssignedUserId = credential.AssignedUserId,
            PlanId = credential.AllocationPlanId,
            SourceCode = credential.AllocationSourceCode,
            ActorUserId = actorUserId,
            Reason = reason.Length <= 240 ? reason : reason[..240],
            CreatedAtUtc = nowUtc,
        });

    private static void Release(CloudProviderCredential credential, DateTime nowUtc)
    {
        credential.AllocationMode = CloudCredentialAllocationModes.Unassigned;
        credential.AssignedUserId = null;
        credential.PoolId = null;
        credential.AllocationPlanId = null;
        credential.AllocationSourceCode = null;
        credential.AllocatedAtUtc = null;
        credential.UpdatedAtUtc = nowUtc;
    }
}

public sealed record CloudPlanAllocationResult(
    IReadOnlyList<Guid> AllocatedCredentialIds,
    IReadOnlyList<Guid> ReleasedCredentialIds,
    IReadOnlyList<string> UnavailableProviders);

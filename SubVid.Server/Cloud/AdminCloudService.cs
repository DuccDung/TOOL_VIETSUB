using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Cloud;

public sealed class AdminCloudService(
    SubVidDbContext database,
    CloudCredentialProtector protector,
    CloudAccessService cloudAccess,
    CloudCredentialProbeService credentialProbe)
{
    private static readonly HashSet<string> SupportedProviders =
        new(StringComparer.OrdinalIgnoreCase) { "openai", "gemini", "deepseek", "groq" };

    public async Task<AdminCloudOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var periodStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var activeCredentials = await database.CloudProviderCredentials.AsNoTracking()
            .CountAsync(item => item.StatusCode == "ACTIVE"
                && item.AllocationMode != CloudCredentialAllocationModes.Unassigned,
                cancellationToken);
        var configuredUsers = await database.CloudQuotaLimits.AsNoTracking()
            .Where(item => item.UnitCode == CloudUsageUnits.LlmToken && item.MonthlyLimit > 0)
            .Select(item => item.UserId)
            .Union(database.UserSubscriptions.AsNoTracking()
                .Where(item => item.StatusCode == "ACTIVE"
                    && item.StartsAtUtc <= nowUtc
                    && (item.EndsAtUtc == null || item.EndsAtUtc > nowUtc)
                    && item.Plan.IsActive
                    && item.Plan.CloudPolicies.Any(policy => policy.IsActive && policy.MonthlyTokenLimit > 0))
                .Select(item => item.UserId))
            .Distinct()
            .CountAsync(cancellationToken);
        var allocationCounts = await database.CloudProviderCredentials.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Unassigned = group.Count(item => item.AllocationMode == CloudCredentialAllocationModes.Unassigned),
                Shared = group.Count(item => item.AllocationMode == CloudCredentialAllocationModes.Shared),
                Dedicated = group.Count(item => item.AllocationMode == CloudCredentialAllocationModes.Dedicated),
                Error = group.Count(item => item.StatusCode == "ERROR"),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var usedTokens = await database.CloudUsageLedger.AsNoTracking()
            .Where(item => item.UnitCode == CloudUsageUnits.LlmToken
                && item.QuotaPeriodStartUtc == periodStart)
            .SumAsync(item => (decimal?)item.TotalUnits, cancellationToken) ?? 0;
        var heldTokens = await database.CloudUsageReservations.AsNoTracking()
            .Where(item => item.UnitCode == CloudUsageUnits.LlmToken
                && item.QuotaPeriodStartUtc == periodStart
                && item.StatusCode == "HELD"
                && item.ExpiresAtUtc > nowUtc)
            .SumAsync(item => (decimal?)item.ReservedUnits, cancellationToken) ?? 0;
        var todayRequests = await database.CloudUsageLedger.AsNoTracking()
            .CountAsync(item => item.OccurredAtUtc >= nowUtc.Date, cancellationToken);
        return new AdminCloudOverview(
            activeCredentials,
            configuredUsers,
            DecimalToLong(usedTokens),
            DecimalToLong(heldTokens),
            todayRequests,
            periodStart,
            periodStart.AddMonths(1),
            allocationCounts?.Unassigned ?? 0,
            allocationCounts?.Shared ?? 0,
            allocationCounts?.Dedicated ?? 0,
            allocationCounts?.Error ?? 0);
    }

    public async Task<IReadOnlyList<AdminCloudCredential>> GetCredentialsAsync(
        CancellationToken cancellationToken) =>
        await database.CloudProviderCredentials.AsNoTracking()
            .Include(item => item.AssignedUser)
            .Include(item => item.Pool)
            .OrderBy(item => item.ProviderCode)
            .ThenBy(item => item.Priority)
            .ThenBy(item => item.DisplayName)
            .Select(item => new AdminCloudCredential(
                item.CredentialId,
                item.ProviderCode,
                item.DisplayName,
                item.KeySuffix,
                item.AssignedUserId,
                item.AssignedUser == null ? null : item.AssignedUser.Email,
                item.StatusCode,
                item.Priority,
                item.LastIssuedAtUtc,
                item.UpdatedAtUtc,
                item.AllocationMode,
                item.PoolId,
                item.Pool == null ? null : item.Pool.DisplayName,
                item.AllocationPlanId,
                item.AllocationSourceCode,
                item.AllocatedAtUtc))
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<AdminCloudKeyPool>> GetPoolsAsync(
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var pools = await database.CloudKeyPools.AsNoTracking()
            .Include(item => item.PlanLinks)
                .ThenInclude(link => link.Plan)
            .OrderBy(item => item.ProviderCode)
            .ThenBy(item => item.DisplayName)
            .ToArrayAsync(cancellationToken);
        var activeSubscribers = await database.UserSubscriptions.AsNoTracking()
            .Where(item => item.StatusCode == "ACTIVE"
                && item.StartsAtUtc <= nowUtc
                && (item.EndsAtUtc == null || item.EndsAtUtc > nowUtc))
            .GroupBy(item => item.PlanId)
            .Select(group => new
            {
                PlanId = group.Key,
                Count = group.Select(item => item.UserId).Distinct().Count(),
            })
            .ToDictionaryAsync(item => item.PlanId, item => item.Count, cancellationToken);
        var credentialCounts = await database.CloudProviderCredentials.AsNoTracking()
            .Where(item => item.PoolId != null)
            .GroupBy(item => item.PoolId!.Value)
            .Select(group => new { PoolId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.PoolId, item => item.Count, cancellationToken);

        return pools.Select(pool => new AdminCloudKeyPool(
            pool.PoolId,
            pool.PoolCode,
            pool.DisplayName,
            pool.ProviderCode,
            pool.StatusCode,
            pool.IsLegacy,
            pool.PlanLinks.Select(link => link.Plan.PlanCode)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            pool.PlanLinks.Sum(link => activeSubscribers.GetValueOrDefault(link.PlanId)),
            credentialCounts.GetValueOrDefault(pool.PoolId)))
            .ToArray();
    }

    public async Task<AdminCloudAccount?> FindAccountAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(
            item => item.EmailNormalized == normalizedEmail && item.DeletedAtUtc == null,
            cancellationToken);
        if (user is null)
        {
            return null;
        }

        var balance = await cloudAccess.GetBalanceAsync(
            user.UserId,
            CloudUsageUnits.LlmToken,
            cancellationToken);
        var customLimit = await database.CloudQuotaLimits.AsNoTracking()
            .Where(item => item.UserId == user.UserId && item.UnitCode == CloudUsageUnits.LlmToken)
            .Select(item => (decimal?)item.MonthlyLimit)
            .SingleOrDefaultAsync(cancellationToken);
        return new AdminCloudAccount(
            user.UserId,
            user.Email,
            user.DisplayName,
            user.StatusCode,
            balance.MonthlyLimit,
            balance.UsedUnits,
            balance.HeldUnits,
            balance.RemainingUnits,
            balance.PeriodStartsAtUtc,
            balance.PeriodEndsAtUtc,
            customLimit is null ? null : DecimalToLong(customLimit.Value));
    }

    public async Task<IReadOnlyList<AdminCloudLedgerItem>> GetRecentLedgerAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(count, 1, 100);
        return await (
            from ledger in database.CloudUsageLedger.AsNoTracking()
            join user in database.Users.AsNoTracking() on ledger.UserId equals user.UserId
            join credential in database.CloudProviderCredentials.AsNoTracking()
                on ledger.CredentialId equals credential.CredentialId
            orderby ledger.OccurredAtUtc descending
            select new AdminCloudLedgerItem(
                ledger.LedgerId,
                user.Email,
                ledger.ProviderCode,
                ledger.ModelId,
                credential.DisplayName,
                DecimalToLong(ledger.InputUnits),
                DecimalToLong(ledger.OutputUnits),
                DecimalToLong(ledger.TotalUnits),
                ledger.ApiRequestCount,
                ledger.RetryRequestCount,
                ledger.ProviderRequestId,
                ledger.OccurredAtUtc))
            .Take(take)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<AdminCloudLedgerPage> GetLedgerPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 10, 100);
        var totalCount = await database.CloudUsageLedger.AsNoTracking()
            .CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)safePageSize));
        safePage = Math.Min(safePage, totalPages);

        var items = await (
            from ledger in database.CloudUsageLedger.AsNoTracking()
            join user in database.Users.AsNoTracking() on ledger.UserId equals user.UserId
            join credential in database.CloudProviderCredentials.AsNoTracking()
                on ledger.CredentialId equals credential.CredentialId
            orderby ledger.OccurredAtUtc descending, ledger.LedgerId descending
            select new AdminCloudLedgerItem(
                ledger.LedgerId,
                user.Email,
                ledger.ProviderCode,
                ledger.ModelId,
                credential.DisplayName,
                DecimalToLong(ledger.InputUnits),
                DecimalToLong(ledger.OutputUnits),
                DecimalToLong(ledger.TotalUnits),
                ledger.ApiRequestCount,
                ledger.RetryRequestCount,
                ledger.ProviderRequestId,
                ledger.OccurredAtUtc))
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToArrayAsync(cancellationToken);

        return new AdminCloudLedgerPage(items, totalCount, safePage, safePageSize, totalPages);
    }

    public async Task<AdminCloudCredential> SaveCredentialAsync(
        Guid actorAdminId,
        Guid? credentialId,
        string providerCode,
        string displayName,
        string? apiKey,
        string? assignedEmail,
        int priority,
        string? ipAddress,
        CancellationToken cancellationToken) =>
        await SaveCredentialAsync(
            actorAdminId,
            credentialId,
            providerCode,
            displayName,
            apiKey,
            string.IsNullOrWhiteSpace(assignedEmail)
                ? CloudCredentialAllocationModes.Unassigned
                : CloudCredentialAllocationModes.Dedicated,
            null,
            assignedEmail,
            priority,
            ipAddress,
            cancellationToken);

    public async Task<AdminCloudCredential> SaveCredentialAsync(
        Guid actorAdminId,
        Guid? credentialId,
        string providerCode,
        string displayName,
        string? apiKey,
        string allocationMode,
        Guid? poolId,
        string? assignedEmail,
        int priority,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var provider = NormalizeProvider(providerCode);
        if (!SupportedProviders.Contains(provider))
        {
            throw new InvalidOperationException("Nhà cung cấp Cloud không hợp lệ.");
        }

        var name = displayName.Trim();
        if (name.Length is 0 or > 120)
        {
            throw new InvalidOperationException("Tên API key phải từ 1 đến 120 ký tự.");
        }

        if (priority is < 0 or > 10000)
        {
            throw new InvalidOperationException("Độ ưu tiên phải từ 0 đến 10000.");
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var mode = allocationMode.Trim().ToUpperInvariant();
        if (!CloudCredentialAllocationModes.IsValid(mode))
        {
            throw new InvalidOperationException("Chế độ phân bổ API key không hợp lệ.");
        }
        var assignedUser = mode != CloudCredentialAllocationModes.Dedicated
            || string.IsNullOrWhiteSpace(assignedEmail)
            ? null
            : await FindUserAsync(assignedEmail, cancellationToken)
                ?? throw new InvalidOperationException("Không tìm thấy người dùng được phân bổ key.");
        var nowUtc = DateTime.UtcNow;
        CloudProviderCredential credential;
        if (credentialId is Guid id)
        {
            credential = await database.CloudProviderCredentials.SingleOrDefaultAsync(
                item => item.CredentialId == id,
                cancellationToken)
                ?? throw new InvalidOperationException("Không tìm thấy API key cần cập nhật.");
            credential.ProviderCode = provider;
            credential.DisplayName = name;
            credential.Priority = priority;
            credential.UpdatedAtUtc = nowUtc;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                ApplySecret(credential, apiKey);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("API key mới không được để trống.");
            }

            credential = new CloudProviderCredential
            {
                CredentialId = Guid.NewGuid(),
                ProviderCode = provider,
                DisplayName = name,
                AllocationMode = CloudCredentialAllocationModes.Unassigned,
                StatusCode = "ACTIVE",
                Priority = priority,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            ApplySecret(credential, apiKey);
            database.CloudProviderCredentials.Add(credential);
        }

        await new CloudCredentialAllocationService(database).AssignAsync(
            credential,
            mode,
            poolId,
            assignedUser?.UserId,
            null,
            CloudCredentialAllocationSources.Admin,
            actorAdminId,
            "Admin cập nhật phân bổ API key.",
            cancellationToken);

        AddAudit(actorAdminId, "ADMIN_CLOUD_CREDENTIAL_SAVE", ipAddress, new
        {
            credential.CredentialId,
            credential.ProviderCode,
            credential.DisplayName,
            credential.AllocationMode,
            credential.PoolId,
            credential.AssignedUserId,
            credential.Priority,
            secretChanged = !string.IsNullOrWhiteSpace(apiKey),
        });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException(
                "API key này đã tồn tại trong kho credential của provider.",
                exception);
        }

        await transaction.CommitAsync(cancellationToken);
        return (await GetCredentialsAsync(cancellationToken))
            .Single(item => item.CredentialId == credential.CredentialId);
    }

    public async Task<AdminCloudCredential> AssignCredentialAsync(
        Guid actorAdminId,
        Guid credentialId,
        string allocationMode,
        Guid? poolId,
        string? assignedEmail,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var credential = await database.CloudProviderCredentials.SingleOrDefaultAsync(
            item => item.CredentialId == credentialId,
            cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy API key cần phân bổ.");
        var mode = allocationMode.Trim().ToUpperInvariant();
        var assignedUser = mode == CloudCredentialAllocationModes.Dedicated
            && !string.IsNullOrWhiteSpace(assignedEmail)
            ? await FindUserAsync(assignedEmail, cancellationToken)
                ?? throw new InvalidOperationException("Không tìm thấy người dùng được phân bổ key.")
            : null;
        await new CloudCredentialAllocationService(database).AssignAsync(
            credential,
            mode,
            poolId,
            assignedUser?.UserId,
            null,
            CloudCredentialAllocationSources.Admin,
            actorAdminId,
            "Admin thay đổi phân bổ từ Kho Key.",
            cancellationToken);
        AddAudit(actorAdminId, "ADMIN_CLOUD_CREDENTIAL_ASSIGN", ipAddress, new
        {
            credential.CredentialId,
            credential.AllocationMode,
            credential.PoolId,
            credential.AssignedUserId,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetCredentialsAsync(cancellationToken))
            .Single(item => item.CredentialId == credentialId);
    }

    public async Task<AdminCloudCredential> ToggleCredentialAsync(
        Guid actorAdminId,
        Guid credentialId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var credential = await database.CloudProviderCredentials.SingleOrDefaultAsync(
            item => item.CredentialId == credentialId,
            cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy API key cần thay đổi.");
        credential.StatusCode = credential.StatusCode == "ACTIVE" ? "DISABLED" : "ACTIVE";
        credential.UpdatedAtUtc = DateTime.UtcNow;
        AddAudit(actorAdminId, "ADMIN_CLOUD_CREDENTIAL_TOGGLE", ipAddress, new
        {
            credential.CredentialId,
            credential.ProviderCode,
            credential.StatusCode,
        });
        await database.SaveChangesAsync(cancellationToken);
        return (await GetCredentialsAsync(cancellationToken))
            .Single(item => item.CredentialId == credential.CredentialId);
    }

    public async Task<CloudCredentialProbeResult> ProbeNewCredentialAsync(
        Guid actorAdminId,
        string providerCode,
        string apiKey,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var provider = NormalizeProvider(providerCode);
        if (!SupportedProviders.Contains(provider))
        {
            throw new InvalidOperationException("Nhà cung cấp Cloud không hợp lệ.");
        }

        var result = await credentialProbe.ProbeAsync(provider, apiKey, cancellationToken);
        AddAudit(actorAdminId, "ADMIN_CLOUD_CREDENTIAL_PROBE", ipAddress, new
        {
            providerCode = provider,
            result.Code,
            result.HttpStatusCode,
            result.LatencyMilliseconds,
            result.ProviderRequestId,
            source = "NEW_SECRET",
        }, result.Succeeded ? "SUCCESS" : "FAILED");
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<(AdminCloudCredential Credential, CloudCredentialProbeResult Result)> ProbeStoredCredentialAsync(
        Guid actorAdminId,
        Guid credentialId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var credential = await database.CloudProviderCredentials.SingleOrDefaultAsync(
            item => item.CredentialId == credentialId,
            cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy API key cần kiểm tra.");
        var result = await credentialProbe.ProbeAsync(
            credential.ProviderCode,
            protector.Unprotect(credential.EncryptedApiKey),
            cancellationToken);
        credential.StatusCode = result.Succeeded ? "ACTIVE" : "ERROR";
        credential.UpdatedAtUtc = DateTime.UtcNow;
        AddAudit(actorAdminId, "ADMIN_CLOUD_CREDENTIAL_PROBE", ipAddress, new
        {
            credential.CredentialId,
            credential.ProviderCode,
            result.Code,
            result.HttpStatusCode,
            result.LatencyMilliseconds,
            result.ProviderRequestId,
            source = "STORED_SECRET",
        }, result.Succeeded ? "SUCCESS" : "FAILED");
        await database.SaveChangesAsync(cancellationToken);
        var view = (await GetCredentialsAsync(cancellationToken))
            .Single(item => item.CredentialId == credential.CredentialId);
        return (view, result);
    }

    public async Task<AdminCloudAccount> SaveQuotaAsync(
        Guid actorAdminId,
        string email,
        long monthlyLlmTokens,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (monthlyLlmTokens is < 0 or > 1_000_000_000_000)
        {
            throw new InvalidOperationException("Hạn mức token phải từ 0 đến 1.000.000.000.000.");
        }

        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var user = await FindUserAsync(email, cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy tài khoản với email này.");
        var nowUtc = DateTime.UtcNow;
        var limit = await database.CloudQuotaLimits.SingleOrDefaultAsync(
            item => item.UserId == user.UserId && item.UnitCode == CloudUsageUnits.LlmToken,
            cancellationToken);
        if (limit is null)
        {
            limit = new CloudQuotaLimit
            {
                UserId = user.UserId,
                UnitCode = CloudUsageUnits.LlmToken,
            };
            database.CloudQuotaLimits.Add(limit);
        }

        var previousLimit = limit.MonthlyLimit;
        limit.MonthlyLimit = monthlyLlmTokens;
        limit.UpdatedAtUtc = nowUtc;
        limit.UpdatedByUserId = actorAdminId;
        database.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            UserId = user.UserId,
            EventCode = "ADMIN_CLOUD_QUOTA_CHANGE",
            OutcomeCode = "SUCCESS",
            IpAddress = ipAddress,
            DeviceId = "WEB_ADMIN",
            DetailsJson = JsonSerializer.Serialize(new
            {
                actorAdminId,
                targetUserId = user.UserId,
                unitCode = CloudUsageUnits.LlmToken,
                previousLimit,
                monthlyLlmTokens,
            }),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await database.SaveChangesAsync(cancellationToken);
        return await FindAccountAsync(user.Email, cancellationToken)
            ?? throw new InvalidOperationException("Không thể tải lại hạn mức vừa cập nhật.");
    }

    public async Task<AdminCloudAccount> ResetQuotaToPlanAsync(
        Guid actorAdminId,
        string email,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var user = await FindUserAsync(email, cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy tài khoản với email này.");
        var limit = await database.CloudQuotaLimits.SingleOrDefaultAsync(
            item => item.UserId == user.UserId && item.UnitCode == CloudUsageUnits.LlmToken,
            cancellationToken);
        if (limit is not null)
        {
            database.CloudQuotaLimits.Remove(limit);
        }
        AddAudit(actorAdminId, "ADMIN_CLOUD_QUOTA_RESET_TO_PLAN", ipAddress, new
        {
            targetUserId = user.UserId,
            previousLimit = limit?.MonthlyLimit,
        });
        await database.SaveChangesAsync(cancellationToken);
        return await FindAccountAsync(user.Email, cancellationToken)
            ?? throw new InvalidOperationException("Không thể tải lại hạn mức theo gói.");
    }

    private async Task EnsureAdminAsync(Guid actorAdminId, CancellationToken cancellationToken)
    {
        var valid = await database.Users.AnyAsync(
            item => item.UserId == actorAdminId
                && item.RoleCode == "ADMIN"
                && item.StatusCode == "ACTIVE"
                && item.DeletedAtUtc == null,
            cancellationToken);
        if (!valid)
        {
            throw new UnauthorizedAccessException("Phiên quản trị không còn hợp lệ.");
        }
    }

    private Task<User?> FindUserAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        return database.Users.SingleOrDefaultAsync(
            item => item.EmailNormalized == normalizedEmail && item.DeletedAtUtc == null,
            cancellationToken);
    }

    private void ApplySecret(CloudProviderCredential credential, string apiKey)
    {
        credential.EncryptedApiKey = protector.Protect(apiKey);
        credential.KeyFingerprint = CloudCredentialProtector.Fingerprint(apiKey);
        credential.KeySuffix = CloudCredentialProtector.Suffix(apiKey);
    }

    private void AddAudit(
        Guid actorAdminId,
        string eventCode,
        string? ipAddress,
        object details,
        string outcomeCode = "SUCCESS") =>
        database.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            UserId = actorAdminId,
            EventCode = eventCode,
            OutcomeCode = outcomeCode,
            IpAddress = ipAddress,
            DeviceId = "WEB_ADMIN",
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAtUtc = DateTime.UtcNow,
        });

    private static string NormalizeProvider(string value) => value.Trim().ToLowerInvariant();

    private static long DecimalToLong(decimal value) => decimal.ToInt64(decimal.Truncate(value));
}

public sealed record AdminCloudOverview(
    int ActiveCredentials,
    int ConfiguredUsers,
    long UsedTokens,
    long HeldTokens,
    int RequestsToday,
    DateTime PeriodStartsAtUtc,
    DateTime PeriodEndsAtUtc,
    int UnassignedCredentials,
    int SharedCredentials,
    int DedicatedCredentials,
    int ErrorCredentials);

public sealed record AdminCloudCredential(
    Guid CredentialId,
    string ProviderCode,
    string DisplayName,
    string KeySuffix,
    Guid? AssignedUserId,
    string? AssignedEmail,
    string Status,
    int Priority,
    DateTime? LastIssuedAtUtc,
    DateTime UpdatedAtUtc,
    string AllocationMode,
    Guid? PoolId,
    string? PoolName,
    Guid? AllocationPlanId,
    string? AllocationSourceCode,
    DateTime? AllocatedAtUtc);

public sealed record AdminCloudKeyPool(
    Guid PoolId,
    string PoolCode,
    string DisplayName,
    string ProviderCode,
    string Status,
    bool IsLegacy,
    IReadOnlyList<string> PlanCodes,
    int ActiveSubscriberCount,
    int CredentialCount);

public sealed record AdminCloudAccount(
    Guid UserId,
    string Email,
    string DisplayName,
    string Status,
    long MonthlyLimit,
    long UsedUnits,
    long HeldUnits,
    long RemainingUnits,
    DateTime PeriodStartsAtUtc,
    DateTime PeriodEndsAtUtc,
    long? CustomMonthlyLimit);

public sealed record AdminCloudLedgerItem(
    Guid LedgerId,
    string UserEmail,
    string ProviderCode,
    string ModelId,
    string CredentialName,
    long InputUnits,
    long OutputUnits,
    long TotalUnits,
    int ApiRequests,
    int RetryRequests,
    string? ProviderRequestId,
    DateTime OccurredAtUtc);

public sealed record AdminCloudLedgerPage(
    IReadOnlyList<AdminCloudLedgerItem> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

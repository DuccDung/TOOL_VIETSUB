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
            .CountAsync(item => item.StatusCode == "ACTIVE", cancellationToken);
        var configuredUsers = await database.CloudQuotaLimits.AsNoTracking()
            .Where(item => item.UnitCode == CloudUsageUnits.LlmToken && item.MonthlyLimit > 0)
            .Select(item => item.UserId)
            .Distinct()
            .CountAsync(cancellationToken);
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
            periodStart.AddMonths(1));
    }

    public async Task<IReadOnlyList<AdminCloudCredential>> GetCredentialsAsync(
        CancellationToken cancellationToken) =>
        await database.CloudProviderCredentials.AsNoTracking()
            .Include(item => item.AssignedUser)
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
                item.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);

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
            balance.PeriodEndsAtUtc);
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

    public async Task<AdminCloudCredential> SaveCredentialAsync(
        Guid actorAdminId,
        Guid? credentialId,
        string providerCode,
        string displayName,
        string? apiKey,
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
        var assignedUser = string.IsNullOrWhiteSpace(assignedEmail)
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
            credential.AssignedUserId = assignedUser?.UserId;
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
                AssignedUserId = assignedUser?.UserId,
                StatusCode = "ACTIVE",
                Priority = priority,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            ApplySecret(credential, apiKey);
            database.CloudProviderCredentials.Add(credential);
        }

        AddAudit(actorAdminId, "ADMIN_CLOUD_CREDENTIAL_SAVE", ipAddress, new
        {
            credential.CredentialId,
            credential.ProviderCode,
            credential.DisplayName,
            assignedUserId = assignedUser?.UserId,
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
        var credential = await database.CloudProviderCredentials.AsNoTracking().SingleOrDefaultAsync(
            item => item.CredentialId == credentialId,
            cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy API key cần kiểm tra.");
        var result = await credentialProbe.ProbeAsync(
            credential.ProviderCode,
            protector.Unprotect(credential.EncryptedApiKey),
            cancellationToken);
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
    DateTime PeriodEndsAtUtc);

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
    DateTime UpdatedAtUtc);

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
    DateTime PeriodEndsAtUtc);

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

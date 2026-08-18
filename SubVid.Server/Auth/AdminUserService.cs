using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Auth;

public sealed class AdminUserService(SubVidDbContext database)
{
    private const int MaximumPageSize = 100;

    public async Task<AdminUserListResult> GetUsersAsync(
        AdminUserListQuery request,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var periodStartUtc = MonthStart(nowUtc);
        var periodEndUtc = periodStartUtc.AddMonths(1);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 10, MaximumPageSize);

        var allUsers = database.Users.AsNoTracking()
            .Where(item => item.DeletedAtUtc == null);
        var stats = new AdminUserOverview(
            await allUsers.CountAsync(cancellationToken),
            await allUsers.CountAsync(item => item.StatusCode == "ACTIVE", cancellationToken),
            await allUsers.CountAsync(item => item.StatusCode == "SUSPENDED", cancellationToken),
            await allUsers.CountAsync(item => item.CreatedAtUtc >= periodStartUtc, cancellationToken));

        var users = allUsers;
        var search = request.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            users = users.Where(item =>
                item.Email.Contains(search) || item.DisplayName.Contains(search));
        }

        var status = request.Status?.Trim().ToUpperInvariant();
        if (status is "ACTIVE" or "SUSPENDED" or "DISABLED")
        {
            users = users.Where(item => item.StatusCode == status);
        }

        var planCode = request.PlanCode?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(planCode))
        {
            if (planCode == "FREE")
            {
                users = users.Where(user =>
                    database.UserSubscriptions.Any(subscription =>
                        subscription.UserId == user.UserId
                        && subscription.StatusCode == "ACTIVE"
                        && subscription.StartsAtUtc <= nowUtc
                        && (subscription.EndsAtUtc == null || subscription.EndsAtUtc > nowUtc)
                        && subscription.Plan.IsActive
                        && subscription.Plan.PlanCode == "FREE")
                    || !database.UserSubscriptions.Any(subscription =>
                        subscription.UserId == user.UserId
                        && subscription.StatusCode == "ACTIVE"
                        && subscription.StartsAtUtc <= nowUtc
                        && (subscription.EndsAtUtc == null || subscription.EndsAtUtc > nowUtc)
                        && subscription.Plan.IsActive));
            }
            else
            {
                users = users.Where(user => database.UserSubscriptions.Any(subscription =>
                    subscription.UserId == user.UserId
                    && subscription.StatusCode == "ACTIVE"
                    && subscription.StartsAtUtc <= nowUtc
                    && (subscription.EndsAtUtc == null || subscription.EndsAtUtc > nowUtc)
                    && subscription.Plan.IsActive
                    && subscription.Plan.PlanCode == planCode));
            }
        }

        users = request.Sort?.Trim().ToLowerInvariant() switch
        {
            "oldest" => users.OrderBy(item => item.CreatedAtUtc),
            "name" => users.OrderBy(item => item.DisplayName).ThenBy(item => item.Email),
            "last-login" => users.OrderByDescending(item => item.LastLoginAtUtc),
            "token" => users.OrderByDescending(user => database.CloudUsageLedger
                .Where(item => item.UserId == user.UserId
                    && item.QuotaPeriodStartUtc == periodStartUtc)
                .Sum(item => (decimal?)item.TotalUnits) ?? 0),
            "minutes" => users.OrderByDescending(user => database.UsageRecords
                .Where(item => item.UserId == user.UserId
                    && item.OccurredAtUtc >= periodStartUtc
                    && item.OccurredAtUtc < periodEndUtc
                    && item.OperationCode == "MEDIA_PROCESSING"
                    && item.UnitCode == "MINUTE")
                .Sum(item => (decimal?)item.Quantity) ?? 0),
            _ => users.OrderByDescending(item => item.CreatedAtUtc),
        };

        var totalCount = await users.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);
        var pageUsers = await users
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new AdminUserBase(
                item.UserId,
                item.Email,
                item.DisplayName,
                item.RoleCode,
                item.StatusCode,
                item.EmailConfirmed,
                item.MonthlyQuotaMinutes,
                item.LastLoginAtUtc,
                item.CreatedAtUtc))
            .ToArrayAsync(cancellationToken);

        var items = await BuildListItemsAsync(
            pageUsers,
            nowUtc,
            periodStartUtc,
            periodEndUtc,
            cancellationToken);
        var plans = await database.ServicePlans.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.MonthlyQuotaMinutes)
            .ThenBy(item => item.DisplayName)
            .Select(item => new AdminUserPlanOption(item.PlanCode, item.DisplayName))
            .ToArrayAsync(cancellationToken);

        return new AdminUserListResult(
            stats,
            items,
            plans,
            totalCount,
            page,
            pageSize,
            totalPages,
            periodStartUtc,
            periodEndUtc);
    }

    public async Task<AdminUserDetail?> GetDetailAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await database.Users.AsNoTracking()
            .Where(item => item.UserId == userId && item.DeletedAtUtc == null)
            .Select(item => new AdminUserBase(
                item.UserId,
                item.Email,
                item.DisplayName,
                item.RoleCode,
                item.StatusCode,
                item.EmailConfirmed,
                item.MonthlyQuotaMinutes,
                item.LastLoginAtUtc,
                item.CreatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return null;
        }

        var nowUtc = DateTime.UtcNow;
        var periodStartUtc = MonthStart(nowUtc);
        var periodEndUtc = periodStartUtc.AddMonths(1);
        var subscriptions = await database.UserSubscriptions.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.StartsAtUtc)
            .Select(item => new AdminUserSubscriptionItem(
                item.SubscriptionId,
                item.Plan.PlanCode,
                item.Plan.DisplayName,
                item.StatusCode,
                item.StartsAtUtc,
                item.EndsAtUtc,
                item.Plan.MonthlyQuotaMinutes,
                item.Plan.MaxVideoMinutes,
                item.Plan.FeaturesJson,
                item.Plan.IsActive,
                item.CreatedAtUtc,
                item.UpdatedAtUtc))
            .Take(100)
            .ToArrayAsync(cancellationToken);
        var effectiveSubscription = subscriptions.FirstOrDefault(item =>
            item.Status == "ACTIVE"
            && item.PlanIsActive
            && item.StartsAtUtc <= nowUtc
            && (item.EndsAtUtc == null || item.EndsAtUtc > nowUtc));
        var freePlan = effectiveSubscription is null
            ? await database.ServicePlans.AsNoTracking()
                .Where(item => item.PlanCode == "FREE" && item.IsActive)
                .Select(item => new AdminUserSubscriptionItem(
                    Guid.Empty,
                    item.PlanCode,
                    item.DisplayName,
                    "ACTIVE",
                    nowUtc,
                    null,
                    item.MonthlyQuotaMinutes,
                    item.MaxVideoMinutes,
                    item.FeaturesJson,
                    item.IsActive,
                    item.CreatedAtUtc,
                    item.UpdatedAtUtc))
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var currentPlan = effectiveSubscription ?? freePlan
            ?? throw new InvalidOperationException("Gói FREE chưa được khởi tạo trong database.");

        var usedMinutes = await database.UsageRecords.AsNoTracking()
            .Where(item => item.UserId == userId
                && item.OccurredAtUtc >= periodStartUtc
                && item.OccurredAtUtc < periodEndUtc
                && item.OperationCode == "MEDIA_PROCESSING"
                && item.UnitCode == "MINUTE")
            .SumAsync(item => (decimal?)item.Quantity, cancellationToken) ?? 0;
        var heldMinutes = await database.UsageReservations.AsNoTracking()
            .Where(item => item.UserId == userId
                && item.QuotaPeriodStartUtc == periodStartUtc
                && item.StatusCode == "HELD"
                && item.ExpiresAtUtc > nowUtc)
            .SumAsync(item => (decimal?)item.EstimatedMinutes, cancellationToken) ?? 0;
        var effectiveMinuteLimit = user.CustomMonthlyQuotaMinutes ?? currentPlan.MonthlyQuotaMinutes;
        var minuteUsage = new AdminUserMinuteUsage(
            effectiveMinuteLimit,
            currentPlan.MonthlyQuotaMinutes,
            user.CustomMonthlyQuotaMinutes,
            usedMinutes,
            heldMinutes,
            effectiveMinuteLimit is null
                ? null
                : Math.Max(0, effectiveMinuteLimit.Value - usedMinutes - heldMinutes),
            currentPlan.MaxVideoMinutes);

        var cloudLimit = await database.CloudQuotaLimits.AsNoTracking()
            .Where(item => item.UserId == userId && item.UnitCode == "LLM_TOKEN")
            .Select(item => (decimal?)item.MonthlyLimit)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var cloudTotals = await database.CloudUsageLedger.AsNoTracking()
            .Where(item => item.UserId == userId
                && item.UnitCode == "LLM_TOKEN"
                && item.QuotaPeriodStartUtc == periodStartUtc)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Input = group.Sum(item => item.InputUnits),
                Output = group.Sum(item => item.OutputUnits),
                Cached = group.Sum(item => item.CachedInputUnits),
                Total = group.Sum(item => item.TotalUnits),
                Requests = group.Sum(item => item.ApiRequestCount),
                Retries = group.Sum(item => item.RetryRequestCount),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var heldTokens = await database.CloudUsageReservations.AsNoTracking()
            .Where(item => item.UserId == userId
                && item.UnitCode == "LLM_TOKEN"
                && item.QuotaPeriodStartUtc == periodStartUtc
                && item.StatusCode == "HELD"
                && item.ExpiresAtUtc > nowUtc)
            .SumAsync(item => (decimal?)item.ReservedUnits, cancellationToken) ?? 0;
        var cloudUsage = new AdminUserCloudUsage(
            ToLong(cloudLimit),
            ToLong(cloudTotals?.Input ?? 0),
            ToLong(cloudTotals?.Output ?? 0),
            ToLong(cloudTotals?.Cached ?? 0),
            ToLong(cloudTotals?.Total ?? 0),
            ToLong(heldTokens),
            Math.Max(0, ToLong(cloudLimit - (cloudTotals?.Total ?? 0) - heldTokens)),
            cloudTotals?.Requests ?? 0,
            cloudTotals?.Retries ?? 0);

        var minuteReservations = await database.UsageReservations.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => new AdminUserMinuteReservationItem(
                item.ReservationId,
                item.FeatureCode,
                item.StatusCode,
                item.EstimatedMinutes,
                item.CommittedMinutes,
                item.CreatedAtUtc,
                item.ExpiresAtUtc))
            .Take(25)
            .ToArrayAsync(cancellationToken);
        var assignedCredentials = await database.CloudProviderCredentials.AsNoTracking()
            .Where(item => item.AssignedUserId == userId)
            .OrderBy(item => item.StatusCode == "ACTIVE" ? 0 : 1)
            .ThenBy(item => item.ProviderCode)
            .ThenBy(item => item.Priority)
            .ThenBy(item => item.DisplayName)
            .Select(item => new AdminUserCredentialItem(
                item.CredentialId,
                item.ProviderCode,
                item.DisplayName,
                item.KeySuffix,
                item.StatusCode,
                item.Priority,
                item.LastIssuedAtUtc,
                item.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);
        var cloudLedger = await (
            from ledger in database.CloudUsageLedger.AsNoTracking()
            join credential in database.CloudProviderCredentials.AsNoTracking()
                on ledger.CredentialId equals credential.CredentialId
            where ledger.UserId == userId
            orderby ledger.OccurredAtUtc descending
            select new AdminUserCloudLedgerItem(
                ledger.LedgerId,
                credential.CredentialId,
                credential.DisplayName,
                credential.KeySuffix,
                credential.StatusCode,
                credential.AssignedUserId == userId,
                ledger.ProviderCode,
                ledger.ModelId,
                ledger.OperationCode,
                ledger.InputUnits,
                ledger.OutputUnits,
                ledger.CachedInputUnits,
                ledger.TotalUnits,
                ledger.ApiRequestCount,
                ledger.RetryRequestCount,
                ledger.ProviderRequestId,
                ledger.OccurredAtUtc))
            .Take(40)
            .ToArrayAsync(cancellationToken);
        var sessions = await database.AuthSessions.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.LastSeenAtUtc)
            .Select(item => new AdminUserSessionItem(
                item.SessionId,
                item.DeviceName,
                item.DeviceId,
                item.AppVersion,
                item.IpAddress,
                item.CreatedAtUtc,
                item.LastSeenAtUtc,
                item.ExpiresAtUtc,
                item.RevokedAtUtc,
                item.RevokeReason))
            .Take(30)
            .ToArrayAsync(cancellationToken);
        var audits = await database.SecurityAuditLogs.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => new AdminUserAuditItem(
                item.AuditLogId,
                item.EventCode,
                item.OutcomeCode,
                item.IpAddress,
                item.DeviceId,
                item.DetailsJson,
                item.CreatedAtUtc))
            .Take(40)
            .ToArrayAsync(cancellationToken);

        return new AdminUserDetail(
            user,
            currentPlan with { FeaturesJson = "[]" },
            ParseFeatures(currentPlan.FeaturesJson),
            subscriptions,
            minuteUsage,
            cloudUsage,
            minuteReservations,
            assignedCredentials,
            cloudLedger,
            sessions,
            audits,
            periodStartUtc,
            periodEndUtc);
    }

    public async Task SetAccountStatusAsync(
        Guid actorAdminId,
        Guid userId,
        string statusCode,
        string? reason,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var status = statusCode.Trim().ToUpperInvariant();
        if (status is not ("ACTIVE" or "SUSPENDED"))
        {
            throw new InvalidOperationException("Trạng thái tài khoản không hợp lệ.");
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var user = await database.Users.SingleOrDefaultAsync(
            item => item.UserId == userId && item.DeletedAtUtc == null,
            cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy người dùng.");
        if (user.UserId == actorAdminId || user.RoleCode == "ADMIN")
        {
            throw new InvalidOperationException("Không thể thay đổi trạng thái tài khoản quản trị tại màn hình này.");
        }

        var previousStatus = user.StatusCode;
        user.StatusCode = status;
        user.UpdatedAtUtc = DateTime.UtcNow;
        if (status == "SUSPENDED")
        {
            await RevokeActiveSessionsAsync(userId, "ADMIN_ACCOUNT_SUSPENDED", cancellationToken);
        }

        AddAudit(userId, actorAdminId, "ADMIN_USER_STATUS_CHANGE", ipAddress, new
        {
            previousStatus,
            newStatus = status,
            reason = CleanReason(reason),
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> RevokeSessionsAsync(
        Guid actorAdminId,
        Guid userId,
        string? reason,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var target = await database.Users.AsNoTracking().SingleOrDefaultAsync(
            item => item.UserId == userId && item.DeletedAtUtc == null,
            cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy người dùng.");
        if (target.UserId == actorAdminId)
        {
            throw new InvalidOperationException("Không thể thu hồi chính phiên quản trị đang sử dụng.");
        }

        var revoked = await RevokeActiveSessionsAsync(
            userId,
            CleanReason(reason) ?? "ADMIN_REVOKED",
            cancellationToken);
        AddAudit(userId, actorAdminId, "ADMIN_USER_SESSIONS_REVOKE", ipAddress, new
        {
            revokedSessions = revoked,
            reason = CleanReason(reason),
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return revoked;
    }

    public async Task SetMinuteQuotaAsync(
        Guid actorAdminId,
        Guid userId,
        decimal? monthlyQuotaMinutes,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (monthlyQuotaMinutes is < 0 or > 1_000_000)
        {
            throw new InvalidOperationException("Hạn mức phải từ 0 đến 1.000.000 phút hoặc để trống để dùng theo gói.");
        }

        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var user = await database.Users.SingleOrDefaultAsync(
            item => item.UserId == userId && item.DeletedAtUtc == null,
            cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy người dùng.");
        var previousQuota = user.MonthlyQuotaMinutes;
        user.MonthlyQuotaMinutes = monthlyQuotaMinutes;
        user.UpdatedAtUtc = DateTime.UtcNow;
        AddAudit(userId, actorAdminId, "ADMIN_USER_MINUTE_QUOTA_CHANGE", ipAddress, new
        {
            previousQuota,
            monthlyQuotaMinutes,
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<AdminUserListItem>> BuildListItemsAsync(
        IReadOnlyList<AdminUserBase> users,
        DateTime nowUtc,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        CancellationToken cancellationToken)
    {
        if (users.Count == 0)
        {
            return [];
        }

        var ids = users.Select(item => item.UserId).ToArray();
        var subscriptions = await database.UserSubscriptions.AsNoTracking()
            .Where(item => ids.Contains(item.UserId)
                && item.StatusCode == "ACTIVE"
                && item.StartsAtUtc <= nowUtc
                && (item.EndsAtUtc == null || item.EndsAtUtc > nowUtc)
                && item.Plan.IsActive)
            .OrderByDescending(item => item.StartsAtUtc)
            .Select(item => new
            {
                item.UserId,
                item.StartsAtUtc,
                item.EndsAtUtc,
                item.Plan.PlanCode,
                item.Plan.DisplayName,
                item.Plan.MonthlyQuotaMinutes,
            })
            .ToArrayAsync(cancellationToken);
        var currentSubscriptions = subscriptions
            .GroupBy(item => item.UserId)
            .ToDictionary(group => group.Key, group => group.First());
        var freePlan = await database.ServicePlans.AsNoTracking()
            .Where(item => item.PlanCode == "FREE" && item.IsActive)
            .Select(item => new { item.PlanCode, item.DisplayName, item.MonthlyQuotaMinutes })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Gói FREE chưa được khởi tạo trong database.");
        var usedMinutes = await database.UsageRecords.AsNoTracking()
            .Where(item => ids.Contains(item.UserId)
                && item.OccurredAtUtc >= periodStartUtc
                && item.OccurredAtUtc < periodEndUtc
                && item.OperationCode == "MEDIA_PROCESSING"
                && item.UnitCode == "MINUTE")
            .GroupBy(item => item.UserId)
            .Select(group => new { UserId = group.Key, Value = group.Sum(item => item.Quantity) })
            .ToDictionaryAsync(item => item.UserId, item => item.Value, cancellationToken);
        var heldMinutes = await database.UsageReservations.AsNoTracking()
            .Where(item => ids.Contains(item.UserId)
                && item.QuotaPeriodStartUtc == periodStartUtc
                && item.StatusCode == "HELD"
                && item.ExpiresAtUtc > nowUtc)
            .GroupBy(item => item.UserId)
            .Select(group => new { UserId = group.Key, Value = group.Sum(item => item.EstimatedMinutes) })
            .ToDictionaryAsync(item => item.UserId, item => item.Value, cancellationToken);
        var cloudLimits = await database.CloudQuotaLimits.AsNoTracking()
            .Where(item => ids.Contains(item.UserId) && item.UnitCode == "LLM_TOKEN")
            .ToDictionaryAsync(item => item.UserId, item => item.MonthlyLimit, cancellationToken);
        var usedTokens = await database.CloudUsageLedger.AsNoTracking()
            .Where(item => ids.Contains(item.UserId)
                && item.UnitCode == "LLM_TOKEN"
                && item.QuotaPeriodStartUtc == periodStartUtc)
            .GroupBy(item => item.UserId)
            .Select(group => new { UserId = group.Key, Value = group.Sum(item => item.TotalUnits) })
            .ToDictionaryAsync(item => item.UserId, item => item.Value, cancellationToken);
        var heldTokens = await database.CloudUsageReservations.AsNoTracking()
            .Where(item => ids.Contains(item.UserId)
                && item.UnitCode == "LLM_TOKEN"
                && item.QuotaPeriodStartUtc == periodStartUtc
                && item.StatusCode == "HELD"
                && item.ExpiresAtUtc > nowUtc)
            .GroupBy(item => item.UserId)
            .Select(group => new { UserId = group.Key, Value = group.Sum(item => item.ReservedUnits) })
            .ToDictionaryAsync(item => item.UserId, item => item.Value, cancellationToken);
        var activeSessions = await database.AuthSessions.AsNoTracking()
            .Where(item => ids.Contains(item.UserId)
                && item.RevokedAtUtc == null
                && item.ExpiresAtUtc > nowUtc)
            .GroupBy(item => item.UserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);

        return users.Select(user =>
        {
            currentSubscriptions.TryGetValue(user.UserId, out var subscription);
            var planQuota = subscription?.MonthlyQuotaMinutes ?? freePlan.MonthlyQuotaMinutes;
            var effectiveMinuteLimit = user.CustomMonthlyQuotaMinutes ?? planQuota;
            var minutesUsed = usedMinutes.GetValueOrDefault(user.UserId);
            var minutesHeld = heldMinutes.GetValueOrDefault(user.UserId);
            var tokenLimit = cloudLimits.GetValueOrDefault(user.UserId);
            var tokensUsed = usedTokens.GetValueOrDefault(user.UserId);
            var tokensHeld = heldTokens.GetValueOrDefault(user.UserId);
            return new AdminUserListItem(
                user,
                subscription?.PlanCode ?? freePlan.PlanCode,
                subscription?.DisplayName ?? freePlan.DisplayName,
                subscription?.StartsAtUtc,
                subscription?.EndsAtUtc,
                effectiveMinuteLimit,
                minutesUsed,
                minutesHeld,
                ToLong(tokenLimit),
                ToLong(tokensUsed),
                ToLong(tokensHeld),
                Math.Max(0, ToLong(tokenLimit - tokensUsed - tokensHeld)),
                activeSessions.GetValueOrDefault(user.UserId));
        }).ToArray();
    }

    private async Task EnsureAdminAsync(Guid actorAdminId, CancellationToken cancellationToken)
    {
        var valid = await database.Users.AsNoTracking().AnyAsync(
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

    private Task<int> RevokeActiveSessionsAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        return database.AuthSessions
            .Where(item => item.UserId == userId
                && item.RevokedAtUtc == null
                && item.ExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.RevokedAtUtc, nowUtc)
                .SetProperty(item => item.RevokeReason, reason), cancellationToken);
    }

    private void AddAudit(
        Guid targetUserId,
        Guid actorAdminId,
        string eventCode,
        string? ipAddress,
        object details) => database.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            UserId = targetUserId,
            EventCode = eventCode,
            OutcomeCode = "SUCCESS",
            IpAddress = ipAddress,
            DeviceId = "WEB_ADMIN",
            DetailsJson = JsonSerializer.Serialize(new { actorAdminId, details }),
            CreatedAtUtc = DateTime.UtcNow,
        });

    private static string? CleanReason(string? reason)
    {
        var value = reason?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, 180)];
    }

    private static IReadOnlyList<string> ParseFeatures(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTime MonthStart(DateTime value) =>
        new(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static long ToLong(decimal value) =>
        decimal.ToInt64(decimal.Truncate(value));
}

public sealed record AdminUserListQuery(
    string? Search,
    string? Status,
    string? PlanCode,
    string? Sort,
    int Page,
    int PageSize);

public sealed record AdminUserOverview(int Total, int Active, int Suspended, int NewThisMonth);

public sealed record AdminUserPlanOption(string Code, string DisplayName);

public sealed record AdminUserBase(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    string Status,
    bool EmailConfirmed,
    decimal? CustomMonthlyQuotaMinutes,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc);

public sealed record AdminUserListItem(
    AdminUserBase User,
    string PlanCode,
    string PlanDisplayName,
    DateTime? PlanStartsAtUtc,
    DateTime? PlanEndsAtUtc,
    decimal? MinuteLimit,
    decimal UsedMinutes,
    decimal HeldMinutes,
    long TokenLimit,
    long UsedTokens,
    long HeldTokens,
    long RemainingTokens,
    int ActiveSessions);

public sealed record AdminUserListResult(
    AdminUserOverview Overview,
    IReadOnlyList<AdminUserListItem> Items,
    IReadOnlyList<AdminUserPlanOption> Plans,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages,
    DateTime PeriodStartsAtUtc,
    DateTime PeriodEndsAtUtc);

public sealed record AdminUserSubscriptionItem(
    Guid SubscriptionId,
    string PlanCode,
    string PlanDisplayName,
    string Status,
    DateTime StartsAtUtc,
    DateTime? EndsAtUtc,
    decimal? MonthlyQuotaMinutes,
    decimal? MaxVideoMinutes,
    string FeaturesJson,
    bool PlanIsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record AdminUserMinuteUsage(
    decimal? EffectiveLimit,
    decimal? PlanLimit,
    decimal? CustomLimit,
    decimal Used,
    decimal Held,
    decimal? Remaining,
    decimal? MaxVideoMinutes);

public sealed record AdminUserCloudUsage(
    long Limit,
    long Input,
    long Output,
    long CachedInput,
    long Used,
    long Held,
    long Remaining,
    int Requests,
    int Retries);

public sealed record AdminUserMinuteReservationItem(
    Guid ReservationId,
    string FeatureCode,
    string Status,
    decimal EstimatedMinutes,
    decimal? CommittedMinutes,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public sealed record AdminUserCredentialItem(
    Guid CredentialId,
    string ProviderCode,
    string DisplayName,
    string KeySuffix,
    string Status,
    int Priority,
    DateTime? LastIssuedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record AdminUserCloudLedgerItem(
    Guid LedgerId,
    Guid CredentialId,
    string CredentialName,
    string KeySuffix,
    string CredentialStatus,
    bool IsDedicatedCredential,
    string ProviderCode,
    string ModelId,
    string OperationCode,
    decimal InputUnits,
    decimal OutputUnits,
    decimal CachedInputUnits,
    decimal TotalUnits,
    int ApiRequests,
    int RetryRequests,
    string? ProviderRequestId,
    DateTime OccurredAtUtc);

public sealed record AdminUserSessionItem(
    Guid SessionId,
    string? DeviceName,
    string DeviceId,
    string? AppVersion,
    string? IpAddress,
    DateTime CreatedAtUtc,
    DateTime LastSeenAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    string? RevokeReason);

public sealed record AdminUserAuditItem(
    long AuditLogId,
    string EventCode,
    string OutcomeCode,
    string? IpAddress,
    string? DeviceId,
    string? DetailsJson,
    DateTime CreatedAtUtc);

public sealed record AdminUserDetail(
    AdminUserBase User,
    AdminUserSubscriptionItem CurrentPlan,
    IReadOnlyList<string> Features,
    IReadOnlyList<AdminUserSubscriptionItem> Subscriptions,
    AdminUserMinuteUsage MinuteUsage,
    AdminUserCloudUsage CloudUsage,
    IReadOnlyList<AdminUserMinuteReservationItem> MinuteReservations,
    IReadOnlyList<AdminUserCredentialItem> AssignedCredentials,
    IReadOnlyList<AdminUserCloudLedgerItem> CloudLedger,
    IReadOnlyList<AdminUserSessionItem> Sessions,
    IReadOnlyList<AdminUserAuditItem> Audits,
    DateTime PeriodStartsAtUtc,
    DateTime PeriodEndsAtUtc);

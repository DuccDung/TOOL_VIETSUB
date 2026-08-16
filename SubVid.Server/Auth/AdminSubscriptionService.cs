using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Auth;

public sealed class AdminSubscriptionService(SubVidDbContext database)
{
    public async Task<IReadOnlyList<AdminPlanOption>> GetPlansAsync(
        CancellationToken cancellationToken) =>
        await database.ServicePlans
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.MonthlyQuotaMinutes)
            .ThenBy(item => item.DisplayName)
            .Select(item => new AdminPlanOption(
                item.PlanCode,
                item.DisplayName,
                item.Description,
                item.MonthlyQuotaMinutes,
                item.MaxVideoMinutes))
            .ToArrayAsync(cancellationToken);

    public async Task<AdminSubscriptionAccount?> FindByEmailAsync(
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

        var nowUtc = DateTime.UtcNow;
        var subscription = await database.UserSubscriptions
            .AsNoTracking()
            .Include(item => item.Plan)
            .Where(item => item.UserId == user.UserId
                && item.StatusCode == "ACTIVE"
                && item.StartsAtUtc <= nowUtc
                && (item.EndsAtUtc == null || item.EndsAtUtc > nowUtc)
                && item.Plan.IsActive)
            .OrderByDescending(item => item.StartsAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var plan = subscription?.Plan ?? await database.ServicePlans
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.PlanCode == "FREE" && item.IsActive, cancellationToken);
        if (plan is null)
        {
            throw new InvalidOperationException("Gói FREE chưa được khởi tạo trong database.");
        }

        return new AdminSubscriptionAccount(
            user.UserId,
            user.Email,
            user.DisplayName,
            user.RoleCode,
            user.StatusCode,
            plan.PlanCode,
            plan.DisplayName,
            subscription?.StartsAtUtc,
            subscription?.EndsAtUtc,
            user.MonthlyQuotaMinutes ?? plan.MonthlyQuotaMinutes,
            user.MonthlyQuotaMinutes,
            plan.MaxVideoMinutes);
    }

    public async Task<AdminSubscriptionAccount> ChangePlanAsync(
        Guid actorAdminId,
        string email,
        string planCode,
        int? durationDays,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var normalizedPlanCode = planCode.Trim().ToUpperInvariant();
        if (durationDays is < 1 or > 3650)
        {
            throw new InvalidOperationException("Thời hạn gói phải từ 1 đến 3650 ngày.");
        }

        var nowUtc = DateTime.UtcNow;
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var actorIsAdmin = await database.Users.AnyAsync(
            item => item.UserId == actorAdminId
                && item.RoleCode == "ADMIN"
                && item.StatusCode == "ACTIVE"
                && item.DeletedAtUtc == null,
            cancellationToken);
        if (!actorIsAdmin)
        {
            throw new UnauthorizedAccessException("Phiên quản trị không còn hợp lệ.");
        }

        var user = await database.Users.SingleOrDefaultAsync(
            item => item.EmailNormalized == normalizedEmail && item.DeletedAtUtc == null,
            cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy tài khoản với email này.");
        var plan = await database.ServicePlans.SingleOrDefaultAsync(
            item => item.PlanCode == normalizedPlanCode && item.IsActive,
            cancellationToken)
            ?? throw new InvalidOperationException("Gói dịch vụ không tồn tại hoặc đã ngừng hoạt động.");

        var activeSubscriptions = await database.UserSubscriptions
            .Include(item => item.Plan)
            .Where(item => item.UserId == user.UserId
                && item.StatusCode == "ACTIVE"
                && item.StartsAtUtc <= nowUtc
                && (item.EndsAtUtc == null || item.EndsAtUtc > nowUtc))
            .ToArrayAsync(cancellationToken);
        var previousPlans = activeSubscriptions
            .Select(item => item.Plan.PlanCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var subscription in activeSubscriptions)
        {
            subscription.StatusCode = "CANCELLED";
            subscription.EndsAtUtc = nowUtc > subscription.StartsAtUtc
                ? nowUtc
                : subscription.StartsAtUtc.AddMilliseconds(1);
            subscription.UpdatedAtUtc = nowUtc;
        }

        DateTime? endsAtUtc = durationDays.HasValue ? nowUtc.AddDays(durationDays.Value) : null;
        database.UserSubscriptions.Add(new UserSubscription
        {
            SubscriptionId = Guid.NewGuid(),
            UserId = user.UserId,
            PlanId = plan.PlanId,
            StatusCode = "ACTIVE",
            StartsAtUtc = nowUtc,
            EndsAtUtc = endsAtUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        });
        user.UpdatedAtUtc = nowUtc;

        database.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            UserId = user.UserId,
            EventCode = "ADMIN_SUBSCRIPTION_CHANGE",
            OutcomeCode = "SUCCESS",
            IpAddress = ipAddress,
            DeviceId = "WEB_ADMIN",
            DetailsJson = JsonSerializer.Serialize(new
            {
                actorAdminId,
                previousPlans,
                newPlanCode = plan.PlanCode,
                durationDays,
                startsAtUtc = nowUtc,
                endsAtUtc,
            }),
            CreatedAtUtc = nowUtc,
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await FindByEmailAsync(user.Email, cancellationToken)
            ?? throw new InvalidOperationException("Không thể tải lại tài khoản vừa cập nhật.");
    }
}

public sealed record AdminPlanOption(
    string Code,
    string DisplayName,
    string? Description,
    decimal? MonthlyQuotaMinutes,
    decimal? MaxVideoMinutes);

public sealed record AdminSubscriptionAccount(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    string Status,
    string PlanCode,
    string PlanDisplayName,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc,
    decimal? EffectiveMonthlyQuotaMinutes,
    decimal? CustomMonthlyQuotaMinutes,
    decimal? MaxVideoMinutes);

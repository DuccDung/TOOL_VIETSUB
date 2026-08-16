using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Contracts;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Auth;

public sealed class EntitlementService(SubVidDbContext database)
{
    public async Task<EntitlementsResponse?> GetAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(
            item => item.UserId == userId && item.DeletedAtUtc == null,
            cancellationToken);
        if (user is null)
        {
            return null;
        }

        var subscription = await database.UserSubscriptions
            .AsNoTracking()
            .Include(item => item.Plan)
            .Where(item => item.UserId == userId
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
            throw new InvalidOperationException("The FREE service plan has not been deployed.");
        }

        var periodStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = periodStart.AddMonths(1);
        var usedMinutes = await database.UsageRecords
            .AsNoTracking()
            .Where(item => item.UserId == userId
                && item.OccurredAtUtc >= periodStart
                && item.OccurredAtUtc < periodEnd
                && item.OperationCode == "MEDIA_PROCESSING"
                && item.UnitCode == "MINUTE")
            .SumAsync(item => (decimal?)item.Quantity, cancellationToken) ?? 0;
        var monthlyQuota = user.MonthlyQuotaMinutes ?? plan.MonthlyQuotaMinutes;
        var reservedMinutes = await database.UsageReservations
            .AsNoTracking()
            .Where(item => item.UserId == userId
                && item.QuotaPeriodStartUtc == periodStart
                && item.StatusCode == "HELD"
                && item.ExpiresAtUtc > nowUtc)
            .SumAsync(item => (decimal?)item.EstimatedMinutes, cancellationToken) ?? 0;
        decimal? remaining = monthlyQuota is null
            ? null
            : Math.Max(0, monthlyQuota.Value - usedMinutes - reservedMinutes);

        return new EntitlementsResponse(
            new PlanResponse(
                plan.PlanCode,
                plan.DisplayName,
                plan.Description,
                subscription?.StatusCode ?? "ACTIVE",
                subscription?.StartsAtUtc,
                subscription?.EndsAtUtc),
            new QuotaResponse(
                monthlyQuota,
                usedMinutes,
                reservedMinutes,
                remaining,
                plan.MaxVideoMinutes,
                periodStart,
                periodEnd),
            ParseFeatures(plan.FeaturesJson),
            nowUtc);
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
}

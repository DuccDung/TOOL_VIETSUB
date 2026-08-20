using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Cloud;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Auth;

public sealed class AdminPlanService(SubVidDbContext database)
{
    private static readonly HashSet<string> Providers =
        new(StringComparer.OrdinalIgnoreCase) { "openai", "gemini", "deepseek", "groq" };

    public async Task<IReadOnlyList<AdminServicePlan>> GetPlansAsync(
        CancellationToken cancellationToken)
    {
        var plans = await database.ServicePlans.AsNoTracking()
            .Include(item => item.CloudPolicies)
            .Include(item => item.CloudPoolLinks)
                .ThenInclude(link => link.Pool)
            .OrderBy(item => item.PriceAmount)
            .ThenBy(item => item.DisplayName)
            .ToArrayAsync(cancellationToken);
        return plans.Select(plan => new AdminServicePlan(
            plan.PlanId,
            plan.PlanCode,
            plan.DisplayName,
            plan.Description,
            plan.MonthlyQuotaMinutes,
            plan.MaxVideoMinutes,
            plan.PriceAmount,
            plan.CurrencyCode,
            plan.BillingPeriodDays,
            plan.IsPublic,
            plan.IsActive,
            ParseFeatures(plan.FeaturesJson),
            plan.CloudPolicies.OrderBy(item => item.ProviderCode).Select(policy =>
                new AdminServicePlanCloudPolicy(
                    policy.ProviderCode,
                    policy.AllocationMode,
                    decimal.ToInt64(policy.MonthlyTokenLimit),
                    ParseModels(policy.AllowedModelsJson),
                    policy.AllowSharedFallback,
                    policy.IsActive,
                    plan.CloudPoolLinks
                        .Where(link => link.Pool.ProviderCode == policy.ProviderCode)
                        .Select(link => link.Pool.DisplayName)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .ToArray()))
            .ToArray();
    }

    public async Task SavePlanAsync(
        Guid actorAdminId,
        Guid planId,
        decimal? monthlyQuotaMinutes,
        decimal? maxVideoMinutes,
        decimal priceAmount,
        string currencyCode,
        int billingPeriodDays,
        bool isPublic,
        string? features,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (monthlyQuotaMinutes is < 0 or > 1_000_000
            || maxVideoMinutes is < 1 or > 10_000
            || priceAmount is < 0 or > 1_000_000_000_000m
            || billingPeriodDays is < 1 or > 3650)
        {
            throw new InvalidOperationException("Thông số gói dịch vụ nằm ngoài giới hạn cho phép.");
        }
        var currency = currencyCode.Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsLetter))
        {
            throw new InvalidOperationException("Mã tiền tệ phải gồm đúng 3 chữ cái.");
        }

        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var plan = await database.ServicePlans.SingleOrDefaultAsync(
            item => item.PlanId == planId,
            cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy gói dịch vụ.");
        plan.MonthlyQuotaMinutes = monthlyQuotaMinutes;
        plan.MaxVideoMinutes = maxVideoMinutes;
        plan.PriceAmount = priceAmount;
        plan.CurrencyCode = currency;
        plan.BillingPeriodDays = billingPeriodDays;
        plan.IsPublic = isPublic;
        plan.FeaturesJson = JsonSerializer.Serialize(ParseList(features));
        plan.UpdatedAtUtc = DateTime.UtcNow;
        AddAudit(actorAdminId, "ADMIN_PLAN_UPDATE", ipAddress, new
        {
            plan.PlanId,
            plan.PlanCode,
            plan.MonthlyQuotaMinutes,
            plan.MaxVideoMinutes,
            plan.PriceAmount,
            plan.CurrencyCode,
            plan.BillingPeriodDays,
            plan.IsPublic,
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task SavePolicyAsync(
        Guid actorAdminId,
        Guid planId,
        string providerCode,
        string allocationMode,
        long monthlyTokenLimit,
        string? allowedModels,
        bool allowSharedFallback,
        bool isActive,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var provider = providerCode.Trim().ToLowerInvariant();
        var mode = allocationMode.Trim().ToUpperInvariant();
        if (!Providers.Contains(provider))
        {
            throw new InvalidOperationException("Nhà cung cấp Cloud không hợp lệ.");
        }
        if (mode is not (CloudCredentialAllocationModes.Shared or CloudCredentialAllocationModes.Dedicated))
        {
            throw new InvalidOperationException("Chế độ key của gói không hợp lệ.");
        }
        if (monthlyTokenLimit is < 0 or > 1_000_000_000_000)
        {
            throw new InvalidOperationException("Hạn mức token nằm ngoài giới hạn cho phép.");
        }
        var models = ParseList(allowedModels);
        if (models.Length == 0)
        {
            throw new InvalidOperationException("Hãy cấu hình ít nhất một model hoặc ký tự *.");
        }

        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var policy = await database.ServicePlanCloudPolicies.SingleOrDefaultAsync(
            item => item.PlanId == planId && item.ProviderCode == provider,
            cancellationToken);
        if (policy is null)
        {
            var planExists = await database.ServicePlans.AnyAsync(
                item => item.PlanId == planId,
                cancellationToken);
            if (!planExists)
            {
                throw new InvalidOperationException("Không tìm thấy gói dịch vụ.");
            }
            policy = new ServicePlanCloudPolicy
            {
                PlanId = planId,
                ProviderCode = provider,
                CreatedAtUtc = DateTime.UtcNow,
            };
            database.ServicePlanCloudPolicies.Add(policy);
        }

        policy.AllocationMode = mode;
        policy.MonthlyTokenLimit = monthlyTokenLimit;
        policy.AllowedModelsJson = JsonSerializer.Serialize(models);
        policy.AllowSharedFallback = allowSharedFallback;
        policy.IsActive = isActive;
        policy.UpdatedAtUtc = DateTime.UtcNow;
        AddAudit(actorAdminId, "ADMIN_PLAN_CLOUD_POLICY_UPDATE", ipAddress, new
        {
            planId,
            provider,
            mode,
            monthlyTokenLimit,
            models,
            allowSharedFallback,
            isActive,
        });
        await database.SaveChangesAsync(cancellationToken);
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

    private void AddAudit(
        Guid actorAdminId,
        string eventCode,
        string? ipAddress,
        object details) => database.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            UserId = actorAdminId,
            EventCode = eventCode,
            OutcomeCode = "SUCCESS",
            IpAddress = ipAddress,
            DeviceId = "WEB_ADMIN",
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAtUtc = DateTime.UtcNow,
        });

    private static string[] ParseList(string? value) =>
        (value ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> ParseFeatures(string json) => ParseJsonList(json);

    private static IReadOnlyList<string> ParseModels(string json) => ParseJsonList(json);

    private static IReadOnlyList<string> ParseJsonList(string json)
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

public sealed record AdminServicePlan(
    Guid PlanId,
    string PlanCode,
    string DisplayName,
    string? Description,
    decimal? MonthlyQuotaMinutes,
    decimal? MaxVideoMinutes,
    decimal PriceAmount,
    string CurrencyCode,
    int BillingPeriodDays,
    bool IsPublic,
    bool IsActive,
    IReadOnlyList<string> Features,
    IReadOnlyList<AdminServicePlanCloudPolicy> CloudPolicies);

public sealed record AdminServicePlanCloudPolicy(
    string ProviderCode,
    string AllocationMode,
    long MonthlyTokenLimit,
    IReadOnlyList<string> AllowedModels,
    bool AllowSharedFallback,
    bool IsActive,
    IReadOnlyList<string> PoolNames);

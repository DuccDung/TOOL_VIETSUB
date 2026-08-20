namespace SubVid.Server.Contracts;

public sealed record AccountResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    string Status,
    bool EmailConfirmed,
    DateTime CreatedAtUtc,
    DateTime? LastLoginAtUtc);

public sealed record PlanResponse(
    string Code,
    string DisplayName,
    string? Description,
    string Status,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc);

public sealed record QuotaResponse(
    decimal? MonthlyMinutes,
    decimal UsedMinutes,
    decimal ReservedMinutes,
    decimal? RemainingMinutes,
    decimal? MaxVideoMinutes,
    DateTime PeriodStartsAtUtc,
    DateTime PeriodEndsAtUtc);

public sealed record EntitlementsResponse(
    PlanResponse Plan,
    QuotaResponse Quota,
    IReadOnlyList<string> Features,
    DateTime EvaluatedAtUtc);

public sealed record PlanCatalogItemResponse(
    string Code,
    string DisplayName,
    string? Description,
    decimal PriceAmount,
    string CurrencyCode,
    int BillingPeriodDays,
    decimal? MonthlyQuotaMinutes,
    decimal? MaxVideoMinutes,
    IReadOnlyList<string> Features,
    IReadOnlyList<PlanCloudOptionResponse> CloudOptions);

public sealed record PlanCloudOptionResponse(
    string ProviderCode,
    string AllocationMode,
    long MonthlyTokenLimit,
    IReadOnlyList<string> AllowedModels,
    bool AllowSharedFallback);

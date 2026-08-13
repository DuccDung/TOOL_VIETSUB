namespace TOOL_VIETSUB.Contracts;

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

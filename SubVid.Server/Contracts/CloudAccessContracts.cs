using System.ComponentModel.DataAnnotations;

namespace SubVid.Server.Contracts;

public sealed record AuthorizeCloudAccessRequest(
    Guid RequestId,
    Guid? ProjectId,
    Guid? LocalJobId,
    [param: Required, StringLength(40)] string OperationCode,
    [param: Required, StringLength(30)] string ProviderCode,
    [param: Required, StringLength(160)] string ModelId,
    [param: Range(0, 100_000_000)] long EstimatedInputTokens,
    [param: Range(0, 100_000_000)] long EstimatedOutputTokens);

public sealed record CommitCloudUsageRequest(
    [param: Range(0, 100_000_000)] long InputTokens,
    [param: Range(0, 100_000_000)] long OutputTokens,
    [param: Range(0, 100_000_000)] long CachedInputTokens,
    [param: Range(0, 10_000)] int ApiRequests,
    [param: Range(0, 10_000)] int RetryRequests,
    [param: StringLength(200)] string? ProviderRequestId);

public sealed record CloudAuthorizationResponse(
    Guid ReservationId,
    string Status,
    string ProviderCode,
    string ModelId,
    string UnitCode,
    long ReservedUnits,
    DateTime ExpiresAtUtc,
    long MonthlyLimit,
    long UsedUnits,
    long HeldUnits,
    long RemainingUnits,
    bool Duplicate,
    string ApiKey);

public sealed record CloudReservationResponse(
    Guid ReservationId,
    string Status,
    string ProviderCode,
    string ModelId,
    string UnitCode,
    long ReservedUnits,
    long? CommittedUnits,
    DateTime ExpiresAtUtc,
    long RemainingUnits,
    bool Duplicate);

public sealed record CloudQuotaBalanceResponse(
    string UnitCode,
    long MonthlyLimit,
    long UsedUnits,
    long HeldUnits,
    long RemainingUnits,
    DateTime PeriodStartsAtUtc,
    DateTime PeriodEndsAtUtc);

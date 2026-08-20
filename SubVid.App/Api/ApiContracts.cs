using System.Text.Json.Serialization;

namespace SubVid.App.Api;

public sealed record ApiError(string Code, string Message);

public sealed record ApiEnvelope<T>(
    bool Success,
    T? Data,
    ApiError? Error,
    string TraceId);

public sealed record LoginApiRequest(
    string Email,
    string Password,
    string DeviceId,
    string DeviceName,
    string AppVersion);

public sealed record RegistrationStartApiRequest(
    string DisplayName,
    string Email,
    string Password,
    string DeviceId,
    string DeviceName,
    string AppVersion);

public sealed record RegistrationVerifyApiRequest(
    Guid ChallengeId,
    string Otp,
    string DeviceId,
    string DeviceName,
    string AppVersion);

public sealed record RegistrationResendApiRequest(
    Guid ChallengeId,
    string DeviceId);

public sealed record RegistrationChallengeResponse(
    Guid ChallengeId,
    string MaskedEmail,
    DateTime ExpiresAtUtc,
    DateTime ResendAtUtc,
    int ResendsRemaining);

public sealed record RefreshApiRequest(
    string RefreshToken,
    string DeviceId,
    string DeviceName,
    string AppVersion);

public sealed record TokenPairResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    AccountResponse Account);

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

public sealed record CreatePurchaseCheckoutApiRequest(
    string PlanCode,
    string IdempotencyKey,
    decimal ExpectedPriceAmount);

public sealed record PurchaseCheckoutResponse(
    Guid OrderId,
    string OrderNumber,
    string OrderStatus,
    string PaymentStatus,
    string PlanCode,
    string PlanName,
    string TransactionCode,
    string BankName,
    string BankShortName,
    string AccountNumber,
    string AccountName,
    string TransferContent,
    string QrImageUrl,
    decimal Amount,
    string Currency,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? PaidAtUtc,
    bool IsPaid,
    bool IsExpired,
    string Message,
    bool ReusedExistingOrder);

public sealed record UsageHistoryItem(
    Guid EventId,
    string OperationCode,
    decimal Quantity,
    string UnitCode,
    DateTime OccurredAtUtc,
    Guid? ProjectId,
    Guid? JobId);

public sealed record UsageHistoryResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<UsageHistoryItem> Items);

public sealed record UsageEventRequest(
    Guid EventId,
    string OperationCode,
    decimal Quantity,
    string UnitCode,
    Guid? ProjectId,
    Guid? JobId,
    DateTime OccurredAtUtc,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record UsageAcceptedResponse(Guid EventId, bool Duplicate);

public sealed record CreateProjectApiRequest(
    Guid ProjectId,
    string Name,
    string? SourceLanguageCode);

public sealed record RenameProjectApiRequest(string Name);

public sealed record ProjectApiResponse(
    Guid ProjectId,
    string Name,
    string Status,
    string? SourceLanguageCode,
    string TargetLanguageCode,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ReserveQuotaApiRequest(
    Guid RequestId,
    Guid? ProjectId,
    Guid? LocalJobId,
    string FeatureCode,
    decimal EstimatedMinutes);

public sealed record CommitQuotaApiRequest(decimal ActualMinutes);

public sealed record QuotaReservationApiResponse(
    Guid ReservationId,
    string Status,
    decimal EstimatedMinutes,
    decimal? CommittedMinutes,
    DateTime ExpiresAtUtc,
    decimal? RemainingMinutes,
    bool Duplicate);

public sealed record AuthorizeCloudAccessApiRequest(
    Guid RequestId,
    Guid? ProjectId,
    Guid? LocalJobId,
    string OperationCode,
    string ProviderCode,
    string ModelId,
    long EstimatedInputTokens,
    long EstimatedOutputTokens);

public sealed record CommitCloudUsageApiRequest(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    int ApiRequests,
    int RetryRequests,
    string? ProviderRequestId);

public sealed record CloudAuthorizationApiResponse(
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

public sealed record CloudReservationApiResponse(
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

public sealed record CloudQuotaBalanceApiResponse(
    string UnitCode,
    long MonthlyLimit,
    long UsedUnits,
    long HeldUnits,
    long RemainingUnits,
    DateTime PeriodStartsAtUtc,
    DateTime PeriodEndsAtUtc);

public sealed record LogoutResponse(bool Revoked);

public sealed record StoredAuthSession(
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    string DeviceId);

public sealed record DesktopAuthState(
    string Status,
    AccountResponse? Account,
    EntitlementsResponse? Entitlements,
    UsageHistoryResponse? History,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    [JsonIgnore]
    public bool IsAuthenticated => Status == "authenticated" && Account is not null;
}

public sealed record DesktopRegistrationResult(
    bool Succeeded,
    RegistrationChallengeResponse? Challenge = null,
    DesktopAuthState? AuthState = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

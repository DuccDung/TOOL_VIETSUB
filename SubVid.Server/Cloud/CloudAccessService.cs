using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubVid.Server.Auth;
using SubVid.Server.Contracts;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Cloud;

public sealed partial class CloudAccessService(
    SubVidDbContext database,
    EntitlementService entitlementService,
    CloudCredentialProtector protector,
    IOptions<CloudAccessOptions> options)
{
    private static readonly HashSet<string> SupportedProviders =
        new(StringComparer.OrdinalIgnoreCase) { "openai", "gemini", "deepseek", "groq" };
    private readonly CloudAccessOptions _options = options.Value;

    public async Task<CloudAccessResult<CloudAuthorizationResponse>> AuthorizeAsync(
        Guid userId,
        AuthorizeCloudAccessRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidateAuthorization(request);
        if (validation is not null)
        {
            return CloudAccessResult<CloudAuthorizationResponse>.Failure(validation.Value.Code, validation.Value.Message);
        }

        var providerCode = NormalizeProvider(request.ProviderCode);
        var operationCode = request.OperationCode.Trim().ToUpperInvariant();
        var modelId = request.ModelId.Trim();
        var reservedUnits = checked(request.EstimatedInputTokens + request.EstimatedOutputTokens);
        var nowUtc = DateTime.UtcNow;
        var periodStart = StartOfMonth(nowUtc);

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        await ExpireHeldAsync(userId, nowUtc, cancellationToken);

        var existing = await database.CloudUsageReservations
            .Include(item => item.Credential)
            .SingleOrDefaultAsync(
                item => item.UserId == userId && item.RequestId == request.RequestId,
                cancellationToken);
        if (existing is not null)
        {
            var sameRequest = existing.ProjectId == request.ProjectId
                && existing.LocalJobId == request.LocalJobId
                && existing.ProviderCode == providerCode
                && existing.OperationCode == operationCode
                && existing.ModelId == modelId
                && existing.EstimatedInputUnits == request.EstimatedInputTokens
                && existing.EstimatedOutputUnits == request.EstimatedOutputTokens;
            if (!sameRequest)
            {
                return CloudAccessResult<CloudAuthorizationResponse>.Failure(
                    "CLOUD_IDEMPOTENCY_CONFLICT",
                    "Mã yêu cầu Cloud đã được sử dụng cho dữ liệu khác.");
            }

            if (existing.StatusCode != "HELD" || existing.ExpiresAtUtc <= nowUtc)
            {
                return CloudAccessResult<CloudAuthorizationResponse>.Failure(
                    "CLOUD_RESERVATION_INACTIVE",
                    "Quyền gọi Cloud của yêu cầu này không còn hiệu lực.");
            }

            if (!string.Equals(existing.Credential.StatusCode, "ACTIVE", StringComparison.Ordinal))
            {
                return CloudAccessResult<CloudAuthorizationResponse>.Failure(
                    "CLOUD_CREDENTIAL_DISABLED",
                    "API key Cloud đã bị quản trị viên vô hiệu hóa.");
            }

            var duplicateBalance = await GetBalanceCoreAsync(userId, existing.UnitCode, existing.QuotaPeriodStartUtc, nowUtc, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CloudAccessResult<CloudAuthorizationResponse>.Success(new CloudAuthorizationResponse(
                existing.ReservationId,
                existing.StatusCode,
                existing.ProviderCode,
                existing.ModelId,
                existing.UnitCode,
                DecimalToLong(existing.ReservedUnits),
                existing.ExpiresAtUtc,
                duplicateBalance.MonthlyLimit,
                duplicateBalance.UsedUnits,
                duplicateBalance.HeldUnits,
                duplicateBalance.RemainingUnits,
                true,
                protector.Unprotect(existing.Credential.EncryptedApiKey)));
        }

        if (request.ProjectId is Guid projectId
            && !await database.Projects.AsNoTracking().AnyAsync(
                item => item.ProjectId == projectId
                    && item.OwnerUserId == userId
                    && item.DeletedAtUtc == null,
                cancellationToken))
        {
            return CloudAccessResult<CloudAuthorizationResponse>.Failure(
                "CLOUD_PROJECT_INVALID",
                "Dự án không thuộc tài khoản hiện tại.");
        }

        var entitlements = await entitlementService.GetAsync(userId, cancellationToken);
        if (entitlements is null)
        {
            return CloudAccessResult<CloudAuthorizationResponse>.Failure(
                "AUTH_TOKEN_INVALID",
                "Không tìm thấy tài khoản hiện tại.");
        }

        if (!entitlements.Features.Contains("subtitle.translate", StringComparer.OrdinalIgnoreCase))
        {
            return CloudAccessResult<CloudAuthorizationResponse>.Failure(
                "CLOUD_FEATURE_NOT_INCLUDED",
                "Gói hiện tại không hỗ trợ dịch thuật Cloud.");
        }

        var unitCode = ResolveUnitCode(operationCode);
        var balance = await GetBalanceCoreAsync(userId, unitCode, periodStart, nowUtc, cancellationToken);
        if (balance.MonthlyLimit <= 0)
        {
            return CloudAccessResult<CloudAuthorizationResponse>.Failure(
                "CLOUD_QUOTA_NOT_CONFIGURED",
                "Tài khoản chưa được Admin cấp hạn mức token Cloud.");
        }

        if (reservedUnits > balance.RemainingUnits)
        {
            return CloudAccessResult<CloudAuthorizationResponse>.Failure(
                "CLOUD_QUOTA_INSUFFICIENT",
                $"Tài khoản chỉ còn {balance.RemainingUnits:N0} token Cloud khả dụng.");
        }

        var credential = await database.CloudProviderCredentials
            .Where(item => item.ProviderCode == providerCode
                && item.StatusCode == "ACTIVE"
                && (item.AssignedUserId == userId || item.AssignedUserId == null))
            .OrderByDescending(item => item.AssignedUserId == userId)
            .ThenBy(item => item.Priority)
            .ThenBy(item => item.LastIssuedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (credential is null)
        {
            return CloudAccessResult<CloudAuthorizationResponse>.Failure(
                "CLOUD_CREDENTIAL_UNAVAILABLE",
                $"Admin chưa cấu hình API key hoạt động cho {providerCode}.");
        }

        var reservation = new CloudUsageReservation
        {
            ReservationId = Guid.NewGuid(),
            RequestId = request.RequestId,
            UserId = userId,
            ProjectId = request.ProjectId,
            LocalJobId = request.LocalJobId,
            CredentialId = credential.CredentialId,
            OperationCode = operationCode,
            ProviderCode = providerCode,
            ModelId = modelId,
            UnitCode = unitCode,
            StatusCode = "HELD",
            EstimatedInputUnits = request.EstimatedInputTokens,
            EstimatedOutputUnits = request.EstimatedOutputTokens,
            ReservedUnits = reservedUnits,
            QuotaPeriodStartUtc = periodStart,
            ExpiresAtUtc = nowUtc.AddMinutes(_options.ReservationLifetimeMinutes),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        credential.LastIssuedAtUtc = nowUtc;
        credential.UpdatedAtUtc = nowUtc;
        database.CloudUsageReservations.Add(reservation);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CloudAccessResult<CloudAuthorizationResponse>.Success(new CloudAuthorizationResponse(
            reservation.ReservationId,
            reservation.StatusCode,
            reservation.ProviderCode,
            reservation.ModelId,
            reservation.UnitCode,
            reservedUnits,
            reservation.ExpiresAtUtc,
            balance.MonthlyLimit,
            balance.UsedUnits,
            balance.HeldUnits + reservedUnits,
            Math.Max(0, balance.RemainingUnits - reservedUnits),
            false,
            protector.Unprotect(credential.EncryptedApiKey)));
    }

    public async Task<CloudAccessResult<CloudReservationResponse>> CommitAsync(
        Guid userId,
        Guid reservationId,
        CommitCloudUsageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.InputTokens < 0
            || request.OutputTokens < 0
            || request.CachedInputTokens < 0
            || request.InputTokens > 100_000_000
            || request.OutputTokens > 100_000_000
            || request.CachedInputTokens > 100_000_000
            || request.ApiRequests < 0
            || request.RetryRequests < 0
            || (request.InputTokens == 0 && request.OutputTokens == 0))
        {
            return CloudAccessResult<CloudReservationResponse>.Failure(
                "CLOUD_ACTUAL_USAGE_INVALID",
                "Usage thực tế phải lớn hơn 0 token.");
        }

        var totalUnits = request.InputTokens + request.OutputTokens;

        var nowUtc = DateTime.UtcNow;
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        var reservation = await database.CloudUsageReservations.SingleOrDefaultAsync(
            item => item.ReservationId == reservationId && item.UserId == userId,
            cancellationToken);
        if (reservation is null)
        {
            return CloudAccessResult<CloudReservationResponse>.Failure(
                "CLOUD_RESERVATION_NOT_FOUND",
                "Không tìm thấy lượt giữ hạn mức Cloud.");
        }

        if (reservation.StatusCode == "COMMITTED")
        {
            var duplicateBalance = await GetBalanceCoreAsync(userId, reservation.UnitCode, StartOfMonth(nowUtc), nowUtc, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CloudAccessResult<CloudReservationResponse>.Success(MapReservation(
                reservation,
                duplicateBalance.RemainingUnits,
                true));
        }

        if (reservation.StatusCode is not ("HELD" or "EXPIRED"))
        {
            return CloudAccessResult<CloudReservationResponse>.Failure(
                "CLOUD_RESERVATION_NOT_COMMITTABLE",
                "Lượt hạn mức Cloud đã được hoàn lại nên không thể chốt usage.");
        }

        reservation.StatusCode = "COMMITTED";
        reservation.CommittedUnits = totalUnits;
        reservation.ProviderRequestId = NormalizeProviderRequestId(request.ProviderRequestId);
        reservation.CommittedAtUtc = nowUtc;
        reservation.UpdatedAtUtc = nowUtc;
        if (!await database.CloudUsageLedger.AnyAsync(
            item => item.ReservationId == reservation.ReservationId,
            cancellationToken))
        {
            database.CloudUsageLedger.Add(new CloudUsageLedger
            {
                LedgerId = Guid.NewGuid(),
                ReservationId = reservation.ReservationId,
                UserId = userId,
                CredentialId = reservation.CredentialId,
                ProviderCode = reservation.ProviderCode,
                ModelId = reservation.ModelId,
                OperationCode = reservation.OperationCode,
                UnitCode = reservation.UnitCode,
                InputUnits = request.InputTokens,
                OutputUnits = request.OutputTokens,
                CachedInputUnits = Math.Min(request.CachedInputTokens, request.InputTokens),
                TotalUnits = totalUnits,
                ApiRequestCount = request.ApiRequests,
                RetryRequestCount = request.RetryRequests,
                ProviderRequestId = reservation.ProviderRequestId,
                QuotaPeriodStartUtc = reservation.QuotaPeriodStartUtc,
                OccurredAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        var balance = await GetBalanceCoreAsync(userId, reservation.UnitCode, StartOfMonth(nowUtc), nowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CloudAccessResult<CloudReservationResponse>.Success(MapReservation(
            reservation,
            balance.RemainingUnits,
            false));
    }

    public async Task<CloudAccessResult<CloudReservationResponse>> ReleaseAsync(
        Guid userId,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        var reservation = await database.CloudUsageReservations.SingleOrDefaultAsync(
            item => item.ReservationId == reservationId && item.UserId == userId,
            cancellationToken);
        if (reservation is null)
        {
            return CloudAccessResult<CloudReservationResponse>.Failure(
                "CLOUD_RESERVATION_NOT_FOUND",
                "Không tìm thấy lượt giữ hạn mức Cloud.");
        }

        var duplicate = reservation.StatusCode == "RELEASED";
        if (reservation.StatusCode is "HELD" or "EXPIRED")
        {
            reservation.StatusCode = "RELEASED";
            reservation.ReleasedAtUtc = nowUtc;
            reservation.UpdatedAtUtc = nowUtc;
            await database.SaveChangesAsync(cancellationToken);
        }
        else if (!duplicate)
        {
            return CloudAccessResult<CloudReservationResponse>.Failure(
                "CLOUD_RESERVATION_NOT_RELEASABLE",
                "Usage Cloud đã được chốt nên không thể hoàn lại.");
        }

        var balance = await GetBalanceCoreAsync(userId, reservation.UnitCode, StartOfMonth(nowUtc), nowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CloudAccessResult<CloudReservationResponse>.Success(MapReservation(
            reservation,
            balance.RemainingUnits,
            duplicate));
    }

    public async Task<CloudAccessResult<CloudReservationResponse>> GetStatusAsync(
        Guid userId,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var reservation = await database.CloudUsageReservations.AsNoTracking().SingleOrDefaultAsync(
            item => item.ReservationId == reservationId && item.UserId == userId,
            cancellationToken);
        if (reservation is null)
        {
            return CloudAccessResult<CloudReservationResponse>.Failure(
                "CLOUD_RESERVATION_NOT_FOUND",
                "Không tìm thấy lượt giữ hạn mức Cloud.");
        }

        var nowUtc = DateTime.UtcNow;
        var balance = await GetBalanceCoreAsync(userId, reservation.UnitCode, StartOfMonth(nowUtc), nowUtc, cancellationToken);
        return CloudAccessResult<CloudReservationResponse>.Success(MapReservation(
            reservation,
            balance.RemainingUnits,
            false));
    }

    public async Task<CloudQuotaBalanceResponse> GetBalanceAsync(
        Guid userId,
        string unitCode,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        return await GetBalanceCoreAsync(
            userId,
            NormalizeUnitCode(unitCode),
            StartOfMonth(nowUtc),
            nowUtc,
            cancellationToken);
    }

    private async Task<CloudQuotaBalanceResponse> GetBalanceCoreAsync(
        Guid userId,
        string unitCode,
        DateTime periodStart,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var periodEnd = periodStart.AddMonths(1);
        var limit = await database.CloudQuotaLimits.AsNoTracking()
            .Where(item => item.UserId == userId && item.UnitCode == unitCode)
            .Select(item => (decimal?)item.MonthlyLimit)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var used = await database.CloudUsageLedger.AsNoTracking()
            .Where(item => item.UserId == userId
                && item.UnitCode == unitCode
                && item.QuotaPeriodStartUtc == periodStart)
            .SumAsync(item => (decimal?)item.TotalUnits, cancellationToken) ?? 0;
        var held = await database.CloudUsageReservations.AsNoTracking()
            .Where(item => item.UserId == userId
                && item.UnitCode == unitCode
                && item.QuotaPeriodStartUtc == periodStart
                && item.StatusCode == "HELD"
                && item.ExpiresAtUtc > nowUtc)
            .SumAsync(item => (decimal?)item.ReservedUnits, cancellationToken) ?? 0;
        return new CloudQuotaBalanceResponse(
            unitCode,
            DecimalToLong(limit),
            DecimalToLong(used),
            DecimalToLong(held),
            DecimalToLong(Math.Max(0, limit - used - held)),
            periodStart,
            periodEnd);
    }

    private static (string Code, string Message)? ValidateAuthorization(AuthorizeCloudAccessRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            return ("CLOUD_REQUEST_ID_INVALID", "Mã yêu cầu Cloud không hợp lệ.");
        }

        if (!SupportedProviders.Contains(request.ProviderCode.Trim()))
        {
            return ("CLOUD_PROVIDER_UNSUPPORTED", "Nhà cung cấp Cloud chưa được hỗ trợ.");
        }

        if (!string.Equals(request.OperationCode.Trim(), "TRANSLATION", StringComparison.OrdinalIgnoreCase))
        {
            return ("CLOUD_OPERATION_UNSUPPORTED", "Tác vụ Cloud chưa được hỗ trợ trong phiên bản này.");
        }

        if (request.EstimatedInputTokens < 0
            || request.EstimatedOutputTokens < 0
            || request.EstimatedInputTokens > 100_000_000
            || request.EstimatedOutputTokens > 100_000_000
            || (request.EstimatedInputTokens == 0 && request.EstimatedOutputTokens == 0))
        {
            return ("CLOUD_ESTIMATE_INVALID", "Ước tính token Cloud không hợp lệ.");
        }

        var modelId = request.ModelId.Trim();
        if (modelId.Length is 0 or > 160 || !ModelIdRegex().IsMatch(modelId))
        {
            return ("CLOUD_MODEL_INVALID", "Tên model Cloud không hợp lệ.");
        }

        return null;
    }

    private Task LockUserAsync(Guid userId, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT user_id FROM dbo.users WITH (UPDLOCK, HOLDLOCK) WHERE user_id = {userId}",
            cancellationToken);

    private Task<int> ExpireHeldAsync(Guid userId, DateTime nowUtc, CancellationToken cancellationToken) =>
        database.CloudUsageReservations
            .Where(item => item.UserId == userId
                && item.StatusCode == "HELD"
                && item.ExpiresAtUtc <= nowUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.StatusCode, "EXPIRED")
                .SetProperty(item => item.UpdatedAtUtc, nowUtc),
                cancellationToken);

    private static CloudReservationResponse MapReservation(
        CloudUsageReservation reservation,
        long remainingUnits,
        bool duplicate) => new(
            reservation.ReservationId,
            reservation.StatusCode,
            reservation.ProviderCode,
            reservation.ModelId,
            reservation.UnitCode,
            DecimalToLong(reservation.ReservedUnits),
            reservation.CommittedUnits is null ? null : DecimalToLong(reservation.CommittedUnits.Value),
            reservation.ExpiresAtUtc,
            remainingUnits,
            duplicate);

    private static DateTime StartOfMonth(DateTime value) =>
        new(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string NormalizeProvider(string value) => value.Trim().ToLowerInvariant();

    private static string NormalizeUnitCode(string value) => value.Trim().ToUpperInvariant();

    private static string ResolveUnitCode(string operationCode) => operationCode switch
    {
        "TRANSLATION" => CloudUsageUnits.LlmToken,
        _ => throw new InvalidOperationException("Unsupported Cloud operation."),
    };

    private static string? NormalizeProviderRequestId(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static long DecimalToLong(decimal value) => decimal.ToInt64(decimal.Truncate(value));

    [GeneratedRegex("^[A-Za-z0-9._:/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdRegex();
}

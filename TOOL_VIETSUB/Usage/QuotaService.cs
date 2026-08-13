using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TOOL_VIETSUB.Auth;
using TOOL_VIETSUB.Contracts;
using TOOL_VIETSUB.Data;
using TOOL_VIETSUB.Models;

namespace TOOL_VIETSUB.Usage;

public sealed class QuotaService(
    ToolVietSubDbContext database,
    EntitlementService entitlementService,
    IOptions<QuotaOptions> options)
{
    private readonly QuotaOptions _options = options.Value;

    public async Task<QuotaServiceResult> ReserveAsync(
        Guid userId,
        ReserveQuotaRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RequestId == Guid.Empty)
        {
            return QuotaServiceResult.Failure("QUOTA_REQUEST_ID_INVALID", "Mã yêu cầu giữ hạn mức không hợp lệ.");
        }

        var feature = request.FeatureCode.Trim().ToLowerInvariant();
        if (feature.Length == 0)
        {
            return QuotaServiceResult.Failure("QUOTA_FEATURE_INVALID", "Tính năng cần sử dụng không hợp lệ.");
        }

        var nowUtc = DateTime.UtcNow;
        var periodStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        await ExpireHeldAsync(userId, nowUtc, cancellationToken);

        var key = request.RequestId.ToString("N");
        var existing = await database.UsageReservations.SingleOrDefaultAsync(
            item => item.UserId == userId && item.IdempotencyKey == key,
            cancellationToken);
        if (existing is not null)
        {
            var sameRequest = existing.ProjectId == request.ProjectId
                && existing.LocalJobId == request.LocalJobId
                && existing.FeatureCode == feature
                && existing.EstimatedMinutes == request.EstimatedMinutes;
            if (!sameRequest)
            {
                return QuotaServiceResult.Failure(
                    "QUOTA_IDEMPOTENCY_CONFLICT",
                    "Mã yêu cầu đã được sử dụng cho dữ liệu khác.");
            }

            await transaction.CommitAsync(cancellationToken);
            var remainingForDuplicate = await CalculateRemainingAsync(userId, cancellationToken);
            return QuotaServiceResult.Success(Map(existing, remainingForDuplicate, duplicate: true));
        }

        if (request.ProjectId is Guid projectId
            && !await database.Projects.AsNoTracking().AnyAsync(
                item => item.ProjectId == projectId
                    && item.OwnerUserId == userId
                    && item.DeletedAtUtc == null,
                cancellationToken))
        {
            return QuotaServiceResult.Failure("QUOTA_PROJECT_INVALID", "Dự án không thuộc tài khoản hiện tại.");
        }

        var entitlements = await entitlementService.GetAsync(userId, cancellationToken);
        if (entitlements is null)
        {
            return QuotaServiceResult.Failure("AUTH_TOKEN_INVALID", "Không tìm thấy tài khoản hiện tại.");
        }

        if (!entitlements.Features.Contains(feature, StringComparer.OrdinalIgnoreCase))
        {
            return QuotaServiceResult.Failure(
                "QUOTA_FEATURE_NOT_INCLUDED",
                "Gói hiện tại không hỗ trợ tính năng này.");
        }

        if (entitlements.Quota.MaxVideoMinutes is decimal maximum
            && request.EstimatedMinutes > maximum)
        {
            return QuotaServiceResult.Failure(
                "QUOTA_VIDEO_TOO_LONG",
                $"Video vượt giới hạn {maximum:0.##} phút của gói hiện tại.");
        }

        if (entitlements.Quota.RemainingMinutes is decimal remaining
            && request.EstimatedMinutes > remaining)
        {
            return QuotaServiceResult.Failure(
                "QUOTA_INSUFFICIENT",
                $"Tài khoản chỉ còn {remaining:0.##} phút xử lý.");
        }

        var reservation = new UsageReservation
        {
            ReservationId = Guid.NewGuid(),
            UserId = userId,
            ProjectId = request.ProjectId,
            LocalJobId = request.LocalJobId,
            FeatureCode = feature,
            StatusCode = "HELD",
            EstimatedMinutes = request.EstimatedMinutes,
            IdempotencyKey = key,
            QuotaPeriodStartUtc = periodStart,
            ExpiresAtUtc = nowUtc.AddMinutes(_options.ReservationLifetimeMinutes),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        database.UsageReservations.Add(reservation);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        decimal? remainingAfter = entitlements.Quota.RemainingMinutes is null
            ? null
            : Math.Max(0, entitlements.Quota.RemainingMinutes.Value - request.EstimatedMinutes);
        return QuotaServiceResult.Success(Map(reservation, remainingAfter, duplicate: false));
    }

    public async Task<QuotaServiceResult> CommitAsync(
        Guid userId,
        Guid reservationId,
        decimal actualMinutes,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        var reservation = await database.UsageReservations.SingleOrDefaultAsync(
            item => item.ReservationId == reservationId && item.UserId == userId,
            cancellationToken);
        if (reservation is null)
        {
            return QuotaServiceResult.Failure("QUOTA_RESERVATION_NOT_FOUND", "Không tìm thấy lượt giữ hạn mức.");
        }

        if (reservation.StatusCode == "COMMITTED")
        {
            await transaction.CommitAsync(cancellationToken);
            return QuotaServiceResult.Success(Map(
                reservation,
                await CalculateRemainingAsync(userId, cancellationToken),
                duplicate: true));
        }

        if (reservation.StatusCode != "HELD" || reservation.ExpiresAtUtc <= nowUtc)
        {
            if (reservation.StatusCode == "HELD")
            {
                reservation.StatusCode = "EXPIRED";
                reservation.UpdatedAtUtc = nowUtc;
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            return QuotaServiceResult.Failure("QUOTA_RESERVATION_INACTIVE", "Lượt giữ hạn mức không còn hiệu lực.");
        }

        if (actualMinutes <= 0 || actualMinutes > reservation.EstimatedMinutes + 0.02m)
        {
            return QuotaServiceResult.Failure(
                "QUOTA_ACTUAL_INVALID",
                "Thời lượng thực tế phải lớn hơn 0 và không vượt thời lượng đã giữ.");
        }

        reservation.StatusCode = "COMMITTED";
        reservation.CommittedMinutes = actualMinutes;
        reservation.CommittedAtUtc = nowUtc;
        reservation.UpdatedAtUtc = nowUtc;
        var externalRequestId = reservation.ReservationId.ToString("N");
        if (!await database.UsageRecords.AnyAsync(
            item => item.ProviderCode == "DESKTOP_APP"
                && item.ExternalRequestId == externalRequestId,
            cancellationToken))
        {
            database.UsageRecords.Add(new UsageRecord
            {
                UsageRecordId = Guid.NewGuid(),
                UserId = userId,
                ProjectId = reservation.ProjectId,
                ProviderCode = "DESKTOP_APP",
                OperationCode = "MEDIA_PROCESSING",
                Quantity = actualMinutes,
                UnitCode = "MINUTE",
                CurrencyCode = "USD",
                ExternalRequestId = externalRequestId,
                OccurredAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return QuotaServiceResult.Success(Map(
            reservation,
            await CalculateRemainingAsync(userId, cancellationToken),
            duplicate: false));
    }

    public async Task<QuotaServiceResult> ReleaseAsync(
        Guid userId,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        var reservation = await database.UsageReservations.SingleOrDefaultAsync(
            item => item.ReservationId == reservationId && item.UserId == userId,
            cancellationToken);
        if (reservation is null)
        {
            return QuotaServiceResult.Failure("QUOTA_RESERVATION_NOT_FOUND", "Không tìm thấy lượt giữ hạn mức.");
        }

        var duplicate = reservation.StatusCode == "RELEASED";
        if (reservation.StatusCode == "HELD")
        {
            reservation.StatusCode = "RELEASED";
            reservation.ReleasedAtUtc = nowUtc;
            reservation.UpdatedAtUtc = nowUtc;
            await database.SaveChangesAsync(cancellationToken);
        }
        else if (!duplicate)
        {
            return QuotaServiceResult.Failure(
                "QUOTA_RESERVATION_NOT_RELEASABLE",
                "Lượt hạn mức đã hoàn tất hoặc hết hạn nên không thể hoàn lại.");
        }

        await transaction.CommitAsync(cancellationToken);
        return QuotaServiceResult.Success(Map(
            reservation,
            await CalculateRemainingAsync(userId, cancellationToken),
            duplicate));
    }

    private Task LockUserAsync(Guid userId, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT user_id FROM dbo.users WITH (UPDLOCK, HOLDLOCK) WHERE user_id = {userId}",
            cancellationToken);

    private Task<int> ExpireHeldAsync(Guid userId, DateTime nowUtc, CancellationToken cancellationToken) =>
        database.UsageReservations
            .Where(item => item.UserId == userId
                && item.StatusCode == "HELD"
                && item.ExpiresAtUtc <= nowUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.StatusCode, "EXPIRED")
                .SetProperty(item => item.UpdatedAtUtc, nowUtc),
                cancellationToken);

    private async Task<decimal?> CalculateRemainingAsync(Guid userId, CancellationToken cancellationToken) =>
        (await entitlementService.GetAsync(userId, cancellationToken))?.Quota.RemainingMinutes;

    private static QuotaReservationResponse Map(
        UsageReservation item,
        decimal? remaining,
        bool duplicate) => new(
            item.ReservationId,
            item.StatusCode,
            item.EstimatedMinutes,
            item.CommittedMinutes,
            item.ExpiresAtUtc,
            remaining,
            duplicate);
}

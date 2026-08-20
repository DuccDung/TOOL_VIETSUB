using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Cloud;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Purchases;

public sealed class PurchaseSettlementService(
    SubVidDbContext database,
    CloudCredentialAllocationService allocationService)
{
    public async Task<PurchaseSettlementResult> SettleAsync(
        PurchaseSettlementRequest request,
        CancellationToken cancellationToken)
    {
        var strategy = database.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var existingEvent = await database.PaymentWebhookEvents.SingleOrDefaultAsync(
                item => item.ProviderCode == request.ProviderCode
                    && item.ExternalEventId == request.ExternalEventId,
                cancellationToken);
            if (existingEvent?.ResultCode == PaymentWebhookResultCodes.Processed)
            {
                var existingOrder = existingEvent.OrderId.HasValue
                    ? await database.PurchaseOrders.AsNoTracking()
                        .SingleAsync(item => item.OrderId == existingEvent.OrderId.Value, cancellationToken)
                    : null;
                await transaction.CommitAsync(cancellationToken);
                return new PurchaseSettlementResult(
                    existingOrder?.OrderId ?? request.OrderId,
                    existingOrder?.OrderNumber ?? string.Empty,
                    existingOrder?.ActivatedSubscriptionId,
                    true,
                    []);
            }

            var order = await database.PurchaseOrders
                .FromSqlInterpolated($$"""
                    SELECT *
                    FROM dbo.purchase_orders WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                    WHERE order_id = {{request.OrderId}}
                    """)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new PurchaseException("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng thanh toán.", StatusCodes.Status404NotFound);

            // Another webhook may have completed while this transaction was waiting
            // for the order lock. Refresh the ledger after the lock is acquired so a
            // concurrent replay never attempts to insert the same provider event.
            existingEvent ??= await database.PaymentWebhookEvents.SingleOrDefaultAsync(
                item => item.ProviderCode == request.ProviderCode
                    && item.ExternalEventId == request.ExternalEventId,
                cancellationToken);
            if (existingEvent?.ResultCode == PaymentWebhookResultCodes.Processed)
            {
                await transaction.CommitAsync(cancellationToken);
                return new PurchaseSettlementResult(
                    order.OrderId,
                    order.OrderNumber,
                    order.ActivatedSubscriptionId,
                    true,
                    []);
            }

            PurchasePaymentTransaction? payment = null;
            if (request.PaymentTransactionId is Guid paymentId)
            {
                payment = await database.PurchasePaymentTransactions
                    .FromSqlInterpolated($$"""
                        SELECT *
                        FROM dbo.purchase_payment_transactions WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                        WHERE payment_transaction_id = {{paymentId}}
                        """)
                    .SingleOrDefaultAsync(cancellationToken)
                    ?? throw new PurchaseException("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.", StatusCodes.Status404NotFound);
                if (payment.OrderId != order.OrderId)
                {
                    throw new PurchaseException("PAYMENT_ORDER_MISMATCH", "Giao dịch không thuộc đơn hàng cần thanh toán.");
                }
            }

            if (order.StatusCode == PurchaseOrderStatuses.Paid)
            {
                UpsertEvent(existingEvent, order, payment, request, PaymentWebhookResultCodes.Processed, request.PaidAtUtc);
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PurchaseSettlementResult(
                    order.OrderId,
                    order.OrderNumber,
                    order.ActivatedSubscriptionId,
                    true,
                    []);
            }
            if (order.StatusCode != PurchaseOrderStatuses.Pending)
            {
                throw new PurchaseException(
                    "ORDER_NOT_PAYABLE",
                    $"Đơn hàng đang ở trạng thái {order.StatusCode} và không thể thanh toán.",
                    StatusCodes.Status409Conflict);
            }

            var userActive = await database.Users.AnyAsync(
                item => item.UserId == order.UserId
                    && item.StatusCode == "ACTIVE"
                    && item.DeletedAtUtc == null,
                cancellationToken);
            if (!userActive)
            {
                throw new PurchaseException("ACCOUNT_NOT_ACTIVE", "Tài khoản nhận gói không còn hoạt động.", StatusCodes.Status409Conflict);
            }
            var planActive = await database.ServicePlans.AnyAsync(
                item => item.PlanId == order.PlanId && item.IsActive,
                cancellationToken);
            if (!planActive)
            {
                throw new PurchaseException("PLAN_NOT_ACTIVE", "Gói trong đơn hàng không còn hoạt động.", StatusCodes.Status409Conflict);
            }
            if (request.PaidAmount != order.PriceAmount)
            {
                throw new PurchaseException("PAYMENT_AMOUNT_MISMATCH", "Số tiền nhận được không khớp giá trị đơn hàng.");
            }

            if (payment is not null)
            {
                if (payment.StatusCode is PurchasePaymentStatuses.Cancelled or PurchasePaymentStatuses.Refunded)
                {
                    throw new PurchaseException("PAYMENT_NOT_PAYABLE", "Giao dịch đã bị hủy hoặc hoàn tiền.", StatusCodes.Status409Conflict);
                }
                if (payment.ExpectedAmount != request.PaidAmount)
                {
                    throw new PurchaseException("PAYMENT_AMOUNT_MISMATCH", "Số tiền nhận được không khớp giao dịch.");
                }

                payment.StatusCode = PurchasePaymentStatuses.Paid;
                payment.ProviderTransactionId = request.ExternalPaymentId;
                payment.PaidAmount = request.PaidAmount;
                payment.PaidAtUtc = request.PaidAtUtc;
                payment.UpdatedAtUtc = request.PaidAtUtc;
            }

            var activeSubscriptions = await database.UserSubscriptions
                .Where(item => item.UserId == order.UserId && item.StatusCode == "ACTIVE")
                .ToArrayAsync(cancellationToken);
            foreach (var subscription in activeSubscriptions)
            {
                subscription.StatusCode = "CANCELLED";
                subscription.EndsAtUtc = request.PaidAtUtc > subscription.StartsAtUtc
                    ? request.PaidAtUtc
                    : subscription.StartsAtUtc.AddMilliseconds(1);
                subscription.UpdatedAtUtc = request.PaidAtUtc;
            }

            var subscriptionId = Guid.NewGuid();
            database.UserSubscriptions.Add(new UserSubscription
            {
                SubscriptionId = subscriptionId,
                UserId = order.UserId,
                PlanId = order.PlanId,
                StatusCode = "ACTIVE",
                StartsAtUtc = request.PaidAtUtc,
                EndsAtUtc = request.PaidAtUtc.AddDays(order.BillingPeriodDays),
                CreatedAtUtc = request.PaidAtUtc,
                UpdatedAtUtc = request.PaidAtUtc,
            });

            var allocation = await allocationService.SynchronizeForPlanAsync(
                order.UserId,
                order.PlanId,
                request.ActorUserId,
                $"Đồng bộ API key sau thanh toán {order.OrderNumber} qua {request.ProviderCode}.",
                cancellationToken);
            if (allocation.UnavailableProviders.Count > 0)
            {
                throw new PurchaseException(
                    "CLOUD_KEY_UNAVAILABLE",
                    $"Kho API key chưa đủ cho: {string.Join(", ", allocation.UnavailableProviders)}.",
                    StatusCodes.Status503ServiceUnavailable);
            }

            order.StatusCode = PurchaseOrderStatuses.Paid;
            order.ExternalPaymentId = request.ExternalPaymentId;
            order.ActivatedSubscriptionId = subscriptionId;
            order.PaidAtUtc = request.PaidAtUtc;
            order.UpdatedAtUtc = request.PaidAtUtc;

            UpsertEvent(existingEvent, order, payment, request, PaymentWebhookResultCodes.Processed, request.PaidAtUtc);
            database.SecurityAuditLogs.Add(new SecurityAuditLog
            {
                UserId = order.UserId,
                EventCode = request.AuditEventCode,
                OutcomeCode = "SUCCESS",
                IpAddress = request.IpAddress,
                DeviceId = request.ProviderCode,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    order.OrderId,
                    order.OrderNumber,
                    order.PlanCodeSnapshot,
                    request.ProviderCode,
                    request.ExternalEventId,
                    request.ExternalPaymentId,
                    request.PaidAmount,
                    subscriptionId,
                    request.LatePayment,
                    allocatedCredentialIds = allocation.AllocatedCredentialIds,
                    releasedCredentialIds = allocation.ReleasedCredentialIds,
                    unavailableProviders = allocation.UnavailableProviders,
                }),
                CreatedAtUtc = request.PaidAtUtc,
            });

            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PurchaseSettlementResult(
                order.OrderId,
                order.OrderNumber,
                subscriptionId,
                false,
                allocation.UnavailableProviders);
        });
    }

    private void UpsertEvent(
        PaymentWebhookEvent? existingEvent,
        PurchaseOrder order,
        PurchasePaymentTransaction? payment,
        PurchaseSettlementRequest request,
        string resultCode,
        DateTime processedAtUtc)
    {
        var paymentEvent = existingEvent ?? new PaymentWebhookEvent
        {
            EventId = Guid.NewGuid(),
            ProviderCode = request.ProviderCode,
            ExternalEventId = request.ExternalEventId,
            ReceivedAtUtc = request.ReceivedAtUtc,
        };
        paymentEvent.OrderId = order.OrderId;
        paymentEvent.PaymentTransactionId = payment?.PaymentTransactionId;
        paymentEvent.EventCode = request.EventCode;
        paymentEvent.PayloadSha256 = request.PayloadSha256;
        paymentEvent.ResultCode = resultCode;
        paymentEvent.TransferContent = request.TransferContent;
        paymentEvent.TransferAmount = request.PaidAmount;
        paymentEvent.RawPayload = request.RawPayload;
        paymentEvent.ProcessedAtUtc = processedAtUtc;
        if (existingEvent is null)
        {
            database.PaymentWebhookEvents.Add(paymentEvent);
        }
    }
}

public sealed record PurchaseSettlementRequest(
    Guid OrderId,
    Guid? PaymentTransactionId,
    string ProviderCode,
    string ExternalEventId,
    string ExternalPaymentId,
    string EventCode,
    string PayloadSha256,
    decimal PaidAmount,
    DateTime ReceivedAtUtc,
    DateTime PaidAtUtc,
    string? TransferContent,
    string? RawPayload,
    Guid? ActorUserId,
    string? IpAddress,
    string AuditEventCode,
    bool LatePayment);

public sealed record PurchaseSettlementResult(
    Guid OrderId,
    string OrderNumber,
    Guid? SubscriptionId,
    bool Duplicate,
    IReadOnlyList<string> UnavailableProviders);

public static class PaymentWebhookResultCodes
{
    public const string Received = "RECEIVED";
    public const string Processed = "PROCESSED";
    public const string Ignored = "IGNORED";
    public const string Unmatched = "UNMATCHED";
    public const string Ambiguous = "AMBIGUOUS";
    public const string Failed = "FAILED";
}

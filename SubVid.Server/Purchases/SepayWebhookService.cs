using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubVid.Server.Contracts;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Purchases;

public sealed class SepayWebhookService(
    SubVidDbContext database,
    PurchaseSettlementService settlementService,
    IOptions<SepayOptions> optionsAccessor,
    IHostEnvironment environment,
    ILogger<SepayWebhookService> logger)
{
    private readonly SepayOptions options = optionsAccessor.Value;

    public bool IsAuthorized(string? authorizationHeader, string? apiKeyHeader)
    {
        var expected = options.WebhookApiKey.Trim();
        if (expected.Length == 0)
        {
            return environment.IsDevelopment();
        }

        var candidate = apiKeyHeader?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var authorization = authorizationHeader?.Trim() ?? string.Empty;
            if (authorization.StartsWith("Apikey ", StringComparison.OrdinalIgnoreCase))
            {
                candidate = authorization[7..].Trim();
            }
            else if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                candidate = authorization[7..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(expectedHash, candidateHash);
    }

    public async Task<SepayWebhookProcessResult> ProcessAsync(
        SepayWebhookPayload payload,
        string rawPayload,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var receivedAtUtc = DateTime.UtcNow;
        var canonicalPayload = CanonicalizePayload(payload);
        var payloadHash = Sha256(rawPayload);
        var externalEventId = ResolveExternalEventId(payload, canonicalPayload);
        var existingEvent = await database.PaymentWebhookEvents
            .SingleOrDefaultAsync(
                item => item.ProviderCode == PurchaseCheckoutService.ProviderCode
                    && item.ExternalEventId == externalEventId,
                cancellationToken);
        if (existingEvent is not null && existingEvent.ResultCode != PaymentWebhookResultCodes.Failed)
        {
            var orderNumber = existingEvent.OrderId.HasValue
                ? await database.PurchaseOrders.AsNoTracking()
                    .Where(item => item.OrderId == existingEvent.OrderId.Value)
                    .Select(item => item.OrderNumber)
                    .SingleOrDefaultAsync(cancellationToken)
                : null;
            return new SepayWebhookProcessResult(
                existingEvent.ResultCode == PaymentWebhookResultCodes.Processed,
                existingEvent.ResultCode,
                "Webhook đã được tiếp nhận trước đó.",
                orderNumber,
                StatusCodes.Status200OK);
        }

        if (!string.Equals(payload.TransferType?.Trim(), "in", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveNonSettlementAsync(
                existingEvent, externalEventId, payloadHash, payload, rawPayload,
                "TRANSFER_NOT_IN", PaymentWebhookResultCodes.Ignored,
                "Giao dịch không phải tiền vào.", receivedAtUtc, cancellationToken);
        }
        if (payload.TransferAmount <= 0)
        {
            return await SaveNonSettlementAsync(
                existingEvent, externalEventId, payloadHash, payload, rawPayload,
                "INVALID_AMOUNT", PaymentWebhookResultCodes.Ignored,
                "Số tiền giao dịch không hợp lệ.", receivedAtUtc, cancellationToken);
        }
        if (SepayGatewayClient.NormalizeAccount(payload.AccountNumber)
            != SepayGatewayClient.NormalizeAccount(options.ReceiverAccountNumber))
        {
            return await SaveNonSettlementAsync(
                existingEvent, externalEventId, payloadHash, payload, rawPayload,
                "RECEIVER_MISMATCH", PaymentWebhookResultCodes.Ignored,
                "Giao dịch không thuộc tài khoản nhận tiền đã cấu hình.", receivedAtUtc, cancellationToken);
        }

        var codes = ExtractTransferCodes(
            options.TransferCodePrefix,
            payload.Code,
            payload.Content,
            payload.Description,
            payload.ReferenceCode);
        if (codes.Count == 0)
        {
            return await SaveNonSettlementAsync(
                existingEvent, externalEventId, payloadHash, payload, rawPayload,
                "TRANSFER_CODE_MISSING", PaymentWebhookResultCodes.Unmatched,
                "Không tìm thấy mã chuyển khoản SubVid.", receivedAtUtc, cancellationToken);
        }
        if (codes.Count > 1)
        {
            return await SaveNonSettlementAsync(
                existingEvent, externalEventId, payloadHash, payload, rawPayload,
                "MULTIPLE_TRANSFER_CODES", PaymentWebhookResultCodes.Ambiguous,
                "Nội dung chứa nhiều mã chuyển khoản SubVid khác nhau.", receivedAtUtc, cancellationToken);
        }

        var transactionCode = codes[0];
        var payments = await database.PurchasePaymentTransactions.AsNoTracking()
            .Include(item => item.Order)
            .Where(item => item.ProviderCode == PurchaseCheckoutService.ProviderCode
                && item.TransactionCode == transactionCode)
            .Take(2)
            .ToArrayAsync(cancellationToken);
        if (payments.Length == 0)
        {
            return await SaveNonSettlementAsync(
                existingEvent, externalEventId, payloadHash, payload, rawPayload,
                "PAYMENT_NOT_FOUND", PaymentWebhookResultCodes.Unmatched,
                "Không tìm thấy giao dịch khớp mã chuyển khoản.", receivedAtUtc, cancellationToken);
        }
        if (payments.Length > 1)
        {
            return await SaveNonSettlementAsync(
                existingEvent, externalEventId, payloadHash, payload, rawPayload,
                "MULTIPLE_PAYMENTS", PaymentWebhookResultCodes.Ambiguous,
                "Tìm thấy nhiều giao dịch phù hợp.", receivedAtUtc, cancellationToken);
        }

        var payment = payments[0];
        if (payment.ExpectedAmount != payload.TransferAmount)
        {
            return await SaveNonSettlementAsync(
                existingEvent, externalEventId, payloadHash, payload, rawPayload,
                "AMOUNT_MISMATCH", PaymentWebhookResultCodes.Ignored,
                "Số tiền nhận được không khớp giao dịch.", receivedAtUtc, cancellationToken,
                payment.OrderId, payment.PaymentTransactionId);
        }
        if (payment.StatusCode is PurchasePaymentStatuses.Cancelled or PurchasePaymentStatuses.Refunded
            || payment.Order.StatusCode is PurchaseOrderStatuses.Cancelled
                or PurchaseOrderStatuses.Failed
                or PurchaseOrderStatuses.Refunded)
        {
            return await SaveNonSettlementAsync(
                existingEvent, externalEventId, payloadHash, payload, rawPayload,
                "PAYMENT_NOT_PAYABLE", PaymentWebhookResultCodes.Ignored,
                "Giao dịch hoặc đơn hàng không còn có thể thanh toán.", receivedAtUtc, cancellationToken,
                payment.OrderId, payment.PaymentTransactionId);
        }

        var paidAtUtc = ParseTransactionDate(payload.TransactionDate) ?? receivedAtUtc;
        var latePayment = payment.ExpiresAtUtc < receivedAtUtc;
        var externalPaymentId = ResolveExternalPaymentId(payload, externalEventId);
        try
        {
            var settlement = await settlementService.SettleAsync(
                new PurchaseSettlementRequest(
                    payment.OrderId,
                    payment.PaymentTransactionId,
                    PurchaseCheckoutService.ProviderCode,
                    externalEventId,
                    externalPaymentId,
                    latePayment ? "LATE_PAYMENT_ACCEPTED" : "PAYMENT_SUCCEEDED",
                    payloadHash,
                    payload.TransferAmount,
                    receivedAtUtc,
                    paidAtUtc,
                    CombineTransferContent(payload),
                    rawPayload,
                    null,
                    ipAddress,
                    latePayment ? "SEPAY_LATE_PAYMENT_ACCEPTED" : "SEPAY_PAYMENT_CONFIRMED",
                    latePayment),
                cancellationToken);
            return new SepayWebhookProcessResult(
                true,
                PaymentWebhookResultCodes.Processed,
                latePayment
                    ? "Đã ghi nhận thanh toán đến muộn và kích hoạt gói."
                    : "Đã ghi nhận thanh toán và kích hoạt gói.",
                settlement.OrderNumber,
                StatusCodes.Status200OK);
        }
        catch (Exception exception) when (exception is PurchaseException or InvalidOperationException or DbUpdateException)
        {
            if (exception is DbUpdateException)
            {
                database.ChangeTracker.Clear();
                var replay = await database.PaymentWebhookEvents.AsNoTracking()
                    .Include(item => item.Order)
                    .SingleOrDefaultAsync(
                        item => item.ProviderCode == PurchaseCheckoutService.ProviderCode
                            && item.ExternalEventId == externalEventId,
                        cancellationToken);
                if (replay?.ResultCode == PaymentWebhookResultCodes.Processed)
                {
                    return new SepayWebhookProcessResult(
                        true,
                        PaymentWebhookResultCodes.Processed,
                        "Webhook đã được xử lý đồng thời trước đó.",
                        replay.Order?.OrderNumber,
                        StatusCodes.Status200OK);
                }
            }

            logger.LogError(exception, "SePay settlement failed for event {ExternalEventId}.", externalEventId);
            database.ChangeTracker.Clear();
            var failedEvent = await database.PaymentWebhookEvents.SingleOrDefaultAsync(
                item => item.ProviderCode == PurchaseCheckoutService.ProviderCode
                    && item.ExternalEventId == externalEventId,
                cancellationToken);
            if (failedEvent?.ResultCode == PaymentWebhookResultCodes.Processed)
            {
                var completedOrderNumber = failedEvent.OrderId.HasValue
                    ? await database.PurchaseOrders.AsNoTracking()
                        .Where(item => item.OrderId == failedEvent.OrderId.Value)
                        .Select(item => item.OrderNumber)
                        .SingleOrDefaultAsync(cancellationToken)
                    : null;
                return new SepayWebhookProcessResult(
                    true,
                    PaymentWebhookResultCodes.Processed,
                    "Webhook đã được xử lý đồng thời trước đó.",
                    completedOrderNumber,
                    StatusCodes.Status200OK);
            }
            await SaveNonSettlementAsync(
                failedEvent, externalEventId, payloadHash, payload, rawPayload,
                "SETTLEMENT_FAILED", PaymentWebhookResultCodes.Failed,
                "Không thể hoàn tất kích hoạt gói; SePay có thể gửi lại webhook.",
                receivedAtUtc, cancellationToken, payment.OrderId, payment.PaymentTransactionId);
            return new SepayWebhookProcessResult(
                false,
                PaymentWebhookResultCodes.Failed,
                "Chưa thể hoàn tất thanh toán. Vui lòng thử lại.",
                payment.Order.OrderNumber,
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static IReadOnlyList<string> ExtractTransferCodes(string? prefix, params string?[] values)
    {
        var normalizedPrefix = PaymentReferenceCodeGenerator.NormalizePrefix(prefix);
        var pattern = new Regex(
            $@"(?<![A-Z0-9]){Regex.Escape(normalizedPrefix)}[\s_-]*([0-9]{{{PaymentReferenceCodeGenerator.DigitCount}}})(?![0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            foreach (Match match in pattern.Matches(value!))
            {
                results.Add($"{normalizedPrefix}-{match.Groups[1].Value}");
            }
        }

        return results.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string ResolveExternalEventId(SepayWebhookPayload payload, string canonicalPayload)
    {
        if (payload.Id.HasValue)
        {
            return $"id:{payload.Id.Value.ToString(CultureInfo.InvariantCulture)}";
        }
        if (!string.IsNullOrWhiteSpace(payload.ReferenceCode))
        {
            var reference = string.Concat(payload.ReferenceCode.Trim().Where(char.IsLetterOrDigit));
            if (reference.Length > 0)
            {
                return $"ref:{reference}"[..Math.Min(120, reference.Length + 4)];
            }
        }

        return $"sha256:{Sha256(canonicalPayload)}";
    }

    private async Task<SepayWebhookProcessResult> SaveNonSettlementAsync(
        PaymentWebhookEvent? existingEvent,
        string externalEventId,
        string payloadHash,
        SepayWebhookPayload payload,
        string rawPayload,
        string eventCode,
        string resultCode,
        string message,
        DateTime receivedAtUtc,
        CancellationToken cancellationToken,
        Guid? orderId = null,
        Guid? paymentTransactionId = null)
    {
        var paymentEvent = existingEvent ?? new PaymentWebhookEvent
        {
            EventId = Guid.NewGuid(),
            ProviderCode = PurchaseCheckoutService.ProviderCode,
            ExternalEventId = externalEventId,
            ReceivedAtUtc = receivedAtUtc,
        };
        paymentEvent.OrderId = orderId;
        paymentEvent.PaymentTransactionId = paymentTransactionId;
        paymentEvent.EventCode = eventCode;
        paymentEvent.PayloadSha256 = payloadHash;
        paymentEvent.ResultCode = resultCode;
        paymentEvent.TransferContent = CombineTransferContent(payload);
        paymentEvent.TransferAmount = payload.TransferAmount;
        paymentEvent.RawPayload = rawPayload;
        paymentEvent.ProcessedAtUtc = receivedAtUtc;
        if (existingEvent is null)
        {
            database.PaymentWebhookEvents.Add(paymentEvent);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
        }

        string? orderNumber = null;
        if (orderId.HasValue)
        {
            orderNumber = await database.PurchaseOrders.AsNoTracking()
                .Where(item => item.OrderId == orderId.Value)
                .Select(item => item.OrderNumber)
                .SingleOrDefaultAsync(cancellationToken);
        }
        return new SepayWebhookProcessResult(
            false,
            resultCode,
            message,
            orderNumber,
            StatusCodes.Status200OK);
    }

    private static string CanonicalizePayload(SepayWebhookPayload payload) => JsonSerializer.Serialize(new
    {
        payload.Id,
        gateway = payload.Gateway?.Trim(),
        transactionDate = payload.TransactionDate?.Trim(),
        accountNumber = SepayGatewayClient.NormalizeAccount(payload.AccountNumber),
        code = payload.Code?.Trim(),
        content = payload.Content?.Trim(),
        transferType = payload.TransferType?.Trim().ToLowerInvariant(),
        payload.TransferAmount,
        payload.Accumulated,
        subAccount = payload.SubAccount?.Trim(),
        referenceCode = payload.ReferenceCode?.Trim(),
        description = payload.Description?.Trim(),
    });

    private static string ResolveExternalPaymentId(SepayWebhookPayload payload, string externalEventId)
    {
        var value = !string.IsNullOrWhiteSpace(payload.ReferenceCode)
            ? payload.ReferenceCode.Trim()
            : externalEventId;
        return value.Length <= 120 ? value : value[..120];
    }

    private static string CombineTransferContent(SepayWebhookPayload payload)
    {
        var combined = string.Join(" | ", new[] { payload.Code, payload.Content, payload.Description, payload.ReferenceCode }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim()));
        return combined.Length <= 1000 ? combined : combined[..1000];
    }

    private static DateTime? ParseTransactionDate(string? value)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed.UtcDateTime;
        }

        return null;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record SepayWebhookProcessResult(
    bool Processed,
    string ResultCode,
    string Message,
    string? OrderNumber,
    int HttpStatusCode);

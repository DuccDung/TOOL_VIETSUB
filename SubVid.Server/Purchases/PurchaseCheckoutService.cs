using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubVid.Server.Contracts;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Purchases;

public sealed class PurchaseCheckoutService(
    SubVidDbContext database,
    PaymentReferenceCodeGenerator referenceCodeGenerator,
    SepayGatewayClient sepayGateway,
    IOptions<SepayOptions> optionsAccessor)
{
    public const string ProviderCode = "SEPAY";
    public const string SourceCode = "DESKTOP_APP";

    private readonly SepayOptions options = optionsAccessor.Value;

    public async Task<PurchaseCheckoutResponse> CreateAsync(
        Guid userId,
        CreatePurchaseCheckoutRequest request,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            throw new PurchaseException("AUTH_REQUIRED", "Vui lòng đăng nhập để mua gói.", StatusCodes.Status401Unauthorized);
        }

        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var existing = await LoadByIdempotencyAsync(idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            EnsureOwner(existing, userId);
            return Map(existing, true);
        }

        var planQuery = database.ServicePlans.AsNoTracking()
            .Where(item => item.IsActive && item.IsPublic);
        ServicePlan? plan;
        if (request.PlanId is Guid planId && planId != Guid.Empty)
        {
            plan = await planQuery.SingleOrDefaultAsync(item => item.PlanId == planId, cancellationToken);
            if (plan is not null
                && !string.IsNullOrWhiteSpace(request.PlanCode)
                && !string.Equals(plan.PlanCode, request.PlanCode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                plan = null;
            }
        }
        else
        {
            var planCode = request.PlanCode?.Trim().ToUpperInvariant();
            plan = string.IsNullOrWhiteSpace(planCode)
                ? null
                : await planQuery.SingleOrDefaultAsync(item => item.PlanCode == planCode, cancellationToken);
        }

        if (plan is null)
        {
            throw new PurchaseException("PLAN_NOT_AVAILABLE", "Gói dịch vụ không tồn tại hoặc đã ngừng bán.", StatusCodes.Status404NotFound);
        }
        if (plan.PriceAmount <= 0)
        {
            throw new PurchaseException("PLAN_NOT_PURCHASABLE", "Gói miễn phí không cần thanh toán.");
        }
        if (!string.Equals(plan.CurrencyCode, "VND", StringComparison.OrdinalIgnoreCase))
        {
            throw new PurchaseException("PAYMENT_CURRENCY_UNSUPPORTED", "SePay hiện chỉ hỗ trợ thanh toán bằng VND.");
        }
        if (plan.BillingPeriodDays is < 1 or > 3650)
        {
            throw new PurchaseException("PLAN_CONFIGURATION_INVALID", "Thời hạn của gói chưa được cấu hình hợp lệ.");
        }

        var price = SepayGatewayClient.NormalizeVndAmount(plan.PriceAmount);
        if (decimal.Round(request.ExpectedPriceAmount, 2, MidpointRounding.AwayFromZero) != price)
        {
            throw new PurchaseException(
                "PLAN_PRICE_CHANGED",
                "Giá gói đã thay đổi. Vui lòng làm mới danh sách gói trước khi thanh toán.",
                StatusCodes.Status409Conflict);
        }

        var transactionCode = await referenceCodeGenerator.GenerateAsync(cancellationToken);
        var receiver = await sepayGateway.PrepareCheckoutAsync(price, transactionCode, cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var expiresAtUtc = nowUtc.AddMinutes(options.PaymentExpireMinutes);
        var orderNumber = await GenerateOrderNumberAsync(cancellationToken);
        var strategy = database.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var concurrentExisting = await LoadByIdempotencyAsync(idempotencyKey, cancellationToken);
            if (concurrentExisting is not null)
            {
                EnsureOwner(concurrentExisting, userId);
                await transaction.CommitAsync(cancellationToken);
                return Map(concurrentExisting, true);
            }

            var order = new PurchaseOrder
            {
                OrderId = Guid.NewGuid(),
                OrderNumber = orderNumber,
                UserId = userId,
                PlanId = plan.PlanId,
                StatusCode = PurchaseOrderStatuses.Pending,
                PaymentProviderCode = ProviderCode,
                IdempotencyKey = idempotencyKey,
                PriceAmount = price,
                CurrencyCode = "VND",
                BillingPeriodDays = plan.BillingPeriodDays,
                PlanCodeSnapshot = plan.PlanCode,
                PlanNameSnapshot = plan.DisplayName,
                SourceCode = SourceCode,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            var payment = new PurchasePaymentTransaction
            {
                PaymentTransactionId = Guid.NewGuid(),
                Order = order,
                ProviderCode = ProviderCode,
                StatusCode = PurchasePaymentStatuses.Pending,
                TransactionCode = transactionCode,
                BankCode = receiver.BankShortName,
                ReceiverBankName = receiver.BankName,
                ReceiverAccountNumber = receiver.AccountNumber,
                ReceiverAccountName = receiver.AccountName,
                QrUrl = receiver.QrImageUrl,
                TransferContent = transactionCode,
                ExpectedAmount = price,
                ExpiresAtUtc = expiresAtUtc,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                ProviderResponseJson = JsonSerializer.Serialize(new
                {
                    receiver.ResolvedByApi,
                    receiver.BankShortName,
                    receiver.AccountNumber,
                }),
            };

            database.PurchaseOrders.Add(order);
            database.PurchasePaymentTransactions.Add(payment);
            database.SecurityAuditLogs.Add(new SecurityAuditLog
            {
                UserId = userId,
                EventCode = "PURCHASE_CHECKOUT_CREATED",
                OutcomeCode = "SUCCESS",
                IpAddress = ipAddress,
                DeviceId = SourceCode,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    order.OrderId,
                    order.OrderNumber,
                    order.PlanCodeSnapshot,
                    order.PriceAmount,
                    order.CurrencyCode,
                    payment.PaymentTransactionId,
                    payment.TransactionCode,
                    payment.ExpiresAtUtc,
                }),
                CreatedAtUtc = nowUtc,
            });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Map(order, payment, false);
        });
    }

    public async Task<PurchaseCheckoutResponse?> GetAsync(
        Guid userId,
        string orderNumber,
        CancellationToken cancellationToken)
    {
        var normalized = orderNumber.Trim();
        var order = await database.PurchaseOrders.AsNoTracking()
            .Include(item => item.PaymentTransactions)
            .SingleOrDefaultAsync(
                item => item.UserId == userId && item.OrderNumber == normalized,
                cancellationToken);
        return order is null ? null : Map(order, true);
    }

    private async Task<PurchaseOrder?> LoadByIdempotencyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await database.PurchaseOrders
            .Include(item => item.PaymentTransactions)
            .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);

    private static void EnsureOwner(PurchaseOrder order, Guid userId)
    {
        if (order.UserId != userId)
        {
            throw new PurchaseException(
                "IDEMPOTENCY_CONFLICT",
                "Khóa chống trùng đã được sử dụng cho một yêu cầu khác.",
                StatusCodes.Status409Conflict);
        }
    }

    private static string NormalizeIdempotencyKey(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 8 or > 100
            || normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new PurchaseException("IDEMPOTENCY_KEY_INVALID", "Khóa chống trùng thanh toán không hợp lệ.");
        }

        return normalized;
    }

    private async Task<string> GenerateOrderNumberAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = $"SV-{DateTime.UtcNow:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}";
            if (!await database.PurchaseOrders.AsNoTracking()
                    .AnyAsync(item => item.OrderNumber == candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Không thể tạo mã đơn hàng duy nhất.");
    }

    private static PurchaseCheckoutResponse Map(PurchaseOrder order, bool reused)
    {
        var payment = order.PaymentTransactions
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Đơn hàng chưa có giao dịch thanh toán.");
        return Map(order, payment, reused);
    }

    private static PurchaseCheckoutResponse Map(
        PurchaseOrder order,
        PurchasePaymentTransaction payment,
        bool reused)
    {
        var isPaid = order.StatusCode == PurchaseOrderStatuses.Paid
            && payment.StatusCode == PurchasePaymentStatuses.Paid;
        var isExpired = !isPaid && payment.ExpiresAtUtc <= DateTime.UtcNow;
        var paymentStatus = isExpired && payment.StatusCode == PurchasePaymentStatuses.Pending
            ? PurchasePaymentStatuses.Expired
            : payment.StatusCode;
        var message = isPaid
            ? "Thanh toán thành công. Gói dịch vụ đã được kích hoạt."
            : isExpired
                ? "Mã thanh toán đã hết hạn. Hãy tạo một lượt thanh toán mới nếu bạn chưa chuyển tiền."
                : "Đang chờ SePay xác nhận giao dịch.";

        return new PurchaseCheckoutResponse(
            order.OrderId,
            order.OrderNumber,
            order.StatusCode,
            paymentStatus,
            order.PlanCodeSnapshot,
            order.PlanNameSnapshot,
            payment.TransactionCode,
            payment.ReceiverBankName,
            payment.BankCode,
            payment.ReceiverAccountNumber,
            payment.ReceiverAccountName,
            payment.TransferContent,
            payment.QrUrl,
            payment.ExpectedAmount,
            order.CurrencyCode,
            AsUtcOffset(order.CreatedAtUtc),
            AsUtcOffset(payment.ExpiresAtUtc),
            AsUtcOffset(payment.PaidAtUtc ?? order.PaidAtUtc),
            isPaid,
            isExpired,
            message,
            reused);
    }

    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? AsUtcOffset(DateTime? value) =>
        value.HasValue ? AsUtcOffset(value.Value) : null;
}

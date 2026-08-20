using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Cloud;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Purchases;

public static class PurchaseOrderStatuses
{
    public const string Pending = "PENDING";
    public const string Paid = "PAID";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
    public const string Refunded = "REFUNDED";
}

public sealed class AdminPurchaseTestService(
    SubVidDbContext database,
    IPasswordHasher<User> passwordHasher,
    CloudCredentialProtector credentialProtector,
    PurchaseSettlementService settlementService)
{
    public const string SourceCode = "ADMIN_E2E";
    public const string PaymentProviderCode = "FAKE_ADMIN";

    public async Task<AdminPurchaseTestRun> CreatePendingProPurchaseAsync(
        Guid actorAdminId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        await EnsureAdminAsync(actorAdminId, cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        var runId = $"E2E_PURCHASE_{nowUtc:yyyyMMddHHmmss}_{token}";
        var email = $"e2e.purchase.{nowUtc:yyyyMMddHHmmss}.{token}@local.invalid";
        var fakeApiKey = $"e2e_fake_{runId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var freePlan = await database.ServicePlans.SingleOrDefaultAsync(
            item => item.PlanCode == "FREE" && item.IsActive,
            cancellationToken)
            ?? throw new InvalidOperationException("Gói FREE chưa được cấu hình hoặc đang bị tắt.");
        var proPlan = await database.ServicePlans.SingleOrDefaultAsync(
            item => item.PlanCode == "PRO" && item.IsActive && item.IsPublic,
            cancellationToken)
            ?? throw new InvalidOperationException("Gói PRO chưa được cấu hình công khai hoặc đang bị tắt.");
        if (proPlan.BillingPeriodDays is < 1 or > 3650)
        {
            throw new InvalidOperationException("Thời hạn thanh toán của gói PRO không hợp lệ.");
        }

        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = email,
            DisplayName = $"E2E mua PRO · {token}",
            RoleCode = "USER",
            StatusCode = "ACTIVE",
            EmailConfirmed = true,
            PasswordChangedAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        user.PasswordHash = passwordHasher.HashPassword(
            user,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));

        var freeSubscription = new UserSubscription
        {
            SubscriptionId = Guid.NewGuid(),
            UserId = user.UserId,
            PlanId = freePlan.PlanId,
            StatusCode = "ACTIVE",
            StartsAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        var fakeCredential = new CloudProviderCredential
        {
            CredentialId = Guid.NewGuid(),
            ProviderCode = "openai",
            DisplayName = $"{runId} · FAKE KEY (không phát hành)",
            EncryptedApiKey = credentialProtector.Protect(fakeApiKey),
            KeyFingerprint = CloudCredentialProtector.Fingerprint(fakeApiKey),
            KeySuffix = CloudCredentialProtector.Suffix(fakeApiKey),
            AllocationMode = CloudCredentialAllocationModes.Unassigned,
            StatusCode = "DISABLED",
            Priority = 10_000,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        var order = new PurchaseOrder
        {
            OrderId = Guid.NewGuid(),
            OrderNumber = $"E2E-{nowUtc:yyyyMMddHHmmss}-{token}",
            UserId = user.UserId,
            PlanId = proPlan.PlanId,
            StatusCode = PurchaseOrderStatuses.Pending,
            PaymentProviderCode = PaymentProviderCode,
            IdempotencyKey = runId,
            PriceAmount = proPlan.PriceAmount,
            CurrencyCode = proPlan.CurrencyCode,
            BillingPeriodDays = proPlan.BillingPeriodDays,
            PlanCodeSnapshot = proPlan.PlanCode,
            PlanNameSnapshot = proPlan.DisplayName,
            SourceCode = SourceCode,
            TestRunId = runId,
            CreatedByAdminId = actorAdminId,
            FakeCredentialId = fakeCredential.CredentialId,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

        database.Users.Add(user);
        database.UserSubscriptions.Add(freeSubscription);
        database.CloudProviderCredentials.Add(fakeCredential);
        database.PurchaseOrders.Add(order);
        database.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            UserId = user.UserId,
            EventCode = "E2E_PURCHASE_CREATED",
            OutcomeCode = "SUCCESS",
            IpAddress = ipAddress,
            DeviceId = SourceCode,
            DetailsJson = JsonSerializer.Serialize(new
            {
                order.OrderId,
                order.OrderNumber,
                order.TestRunId,
                order.PlanCodeSnapshot,
                order.PriceAmount,
                order.CurrencyCode,
                actorAdminId,
                fakeCredentialId = fakeCredential.CredentialId,
                fakeCredentialStatus = fakeCredential.StatusCode,
                fakeCredentialAllocation = fakeCredential.AllocationMode,
            }),
            CreatedAtUtc = nowUtc,
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetRequiredRunAsync(order.OrderId, false, cancellationToken);
    }

    public async Task<AdminPurchaseTestRun> ProcessSuccessfulFakeWebhookAsync(
        Guid actorAdminId,
        Guid orderId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        await EnsureAdminAsync(actorAdminId, cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var order = await database.PurchaseOrders.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrderId == orderId, cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy đơn hàng kiểm thử.");
        EnsureE2EOrder(order);

        var externalEventId = $"fake-paid-{order.TestRunId}";
        var payload = $"{externalEventId}|{order.OrderId:N}|PAID|{order.PriceAmount:0.00}|{order.CurrencyCode}";
        var settlement = await settlementService.SettleAsync(
            new PurchaseSettlementRequest(
                order.OrderId,
                null,
                PaymentProviderCode,
                externalEventId,
                $"FAKE-{order.TestRunId}",
                "PAYMENT_SUCCEEDED",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
                order.PriceAmount,
                nowUtc,
                nowUtc,
                order.TestRunId,
                payload,
                actorAdminId,
                ipAddress,
                "E2E_PAYMENT_CONFIRMED",
                false),
            cancellationToken);
        return await GetRequiredRunAsync(orderId, settlement.Duplicate, cancellationToken);
    }

    public async Task<IReadOnlyList<AdminPurchaseTestRun>> GetRecentRunsAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var orderIds = await database.PurchaseOrders.AsNoTracking()
            .Where(item => item.SourceCode == SourceCode && item.TestRunId != null)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => item.OrderId)
            .Take(Math.Clamp(count, 1, 50))
            .ToArrayAsync(cancellationToken);
        var runs = new List<AdminPurchaseTestRun>(orderIds.Length);
        foreach (var orderId in orderIds)
        {
            runs.Add(await GetRequiredRunAsync(orderId, false, cancellationToken));
        }

        return runs;
    }

    private async Task<AdminPurchaseTestRun> GetRequiredRunAsync(
        Guid orderId,
        bool duplicateWebhook,
        CancellationToken cancellationToken)
    {
        var order = await database.PurchaseOrders.AsNoTracking()
            .Include(item => item.User)
            .Include(item => item.FakeCredential)
            .Include(item => item.ActivatedSubscription)
            .Include(item => item.PaymentEvents)
            .SingleOrDefaultAsync(item => item.OrderId == orderId, cancellationToken)
            ?? throw new InvalidOperationException("Không thể tải lại đơn hàng kiểm thử.");
        var activePlanCode = await database.UserSubscriptions.AsNoTracking()
            .Where(item => item.UserId == order.UserId
                && item.StatusCode == "ACTIVE"
                && item.StartsAtUtc <= DateTime.UtcNow
                && (item.EndsAtUtc == null || item.EndsAtUtc > DateTime.UtcNow))
            .OrderByDescending(item => item.StartsAtUtc)
            .Select(item => item.Plan.PlanCode)
            .FirstOrDefaultAsync(cancellationToken) ?? "FREE";
        return new AdminPurchaseTestRun(
            order.OrderId,
            order.OrderNumber,
            order.TestRunId!,
            order.UserId,
            order.User.Email,
            order.StatusCode,
            order.PlanCodeSnapshot,
            order.PlanNameSnapshot,
            order.PriceAmount,
            order.CurrencyCode,
            order.BillingPeriodDays,
            activePlanCode,
            order.FakeCredentialId,
            order.FakeCredential?.DisplayName,
            order.FakeCredential?.StatusCode,
            order.FakeCredential?.AllocationMode,
            order.ActivatedSubscriptionId,
            order.ExternalPaymentId,
            order.PaymentEvents.Count,
            duplicateWebhook,
            order.CreatedAtUtc,
            order.PaidAtUtc);
    }

    private async Task EnsureAdminAsync(Guid actorAdminId, CancellationToken cancellationToken)
    {
        if (!await database.Users.AsNoTracking().AnyAsync(
            item => item.UserId == actorAdminId
                && item.RoleCode == "ADMIN"
                && item.StatusCode == "ACTIVE"
                && item.DeletedAtUtc == null,
            cancellationToken))
        {
            throw new UnauthorizedAccessException("Phiên quản trị không hợp lệ.");
        }
    }

    private static void EnsureE2EOrder(PurchaseOrder order)
    {
        if (order.SourceCode != SourceCode
            || order.PaymentProviderCode != PaymentProviderCode
            || string.IsNullOrWhiteSpace(order.TestRunId)
            || !order.TestRunId.StartsWith("E2E_PURCHASE_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Chỉ đơn ADMIN_E2E mới được dùng cổng thanh toán giả.");
        }
    }
}

public sealed record AdminPurchaseTestRun(
    Guid OrderId,
    string OrderNumber,
    string RunId,
    Guid UserId,
    string Email,
    string OrderStatus,
    string PurchasedPlanCode,
    string PurchasedPlanName,
    decimal PriceAmount,
    string CurrencyCode,
    int BillingPeriodDays,
    string ActivePlanCode,
    Guid? FakeCredentialId,
    string? FakeCredentialName,
    string? FakeCredentialStatus,
    string? FakeCredentialAllocationMode,
    Guid? ActivatedSubscriptionId,
    string? ExternalPaymentId,
    int WebhookCount,
    bool DuplicateWebhook,
    DateTime CreatedAtUtc,
    DateTime? PaidAtUtc);

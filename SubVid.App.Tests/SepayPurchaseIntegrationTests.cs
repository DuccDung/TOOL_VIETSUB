using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubVid.Server.Cloud;
using SubVid.Server.Contracts;
using SubVid.Server.Data;
using SubVid.Server.Models;
using SubVid.Server.Purchases;

namespace SubVid.App.Tests;

[Collection("SQL Server integration")]
public sealed class SepayPurchaseIntegrationTests
{
    [Fact]
    public async Task Checkout_EnforcesCatalogPriceIdempotencyAndOwnership()
    {
        var scope = await PurchaseTestScope.CreateAsync();
        await using (scope)
        {
            var service = scope.CreateCheckoutService();
            var idempotencyKey = $"checkout-{Guid.NewGuid():N}";
            var request = new CreatePurchaseCheckoutRequest
            {
                PlanCode = scope.PaidPlan.PlanCode,
                ExpectedPriceAmount = scope.PaidPlan.PriceAmount,
                IdempotencyKey = idempotencyKey,
            };

            var created = await service.CreateAsync(scope.User1.UserId, request, "127.0.0.1", CancellationToken.None);
            var replay = await service.CreateAsync(scope.User1.UserId, request, "127.0.0.1", CancellationToken.None);

            Assert.False(created.ReusedExistingOrder);
            Assert.True(replay.ReusedExistingOrder);
            Assert.Equal(created.OrderId, replay.OrderId);
            Assert.Matches("^SUBVID-[0-9]{10}$", created.TransactionCode);
            Assert.Equal(scope.PaidPlan.PriceAmount, created.Amount);
            Assert.Equal("VND", created.Currency);
            Assert.Equal(TimeSpan.Zero, created.CreatedAtUtc.Offset);
            Assert.Equal(TimeSpan.Zero, created.ExpiresAtUtc.Offset);
            Assert.InRange(
                (created.ExpiresAtUtc - created.CreatedAtUtc).TotalMinutes,
                14.99,
                15.01);
            scope.Database.ChangeTracker.Clear();
            var statusAfterSqlReload = await service.GetAsync(
                scope.User1.UserId,
                created.OrderNumber,
                CancellationToken.None);
            Assert.NotNull(statusAfterSqlReload);
            Assert.Equal(TimeSpan.Zero, statusAfterSqlReload.ExpiresAtUtc.Offset);
            Assert.False(statusAfterSqlReload.IsExpired);
            Assert.Null(await service.GetAsync(scope.User2.UserId, created.OrderNumber, CancellationToken.None));
            var ownershipConflict = await Assert.ThrowsAsync<PurchaseException>(() =>
                service.CreateAsync(scope.User2.UserId, request, null, CancellationToken.None));
            Assert.Equal("IDEMPOTENCY_CONFLICT", ownershipConflict.Code);

            var stalePrice = await Assert.ThrowsAsync<PurchaseException>(() => service.CreateAsync(
                scope.User1.UserId,
                new CreatePurchaseCheckoutRequest
                {
                    PlanCode = scope.PaidPlan.PlanCode,
                    ExpectedPriceAmount = scope.PaidPlan.PriceAmount - 1,
                    IdempotencyKey = $"stale-{Guid.NewGuid():N}",
                },
                null,
                CancellationToken.None));
            Assert.Equal("PLAN_PRICE_CHANGED", stalePrice.Code);

            await AssertPlanRejectedAsync(service, scope.User1.UserId, scope.FreePlan, "PLAN_NOT_PURCHASABLE");
            await AssertPlanRejectedAsync(service, scope.User1.UserId, scope.PrivatePlan, "PLAN_NOT_AVAILABLE");
            await AssertPlanRejectedAsync(service, scope.User1.UserId, scope.InactivePlan, "PLAN_NOT_AVAILABLE");

            Assert.Equal(1, await scope.Database.PurchaseOrders.CountAsync(item => item.OrderId == created.OrderId));
            Assert.Equal(1, await scope.Database.PurchasePaymentTransactions.CountAsync(
                item => item.OrderId == created.OrderId));
        }
    }

    [Fact]
    public async Task Webhook_ReconcilesStrictlyAndSettlementIsIdempotentConcurrentLateAndAtomic()
    {
        var scope = await PurchaseTestScope.CreateAsync();
        await using (scope)
        {
            var checkout = scope.CreateCheckoutService();
            var first = await scope.CreateCheckoutAsync(checkout, "happy");
            scope.Database.ChangeTracker.Clear();
            var webhook = scope.CreateWebhookService(scope.Database);
            Assert.True(webhook.IsAuthorized(null, scope.Options.WebhookApiKey));
            Assert.True(webhook.IsAuthorized($"Apikey {scope.Options.WebhookApiKey}", null));
            Assert.True(webhook.IsAuthorized($"Bearer {scope.Options.WebhookApiKey}", null));
            Assert.False(webhook.IsAuthorized(null, null));
            Assert.False(webhook.IsAuthorized("Bearer wrong", null));

            var happyPayload = scope.Payload(1001, first, first.Amount);
            var happy = await webhook.ProcessAsync(happyPayload, Raw(happyPayload), "127.0.0.1", CancellationToken.None);
            var replay = await webhook.ProcessAsync(happyPayload, Raw(happyPayload), "127.0.0.1", CancellationToken.None);
            Assert.True(happy.Processed);
            Assert.Equal(PaymentWebhookResultCodes.Processed, happy.ResultCode);
            Assert.True(replay.Processed);

            scope.Database.ChangeTracker.Clear();
            var paidOrder = await scope.Database.PurchaseOrders.AsNoTracking()
                .SingleAsync(item => item.OrderId == first.OrderId);
            var paidPayment = await scope.Database.PurchasePaymentTransactions.AsNoTracking()
                .SingleAsync(item => item.OrderId == first.OrderId);
            Assert.Equal(PurchaseOrderStatuses.Paid, paidOrder.StatusCode);
            Assert.Equal(PurchasePaymentStatuses.Paid, paidPayment.StatusCode);
            Assert.NotNull(paidOrder.ActivatedSubscriptionId);
            Assert.Equal(1, await scope.Database.UserSubscriptions.CountAsync(item => item.UserId == scope.User1.UserId));
            Assert.Equal(1, await scope.Database.PaymentWebhookEvents.CountAsync(item =>
                item.ProviderCode == PurchaseCheckoutService.ProviderCode
                && item.ExternalEventId == "id:1001"));

            var outgoing = scope.Payload(1002, first, first.Amount) with { TransferType = "out" };
            var zero = scope.Payload(1003, first, 0m);
            var wrongAccount = scope.Payload(1004, first, first.Amount) with { AccountNumber = "999999" };
            var missingCode = scope.Payload(1005, first, first.Amount) with
            {
                Content = "khong co ma",
                Code = null,
                Description = "giao dich khong co ma",
            };
            var ambiguous = scope.Payload(1006, first, first.Amount) with
            {
                Content = $"{first.TransactionCode} SUBVID-9999999999",
            };
            Assert.Equal(PaymentWebhookResultCodes.Ignored,
                (await webhook.ProcessAsync(outgoing, Raw(outgoing), null, CancellationToken.None)).ResultCode);
            Assert.Equal(PaymentWebhookResultCodes.Ignored,
                (await webhook.ProcessAsync(zero, Raw(zero), null, CancellationToken.None)).ResultCode);
            Assert.Equal(PaymentWebhookResultCodes.Ignored,
                (await webhook.ProcessAsync(wrongAccount, Raw(wrongAccount), null, CancellationToken.None)).ResultCode);
            Assert.Equal(PaymentWebhookResultCodes.Unmatched,
                (await webhook.ProcessAsync(missingCode, Raw(missingCode), null, CancellationToken.None)).ResultCode);
            Assert.Equal(PaymentWebhookResultCodes.Ambiguous,
                (await webhook.ProcessAsync(ambiguous, Raw(ambiguous), null, CancellationToken.None)).ResultCode);

            var amountCheckout = await scope.CreateCheckoutAsync(checkout, "amount");
            scope.Database.ChangeTracker.Clear();
            var wrongAmount = scope.Payload(1007, amountCheckout, amountCheckout.Amount + 1);
            Assert.Equal(PaymentWebhookResultCodes.Ignored,
                (await webhook.ProcessAsync(wrongAmount, Raw(wrongAmount), null, CancellationToken.None)).ResultCode);

            var lateCheckout = await scope.CreateCheckoutAsync(checkout, "late");
            await scope.Database.PurchasePaymentTransactions
                .Where(item => item.OrderId == lateCheckout.OrderId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    item => item.ExpiresAtUtc,
                    DateTime.UtcNow.AddMinutes(-1)));
            scope.Database.ChangeTracker.Clear();
            var latePayload = scope.Payload(1008, lateCheckout, lateCheckout.Amount);
            var late = await webhook.ProcessAsync(latePayload, Raw(latePayload), null, CancellationToken.None);
            Assert.True(late.Processed);
            Assert.Equal("LATE_PAYMENT_ACCEPTED", await scope.Database.PaymentWebhookEvents.AsNoTracking()
                .Where(item => item.ExternalEventId == "id:1008")
                .Select(item => item.EventCode)
                .SingleAsync());

            var concurrentCheckout = await scope.CreateCheckoutAsync(checkout, "concurrent");
            scope.Database.ChangeTracker.Clear();
            var concurrentPayload = scope.Payload(1009, concurrentCheckout, concurrentCheckout.Amount);
            await using var database2 = PurchaseTestScope.CreateDatabase();
            var webhook2 = scope.CreateWebhookService(database2);
            var concurrentResults = await Task.WhenAll(
                webhook.ProcessAsync(concurrentPayload, Raw(concurrentPayload), null, CancellationToken.None),
                webhook2.ProcessAsync(concurrentPayload, Raw(concurrentPayload), null, CancellationToken.None));
            Assert.All(concurrentResults, item => Assert.True(item.Processed));
            scope.Database.ChangeTracker.Clear();
            Assert.Equal(3, await scope.Database.UserSubscriptions.CountAsync(item => item.UserId == scope.User1.UserId));
            Assert.Equal(1, await scope.Database.UserSubscriptions.CountAsync(item =>
                item.UserId == scope.User1.UserId && item.StatusCode == "ACTIVE"));
            Assert.Equal(1, await scope.Database.PaymentWebhookEvents.CountAsync(item =>
                item.ProviderCode == PurchaseCheckoutService.ProviderCode
                && item.ExternalEventId == "id:1009"));

            scope.Database.ServicePlanCloudPolicies.Add(new ServicePlanCloudPolicy
            {
                PlanId = scope.PaidPlan.PlanId,
                ProviderCode = "groq",
                AllocationMode = CloudCredentialAllocationModes.Dedicated,
                MonthlyTokenLimit = 100_000,
                AllowedModelsJson = "[\"*\"]",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await scope.Database.SaveChangesAsync();
            var subscriptionCountBeforeRollback = await scope.Database.UserSubscriptions
                .CountAsync(item => item.UserId == scope.User1.UserId);
            var rollbackCheckout = await scope.CreateCheckoutAsync(checkout, "rollback-key-unavailable");
            scope.Database.ChangeTracker.Clear();
            var rollbackPayload = scope.Payload(1010, rollbackCheckout, rollbackCheckout.Amount);
            var failed = await webhook.ProcessAsync(
                rollbackPayload,
                Raw(rollbackPayload),
                null,
                CancellationToken.None);
            Assert.False(failed.Processed);
            Assert.Equal(PaymentWebhookResultCodes.Failed, failed.ResultCode);
            scope.Database.ChangeTracker.Clear();
            Assert.Equal(PurchaseOrderStatuses.Pending, await scope.Database.PurchaseOrders.AsNoTracking()
                .Where(item => item.OrderId == rollbackCheckout.OrderId)
                .Select(item => item.StatusCode)
                .SingleAsync());
            Assert.Equal(PurchasePaymentStatuses.Pending, await scope.Database.PurchasePaymentTransactions.AsNoTracking()
                .Where(item => item.OrderId == rollbackCheckout.OrderId)
                .Select(item => item.StatusCode)
                .SingleAsync());
            Assert.Equal(subscriptionCountBeforeRollback, await scope.Database.UserSubscriptions.AsNoTracking()
                .CountAsync(item => item.UserId == scope.User1.UserId));
        }
    }

    private static async Task AssertPlanRejectedAsync(
        PurchaseCheckoutService service,
        Guid userId,
        ServicePlan plan,
        string code)
    {
        var exception = await Assert.ThrowsAsync<PurchaseException>(() => service.CreateAsync(
            userId,
            new CreatePurchaseCheckoutRequest
            {
                PlanCode = plan.PlanCode,
                ExpectedPriceAmount = plan.PriceAmount,
                IdempotencyKey = $"reject-{Guid.NewGuid():N}",
            },
            null,
            CancellationToken.None));
        Assert.Equal(code, exception.Code);
    }

    private static string Raw(SepayWebhookPayload payload) =>
        System.Text.Json.JsonSerializer.Serialize(payload);

    private sealed class PurchaseTestScope : IAsyncDisposable
    {
        private PurchaseTestScope(
            SubVidDbContext database,
            SepayOptions options,
            User user1,
            User user2,
            ServicePlan paidPlan,
            ServicePlan freePlan,
            ServicePlan privatePlan,
            ServicePlan inactivePlan)
        {
            Database = database;
            Options = options;
            User1 = user1;
            User2 = user2;
            PaidPlan = paidPlan;
            FreePlan = freePlan;
            PrivatePlan = privatePlan;
            InactivePlan = inactivePlan;
        }

        public SubVidDbContext Database { get; }
        public SepayOptions Options { get; }
        public User User1 { get; }
        public User User2 { get; }
        public ServicePlan PaidPlan { get; }
        public ServicePlan FreePlan { get; }
        public ServicePlan PrivatePlan { get; }
        public ServicePlan InactivePlan { get; }

        public static async Task<PurchaseTestScope> CreateAsync()
        {
            var database = CreateDatabase();
            var token = Guid.NewGuid().ToString("N");
            var nowUtc = DateTime.UtcNow;
            var user1 = User($"sepay.1.{token}@local.invalid", nowUtc);
            var user2 = User($"sepay.2.{token}@local.invalid", nowUtc);
            var paid = Plan($"PAY{token[..8]}", 129_000m, true, true, nowUtc);
            var free = Plan($"FRE{token[..8]}", 0m, true, true, nowUtc);
            var privatePlan = Plan($"PRI{token[..8]}", 99_000m, true, false, nowUtc);
            var inactive = Plan($"INA{token[..8]}", 99_000m, false, true, nowUtc);
            database.Users.AddRange(user1, user2);
            database.ServicePlans.AddRange(paid, free, privatePlan, inactive);
            await database.SaveChangesAsync();
            var options = new SepayOptions
            {
                ApiToken = string.Empty,
                ReceiverBankName = "Ngân hàng kiểm thử",
                ReceiverBankShortName = "TESTBANK",
                ReceiverAccountNumber = "0123456789",
                ReceiverAccountName = "SUBVID TEST",
                WebhookApiKey = $"webhook-{token}",
                TransferCodePrefix = "SUBVID",
                PaymentExpireMinutes = 15,
            };
            return new PurchaseTestScope(
                database,
                options,
                user1,
                user2,
                paid,
                free,
                privatePlan,
                inactive);
        }

        public PurchaseCheckoutService CreateCheckoutService(SubVidDbContext? database = null)
        {
            var selectedDatabase = database ?? Database;
            var options = Microsoft.Extensions.Options.Options.Create(Options);
            return new PurchaseCheckoutService(
                selectedDatabase,
                new PaymentReferenceCodeGenerator(selectedDatabase, options),
                new SepayGatewayClient(
                    new HttpClient(new NoNetworkHandler()),
                    options,
                    NullLogger<SepayGatewayClient>.Instance),
                options);
        }

        public SepayWebhookService CreateWebhookService(SubVidDbContext database)
        {
            var settlement = new PurchaseSettlementService(
                database,
                new CloudCredentialAllocationService(database));
            return new SepayWebhookService(
                database,
                settlement,
                Microsoft.Extensions.Options.Options.Create(Options),
                new TestHostEnvironment(),
                NullLogger<SepayWebhookService>.Instance);
        }

        public async Task<PurchaseCheckoutResponse> CreateCheckoutAsync(
            PurchaseCheckoutService service,
            string name) => await service.CreateAsync(
                User1.UserId,
                new CreatePurchaseCheckoutRequest
                {
                    PlanId = PaidPlan.PlanId,
                    PlanCode = PaidPlan.PlanCode,
                    ExpectedPriceAmount = PaidPlan.PriceAmount,
                    IdempotencyKey = $"{name}-{Guid.NewGuid():N}",
                },
                null,
                CancellationToken.None);

        public SepayWebhookPayload Payload(long id, PurchaseCheckoutResponse checkout, decimal amount) => new()
        {
            Id = id,
            Gateway = Options.ReceiverBankShortName,
            TransactionDate = DateTime.UtcNow.ToString("O"),
            AccountNumber = Options.ReceiverAccountNumber,
            Code = checkout.TransactionCode,
            Content = checkout.TransferContent,
            TransferType = "in",
            TransferAmount = amount,
            Accumulated = amount,
            ReferenceCode = $"SEPAY-{id}",
            Description = $"Thanh toan {checkout.TransactionCode}",
        };

        public async ValueTask DisposeAsync()
        {
            Database.ChangeTracker.Clear();
            var userIds = new[] { User1.UserId, User2.UserId };
            var planIds = new[] { PaidPlan.PlanId, FreePlan.PlanId, PrivatePlan.PlanId, InactivePlan.PlanId };
            var orderIds = await Database.PurchaseOrders.AsNoTracking()
                .Where(item => userIds.Contains(item.UserId))
                .Select(item => item.OrderId)
                .ToArrayAsync();
            await Database.PaymentWebhookEvents
                .Where(item => item.OrderId.HasValue && orderIds.Contains(item.OrderId.Value))
                .ExecuteDeleteAsync();
            await Database.PurchasePaymentTransactions
                .Where(item => orderIds.Contains(item.OrderId))
                .ExecuteDeleteAsync();
            await Database.PurchaseOrders
                .Where(item => orderIds.Contains(item.OrderId))
                .ExecuteDeleteAsync();
            await Database.SecurityAuditLogs
                .Where(item => item.UserId.HasValue && userIds.Contains(item.UserId.Value))
                .ExecuteDeleteAsync();
            await Database.CloudCredentialAllocationHistory
                .Where(item => item.AssignedUserId.HasValue && userIds.Contains(item.AssignedUserId.Value))
                .ExecuteDeleteAsync();
            await Database.UserSubscriptions
                .Where(item => userIds.Contains(item.UserId))
                .ExecuteDeleteAsync();
            await Database.ServicePlanCloudPolicies
                .Where(item => planIds.Contains(item.PlanId))
                .ExecuteDeleteAsync();
            await Database.ServicePlans
                .Where(item => planIds.Contains(item.PlanId))
                .ExecuteDeleteAsync();
            await Database.Users
                .Where(item => userIds.Contains(item.UserId))
                .ExecuteDeleteAsync();
            await Database.DisposeAsync();
        }

        public static SubVidDbContext CreateDatabase()
        {
            var options = new DbContextOptionsBuilder<SubVidDbContext>()
                .UseSqlServer(TestDatabase.ConnectionString)
                .EnableDetailedErrors()
                .Options;
            return new SubVidDbContext(options);
        }

        private static User User(string email, DateTime nowUtc) => new()
        {
            UserId = Guid.NewGuid(),
            Email = email,
            EmailNormalized = email.ToUpperInvariant(),
            PasswordHash = "not-used-by-sepay-test",
            DisplayName = "SePay integration test",
            RoleCode = "USER",
            StatusCode = "ACTIVE",
            EmailConfirmed = true,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

        private static ServicePlan Plan(
            string code,
            decimal price,
            bool active,
            bool isPublic,
            DateTime nowUtc) => new()
        {
            PlanId = Guid.NewGuid(),
            PlanCode = code,
            DisplayName = $"Test {code}",
            Description = "Temporary SePay integration plan",
            FeaturesJson = "[]",
            PriceAmount = price,
            CurrencyCode = "VND",
            BillingPeriodDays = 30,
            IsPublic = isPublic,
            IsActive = active,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "Unit/integration tests must not call SePay over the network.");
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SubVid.App.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

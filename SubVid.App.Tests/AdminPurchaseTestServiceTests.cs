using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Cloud;
using SubVid.Server.Data;
using SubVid.Server.Models;
using SubVid.Server.Purchases;

namespace SubVid.App.Tests;

[Collection("SQL Server integration")]
public sealed class AdminPurchaseTestServiceTests
{
    [Fact]
    public async Task FullProFlow_IsPaidAndRepeatedWebhookIsIdempotent()
    {
        Guid? userId = null;
        Guid? orderId = null;
        Guid? credentialId = null;
        var keyRing = Path.Combine(Path.GetTempPath(), "SUBVID_PURCHASE_E2E_TESTS", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyRing);
        try
        {
            await using var database = CreateDatabase();
            var adminId = await database.Users.AsNoTracking()
                .Where(item => item.RoleCode == "ADMIN"
                    && item.StatusCode == "ACTIVE"
                    && item.DeletedAtUtc == null)
                .Select(item => item.UserId)
                .FirstAsync();
            var protector = new CloudCredentialProtector(DataProtectionProvider.Create(keyRing));
            var allocationService = new CloudCredentialAllocationService(database);
            var service = new AdminPurchaseTestService(
                database,
                new PasswordHasher<User>(),
                protector,
                new PurchaseSettlementService(database, allocationService));

            var pending = await service.CreatePendingProPurchaseAsync(
                adminId,
                "127.0.0.1",
                CancellationToken.None);
            userId = pending.UserId;
            orderId = pending.OrderId;
            credentialId = pending.FakeCredentialId;
            Assert.Equal(PurchaseOrderStatuses.Pending, pending.OrderStatus);
            Assert.Equal("FREE", pending.ActivePlanCode);
            Assert.Equal("DISABLED", pending.FakeCredentialStatus);
            Assert.Equal(CloudCredentialAllocationModes.Unassigned, pending.FakeCredentialAllocationMode);

            var paid = await service.ProcessSuccessfulFakeWebhookAsync(
                adminId,
                pending.OrderId,
                "127.0.0.1",
                CancellationToken.None);
            var replay = await service.ProcessSuccessfulFakeWebhookAsync(
                adminId,
                pending.OrderId,
                "127.0.0.1",
                CancellationToken.None);

            Assert.Equal(PurchaseOrderStatuses.Paid, paid.OrderStatus);
            Assert.Equal("PRO", paid.ActivePlanCode);
            Assert.NotNull(paid.ActivatedSubscriptionId);
            Assert.Single(await database.PaymentWebhookEvents.AsNoTracking()
                .Where(item => item.OrderId == pending.OrderId)
                .ToArrayAsync());
            Assert.True(replay.DuplicateWebhook);
            Assert.Equal(paid.ActivatedSubscriptionId, replay.ActivatedSubscriptionId);
            Assert.Equal(1, await database.UserSubscriptions.AsNoTracking().CountAsync(item =>
                item.UserId == pending.UserId
                && item.StatusCode == "ACTIVE"
                && item.Plan.PlanCode == "PRO"));
            var protectedFakeCredential = await database.CloudProviderCredentials.AsNoTracking()
                .SingleAsync(item => item.CredentialId == pending.FakeCredentialId);
            Assert.Equal("DISABLED", protectedFakeCredential.StatusCode);
            Assert.Equal(CloudCredentialAllocationModes.Unassigned, protectedFakeCredential.AllocationMode);
            Assert.Null(protectedFakeCredential.AssignedUserId);
            Assert.False(await database.CloudUsageReservations.AsNoTracking()
                .AnyAsync(item => item.UserId == pending.UserId));
            Assert.False(await database.CloudUsageLedger.AsNoTracking()
                .AnyAsync(item => item.UserId == pending.UserId));
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            if (orderId is Guid savedOrderId)
            {
                await cleanup.PaymentWebhookEvents
                    .Where(item => item.OrderId == savedOrderId)
                    .ExecuteDeleteAsync();
                await cleanup.PurchaseOrders
                    .Where(item => item.OrderId == savedOrderId)
                    .ExecuteDeleteAsync();
            }
            if (userId is Guid savedUserId)
            {
                await cleanup.SecurityAuditLogs
                    .Where(item => item.UserId == savedUserId)
                    .ExecuteDeleteAsync();
                await cleanup.CloudUsageLedger
                    .Where(item => item.UserId == savedUserId)
                    .ExecuteDeleteAsync();
                await cleanup.CloudUsageReservations
                    .Where(item => item.UserId == savedUserId)
                    .ExecuteDeleteAsync();
                await cleanup.CloudQuotaLimits
                    .Where(item => item.UserId == savedUserId)
                    .ExecuteDeleteAsync();
                await cleanup.UserSubscriptions
                    .Where(item => item.UserId == savedUserId)
                    .ExecuteDeleteAsync();
            }
            if (credentialId is Guid savedCredentialId)
            {
                await cleanup.CloudCredentialAllocationHistory
                    .Where(item => item.CredentialId == savedCredentialId)
                    .ExecuteDeleteAsync();
                await cleanup.CloudProviderCredentials
                    .Where(item => item.CredentialId == savedCredentialId)
                    .ExecuteDeleteAsync();
            }
            if (userId is Guid finalUserId)
            {
                await cleanup.Users
                    .Where(item => item.UserId == finalUserId)
                    .ExecuteDeleteAsync();
            }
            Directory.Delete(keyRing, true);
        }
    }

    private static SubVidDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<SubVidDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .EnableDetailedErrors()
            .Options;
        return new SubVidDbContext(options);
    }
}

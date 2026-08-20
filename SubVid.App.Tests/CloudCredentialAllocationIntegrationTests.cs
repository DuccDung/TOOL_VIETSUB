using System.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubVid.Server.Auth;
using SubVid.Server.Cloud;
using SubVid.Server.Contracts;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.App.Tests;

[Collection("SQL Server integration")]
public sealed class CloudCredentialAllocationIntegrationTests
{
    private static string ConnectionString => TestDatabase.ConnectionString;

    [Fact]
    public async Task SynchronizeForPlan_AssignsAndReleasesPlanManagedDedicatedKey()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var planCode = $"TEST_{planId:N}"[..30];
        var nowUtc = DateTime.UtcNow;
        try
        {
            await using (var setup = CreateDatabase())
            {
                setup.Users.Add(new User
                {
                    UserId = userId,
                    Email = $"allocation-{userId:N}@local.invalid",
                    PasswordHash = "integration-test-not-a-login",
                    DisplayName = "Allocation integration test",
                    RoleCode = "USER",
                    StatusCode = "ACTIVE",
                    EmailConfirmed = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.ServicePlans.Add(new ServicePlan
                {
                    PlanId = planId,
                    PlanCode = planCode,
                    DisplayName = "Dedicated test plan",
                    MonthlyQuotaMinutes = 100,
                    MaxVideoMinutes = 100,
                    FeaturesJson = "[\"subtitle.translate\"]",
                    IsActive = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.ServicePlanCloudPolicies.Add(new ServicePlanCloudPolicy
                {
                    PlanId = planId,
                    ProviderCode = "groq",
                    AllocationMode = CloudCredentialAllocationModes.Dedicated,
                    MonthlyTokenLimit = 100_000,
                    AllowedModelsJson = "[\"*\"]",
                    IsActive = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.CloudProviderCredentials.Add(new CloudProviderCredential
                {
                    CredentialId = credentialId,
                    ProviderCode = "groq",
                    DisplayName = "Unassigned allocation test key",
                    EncryptedApiKey = "integration-test-protected-value",
                    KeyFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                    KeySuffix = "alloc123",
                    AllocationMode = CloudCredentialAllocationModes.Unassigned,
                    StatusCode = "ACTIVE",
                    Priority = 1,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                await setup.SaveChangesAsync();
            }

            await using (var database = CreateDatabase())
            await using (var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable))
            {
                var result = await new CloudCredentialAllocationService(database)
                    .SynchronizeForPlanAsync(
                        userId,
                        planId,
                        null,
                        "Integration test assign.",
                        CancellationToken.None);
                await database.SaveChangesAsync();
                await transaction.CommitAsync();
                Assert.Contains(credentialId, result.AllocatedCredentialIds);
            }

            await using (var verify = CreateDatabase())
            {
                var credential = await verify.CloudProviderCredentials.AsNoTracking()
                    .SingleAsync(item => item.CredentialId == credentialId);
                Assert.Equal(CloudCredentialAllocationModes.Dedicated, credential.AllocationMode);
                Assert.Equal(userId, credential.AssignedUserId);
                Assert.Equal(planId, credential.AllocationPlanId);
                Assert.Equal(CloudCredentialAllocationSources.Plan, credential.AllocationSourceCode);
            }

            await using (var database = CreateDatabase())
            await using (var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable))
            {
                var freePlanId = await database.ServicePlans
                    .Where(item => item.PlanCode == "FREE")
                    .Select(item => item.PlanId)
                    .SingleAsync();
                var result = await new CloudCredentialAllocationService(database)
                    .SynchronizeForPlanAsync(
                        userId,
                        freePlanId,
                        null,
                        "Integration test release.",
                        CancellationToken.None);
                await database.SaveChangesAsync();
                await transaction.CommitAsync();
                Assert.Contains(credentialId, result.ReleasedCredentialIds);
            }

            await using (var verify = CreateDatabase())
            {
                var credential = await verify.CloudProviderCredentials.AsNoTracking()
                    .SingleAsync(item => item.CredentialId == credentialId);
                Assert.Equal(CloudCredentialAllocationModes.Unassigned, credential.AllocationMode);
                Assert.Null(credential.AssignedUserId);
                Assert.Null(credential.PoolId);
                Assert.Null(credential.AllocationSourceCode);
                Assert.Equal(2, await verify.CloudCredentialAllocationHistory.CountAsync(
                    item => item.CredentialId == credentialId));
            }
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            await cleanup.CloudCredentialAllocationHistory
                .Where(item => item.CredentialId == credentialId)
                .ExecuteDeleteAsync();
            await cleanup.CloudProviderCredentials
                .Where(item => item.CredentialId == credentialId)
                .ExecuteDeleteAsync();
            await cleanup.ServicePlanCloudPolicies
                .Where(item => item.PlanId == planId)
                .ExecuteDeleteAsync();
            await cleanup.UserSubscriptions
                .Where(item => item.UserId == userId)
                .ExecuteDeleteAsync();
            await cleanup.ServicePlans
                .Where(item => item.PlanId == planId)
                .ExecuteDeleteAsync();
            await cleanup.Users
                .Where(item => item.UserId == userId)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Authorize_DoesNotIssueUnassignedKey()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var planCode = $"TEST_{planId:N}"[..30];
        var nowUtc = DateTime.UtcNow;
        var keyRing = Path.Combine(Path.GetTempPath(), "SUBVID_DP_TESTS", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyRing);
        var protector = new CloudCredentialProtector(DataProtectionProvider.Create(keyRing));
        var secret = $"unassigned-{Guid.NewGuid():N}";
        try
        {
            await using (var setup = CreateDatabase())
            {
                setup.Users.Add(new User
                {
                    UserId = userId,
                    Email = $"unassigned-{userId:N}@local.invalid",
                    PasswordHash = "integration-test-not-a-login",
                    DisplayName = "Unassigned key integration test",
                    RoleCode = "USER",
                    StatusCode = "ACTIVE",
                    EmailConfirmed = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.ServicePlans.Add(new ServicePlan
                {
                    PlanId = planId,
                    PlanCode = planCode,
                    DisplayName = "Unassigned key test plan",
                    MonthlyQuotaMinutes = 100,
                    MaxVideoMinutes = 100,
                    FeaturesJson = "[\"subtitle.translate\"]",
                    IsActive = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.ServicePlanCloudPolicies.Add(new ServicePlanCloudPolicy
                {
                    PlanId = planId,
                    ProviderCode = "groq",
                    AllocationMode = CloudCredentialAllocationModes.Shared,
                    MonthlyTokenLimit = 100_000,
                    AllowedModelsJson = "[\"*\"]",
                    IsActive = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.UserSubscriptions.Add(new UserSubscription
                {
                    SubscriptionId = subscriptionId,
                    UserId = userId,
                    PlanId = planId,
                    StatusCode = "ACTIVE",
                    StartsAtUtc = nowUtc,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.CloudProviderCredentials.Add(new CloudProviderCredential
                {
                    CredentialId = credentialId,
                    ProviderCode = "groq",
                    DisplayName = "Must remain in inventory",
                    EncryptedApiKey = protector.Protect(secret),
                    KeyFingerprint = CloudCredentialProtector.Fingerprint(secret),
                    KeySuffix = CloudCredentialProtector.Suffix(secret),
                    AllocationMode = CloudCredentialAllocationModes.Unassigned,
                    StatusCode = "ACTIVE",
                    Priority = 0,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                await setup.SaveChangesAsync();
            }

            await using var database = CreateDatabase();
            var service = new CloudAccessService(
                database,
                new EntitlementService(database),
                protector,
                Options.Create(new CloudAccessOptions()));
            var result = await service.AuthorizeAsync(
                userId,
                new AuthorizeCloudAccessRequest(
                    Guid.NewGuid(),
                    null,
                    Guid.NewGuid(),
                    "TRANSLATION",
                    "groq",
                    "openai/gpt-oss-20b",
                    100,
                    100),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("CLOUD_CREDENTIAL_UNAVAILABLE", result.ErrorCode);
            Assert.False(await database.CloudUsageReservations.AnyAsync(item => item.UserId == userId));
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            await cleanup.CloudUsageLedger.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.CloudUsageReservations.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.CloudProviderCredentials.Where(item => item.CredentialId == credentialId).ExecuteDeleteAsync();
            await cleanup.UserSubscriptions.Where(item => item.SubscriptionId == subscriptionId).ExecuteDeleteAsync();
            await cleanup.ServicePlanCloudPolicies.Where(item => item.PlanId == planId).ExecuteDeleteAsync();
            await cleanup.ServicePlans.Where(item => item.PlanId == planId).ExecuteDeleteAsync();
            await cleanup.Users.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            if (Directory.Exists(keyRing))
            {
                Directory.Delete(keyRing, recursive: true);
            }
        }
    }

    private static SubVidDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<SubVidDbContext>()
            .UseSqlServer(ConnectionString)
            .EnableDetailedErrors()
            .Options;
        return new SubVidDbContext(options);
    }
}

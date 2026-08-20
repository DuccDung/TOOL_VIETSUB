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
public sealed class CloudAccessIntegrationTests : IDisposable
{
    private static string ConnectionString => TestDatabase.ConnectionString;
    private readonly string _keyRing = Path.Combine(Path.GetTempPath(), "SUBVID_DP_TESTS", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AuthorizeAndCommit_AreIdempotent_AndLedgerIsAppendOnly()
    {
        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var email = $"cloud-access-{userId:N}@local.invalid";
        var apiKey = $"test-secret-{Guid.NewGuid():N}";
        var requestId = Guid.NewGuid();
        Guid reservationId = Guid.Empty;
        Directory.CreateDirectory(_keyRing);
        var protector = new CloudCredentialProtector(DataProtectionProvider.Create(_keyRing));

        try
        {
            await using (var setup = CreateDatabase())
            {
                var nowUtc = DateTime.UtcNow;
                setup.Users.Add(new User
                {
                    UserId = userId,
                    Email = email,
                    PasswordHash = "integration-test-not-a-login",
                    DisplayName = "Cloud access integration test",
                    RoleCode = "USER",
                    StatusCode = "ACTIVE",
                    EmailConfirmed = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.CloudQuotaLimits.Add(new CloudQuotaLimit
                {
                    UserId = userId,
                    UnitCode = CloudUsageUnits.LlmToken,
                    MonthlyLimit = 100_000,
                    UpdatedAtUtc = nowUtc,
                });
                setup.CloudProviderCredentials.Add(new CloudProviderCredential
                {
                    CredentialId = credentialId,
                    ProviderCode = "groq",
                    DisplayName = "Integration key",
                    EncryptedApiKey = protector.Protect(apiKey),
                    KeyFingerprint = CloudCredentialProtector.Fingerprint(apiKey),
                    KeySuffix = CloudCredentialProtector.Suffix(apiKey),
                    AssignedUserId = userId,
                    AllocationMode = CloudCredentialAllocationModes.Dedicated,
                    AllocationSourceCode = CloudCredentialAllocationSources.Admin,
                    AllocatedAtUtc = nowUtc,
                    StatusCode = "ACTIVE",
                    Priority = 1,
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
                Options.Create(new CloudAccessOptions { ReservationLifetimeMinutes = 45 }));
            var request = new AuthorizeCloudAccessRequest(
                requestId,
                null,
                Guid.NewGuid(),
                "TRANSLATION",
                "groq",
                "openai/gpt-oss-20b",
                1200,
                800);

            var authorized = await service.AuthorizeAsync(userId, request, CancellationToken.None);
            var duplicateAuthorization = await service.AuthorizeAsync(userId, request, CancellationToken.None);

            Assert.True(authorized.Succeeded, authorized.ErrorMessage);
            Assert.Equal(apiKey, authorized.Value!.ApiKey);
            Assert.False(authorized.Value.Duplicate);
            reservationId = authorized.Value.ReservationId;
            Assert.True(duplicateAuthorization.Succeeded, duplicateAuthorization.ErrorMessage);
            Assert.True(duplicateAuthorization.Value!.Duplicate);
            Assert.Equal(reservationId, duplicateAuthorization.Value.ReservationId);

            var usage = new CommitCloudUsageRequest(900, 300, 100, 1, 0, "provider-request-1");
            var committed = await service.CommitAsync(userId, reservationId, usage, CancellationToken.None);
            var duplicateCommit = await service.CommitAsync(userId, reservationId, usage, CancellationToken.None);

            Assert.True(committed.Succeeded, committed.ErrorMessage);
            Assert.Equal("COMMITTED", committed.Value!.Status);
            Assert.Equal(1200, committed.Value.CommittedUnits);
            Assert.True(duplicateCommit.Succeeded, duplicateCommit.ErrorMessage);
            Assert.True(duplicateCommit.Value!.Duplicate);
            Assert.Equal(1, await database.CloudUsageLedger.CountAsync(item => item.ReservationId == reservationId));
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            await cleanup.CloudUsageLedger.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.CloudUsageReservations.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.CloudQuotaLimits.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.CloudProviderCredentials.Where(item => item.CredentialId == credentialId).ExecuteDeleteAsync();
            await cleanup.Users.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        }
    }

    private static SubVidDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<SubVidDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        return new SubVidDbContext(options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_keyRing))
        {
            Directory.Delete(_keyRing, recursive: true);
        }
    }
}

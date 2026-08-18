using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Auth;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.App.Tests;

[Collection("SQL Server integration")]
public sealed class AdminUserServiceTests
{
    [Fact]
    public async Task DetailAndStatusActions_AggregateUserAndRevokeSessions()
    {
        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var cloudReservationId = Guid.NewGuid();
        var email = $"admin-user-test-{userId:N}@local.invalid";
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
                    DisplayName = "Admin user integration test",
                    RoleCode = "USER",
                    StatusCode = "ACTIVE",
                    EmailConfirmed = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.AuthSessions.Add(new AuthSession
                {
                    SessionId = Guid.NewGuid(),
                    UserId = userId,
                    RefreshTokenHash = RandomNumberGenerator.GetBytes(32),
                    DeviceId = $"test-{userId:N}",
                    DeviceName = "Integration test",
                    CreatedAtUtc = nowUtc,
                    LastSeenAtUtc = nowUtc,
                    ExpiresAtUtc = nowUtc.AddDays(1),
                });
                setup.CloudProviderCredentials.Add(new CloudProviderCredential
                {
                    CredentialId = credentialId,
                    ProviderCode = "openai",
                    DisplayName = "User dedicated test key",
                    EncryptedApiKey = "integration-test-protected-value",
                    KeyFingerprint = userId.ToString("N") + userId.ToString("N"),
                    KeySuffix = "test1234",
                    AssignedUserId = userId,
                    StatusCode = "ACTIVE",
                    Priority = 10,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                var periodStartUtc = new DateTime(
                    nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                setup.CloudUsageReservations.Add(new CloudUsageReservation
                {
                    ReservationId = cloudReservationId,
                    RequestId = Guid.NewGuid(),
                    UserId = userId,
                    CredentialId = credentialId,
                    OperationCode = "subtitle.translate",
                    ProviderCode = "openai",
                    ModelId = "gpt-4o-mini",
                    UnitCode = "LLM_TOKEN",
                    StatusCode = "COMMITTED",
                    EstimatedInputUnits = 90,
                    EstimatedOutputUnits = 20,
                    ReservedUnits = 110,
                    CommittedUnits = 110,
                    QuotaPeriodStartUtc = periodStartUtc,
                    ExpiresAtUtc = nowUtc.AddMinutes(45),
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                    CommittedAtUtc = nowUtc,
                });
                setup.CloudUsageLedger.Add(new CloudUsageLedger
                {
                    LedgerId = Guid.NewGuid(),
                    ReservationId = cloudReservationId,
                    UserId = userId,
                    CredentialId = credentialId,
                    ProviderCode = "openai",
                    ModelId = "gpt-4o-mini",
                    OperationCode = "subtitle.translate",
                    UnitCode = "LLM_TOKEN",
                    InputUnits = 90,
                    OutputUnits = 20,
                    CachedInputUnits = 0,
                    TotalUnits = 110,
                    ApiRequestCount = 1,
                    RetryRequestCount = 0,
                    QuotaPeriodStartUtc = periodStartUtc,
                    OccurredAtUtc = nowUtc,
                    CreatedAtUtc = nowUtc,
                });
                await setup.SaveChangesAsync();
            }

            await using var database = CreateDatabase();
            var adminId = await database.Users.AsNoTracking()
                .Where(item => item.RoleCode == "ADMIN"
                    && item.StatusCode == "ACTIVE"
                    && item.DeletedAtUtc == null)
                .Select(item => item.UserId)
                .FirstAsync();
            var service = new AdminUserService(database);

            var result = await service.GetUsersAsync(
                new AdminUserListQuery(email, null, null, null, 1, 20),
                CancellationToken.None);
            var filtered = await service.GetUsersAsync(
                new AdminUserListQuery(email, "ACTIVE", "FREE", "token", 1, 20),
                CancellationToken.None);
            var detail = await service.GetDetailAsync(userId, CancellationToken.None);

            Assert.Single(result.Items);
            Assert.Single(filtered.Items);
            Assert.NotNull(detail);
            Assert.Equal("FREE", detail.CurrentPlan.PlanCode);
            Assert.Single(detail.Sessions);
            Assert.Single(detail.AssignedCredentials);
            Assert.Equal("openai", detail.AssignedCredentials[0].ProviderCode);
            Assert.Single(detail.CloudLedger);
            Assert.Equal(credentialId, detail.CloudLedger[0].CredentialId);
            Assert.Equal("User dedicated test key", detail.CloudLedger[0].CredentialName);

            await service.SetMinuteQuotaAsync(
                adminId, userId, 321, "127.0.0.1", CancellationToken.None);
            await service.SetAccountStatusAsync(
                adminId, userId, "SUSPENDED", "Integration test", "127.0.0.1", CancellationToken.None);

            Assert.Equal("SUSPENDED", await database.Users
                .Where(item => item.UserId == userId)
                .Select(item => item.StatusCode)
                .SingleAsync());
            Assert.Equal(321, await database.Users
                .Where(item => item.UserId == userId)
                .Select(item => item.MonthlyQuotaMinutes)
                .SingleAsync());
            Assert.True(await database.AuthSessions.AnyAsync(item =>
                item.UserId == userId && item.RevokedAtUtc != null));
            Assert.True(await database.SecurityAuditLogs.AnyAsync(item =>
                item.UserId == userId && item.EventCode == "ADMIN_USER_STATUS_CHANGE"));
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            await cleanup.SecurityAuditLogs.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.AuthSessions.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.CloudUsageLedger.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.CloudUsageReservations.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.CloudProviderCredentials.Where(item => item.AssignedUserId == userId).ExecuteDeleteAsync();
            await cleanup.Users.Where(item => item.UserId == userId).ExecuteDeleteAsync();
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

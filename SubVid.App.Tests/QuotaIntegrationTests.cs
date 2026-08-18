using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubVid.Server.Auth;
using SubVid.Server.Contracts;
using SubVid.Server.Data;
using SubVid.Server.Models;
using SubVid.Server.Usage;

namespace SubVid.App.Tests;

[Collection("SQL Server integration")]
public sealed class QuotaIntegrationTests
{
    private static string ConnectionString => TestDatabase.ConnectionString;

    [Fact]
    public async Task Reservation_IsIdempotentAndCommitRecordsActualUsageOnce()
    {
        var projectId = Guid.NewGuid();
        var firstRequestId = Guid.NewGuid();
        var secondRequestId = Guid.NewGuid();
        Guid firstReservationId = Guid.Empty;
        Guid secondReservationId = Guid.Empty;

        try
        {
            await using var database = CreateDatabase();
            var user = await database.Users.AsNoTracking().SingleAsync(
                item => item.EmailNormalized == "ADMIN@TOOLVIETSUB.LOCAL");
            var nowUtc = DateTime.UtcNow;
            database.Projects.Add(new Project
            {
                ProjectId = projectId,
                OwnerUserId = user.UserId,
                ProjectName = "__QUOTA_INTEGRATION_TEST__",
                StatusCode = "DRAFT",
                TargetLanguageCode = "vi",
                CurrentTranscriptVersion = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            });
            await database.SaveChangesAsync();

            var service = new QuotaService(
                database,
                new EntitlementService(database),
                Options.Create(new QuotaOptions { ReservationLifetimeMinutes = 120 }));
            var firstRequest = new ReserveQuotaRequest
            {
                RequestId = firstRequestId,
                ProjectId = projectId,
                LocalJobId = Guid.NewGuid(),
                FeatureCode = "subtitle.transcribe",
                EstimatedMinutes = 2.5m,
            };

            var reserved = await service.ReserveAsync(user.UserId, firstRequest, CancellationToken.None);
            Assert.True(reserved.Succeeded, reserved.ErrorMessage);
            firstReservationId = reserved.Value!.ReservationId;

            var duplicateReserve = await service.ReserveAsync(user.UserId, firstRequest, CancellationToken.None);
            Assert.True(duplicateReserve.Succeeded, duplicateReserve.ErrorMessage);
            Assert.True(duplicateReserve.Value!.Duplicate);
            Assert.Equal(firstReservationId, duplicateReserve.Value.ReservationId);

            var released = await service.ReleaseAsync(user.UserId, firstReservationId, CancellationToken.None);
            Assert.True(released.Succeeded, released.ErrorMessage);
            Assert.Equal("RELEASED", released.Value!.Status);

            var secondRequest = new ReserveQuotaRequest
            {
                RequestId = secondRequestId,
                ProjectId = projectId,
                LocalJobId = Guid.NewGuid(),
                FeatureCode = "subtitle.transcribe",
                EstimatedMinutes = 2m,
            };
            var secondReservation = await service.ReserveAsync(user.UserId, secondRequest, CancellationToken.None);
            Assert.True(secondReservation.Succeeded, secondReservation.ErrorMessage);
            secondReservationId = secondReservation.Value!.ReservationId;

            var committed = await service.CommitAsync(
                user.UserId,
                secondReservationId,
                1.5m,
                CancellationToken.None);
            var duplicateCommit = await service.CommitAsync(
                user.UserId,
                secondReservationId,
                1.5m,
                CancellationToken.None);

            Assert.True(committed.Succeeded, committed.ErrorMessage);
            Assert.Equal("COMMITTED", committed.Value!.Status);
            Assert.Equal(1.5m, committed.Value.CommittedMinutes);
            Assert.True(duplicateCommit.Succeeded, duplicateCommit.ErrorMessage);
            Assert.True(duplicateCommit.Value!.Duplicate);
            Assert.Equal(1, await database.UsageRecords.CountAsync(item =>
                item.ProviderCode == "DESKTOP_APP"
                && item.ExternalRequestId == secondReservationId.ToString("N")));
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            var reservationIds = new[] { firstReservationId, secondReservationId }
                .Where(item => item != Guid.Empty)
                .ToArray();
            if (reservationIds.Length > 0)
            {
                var externalIds = reservationIds.Select(item => item.ToString("N")).ToArray();
                await cleanup.UsageRecords
                    .Where(item => item.ProviderCode == "DESKTOP_APP"
                        && item.ExternalRequestId != null
                        && externalIds.Contains(item.ExternalRequestId))
                    .ExecuteDeleteAsync();
                await cleanup.UsageReservations
                    .Where(item => reservationIds.Contains(item.ReservationId))
                    .ExecuteDeleteAsync();
            }

            await cleanup.Projects
                .Where(item => item.ProjectId == projectId)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ConcurrentReservations_CannotExceedUserQuota()
    {
        var userId = Guid.NewGuid();
        var email = $"quota-test-{userId:N}@local.invalid";
        var requestIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
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
                    DisplayName = "Quota concurrency test",
                    RoleCode = "USER",
                    StatusCode = "ACTIVE",
                    MonthlyQuotaMinutes = 10,
                    EmailConfirmed = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                await setup.SaveChangesAsync();
            }

            var tasks = requestIds.Select(requestId => ReserveWithFreshContextAsync(
                userId,
                new ReserveQuotaRequest
                {
                    RequestId = requestId,
                    FeatureCode = "subtitle.transcribe",
                    EstimatedMinutes = 6,
                })).ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.Single(results, item => item.Succeeded);
            Assert.Single(results, item =>
                !item.Succeeded && item.ErrorCode == "QUOTA_INSUFFICIENT");
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            await cleanup.UsageReservations
                .Where(item => item.UserId == userId)
                .ExecuteDeleteAsync();
            await cleanup.Users
                .Where(item => item.UserId == userId)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task NewReservation_ExpiresOldHeldMinutesBeforeCheckingQuota()
    {
        var userId = Guid.NewGuid();
        var expiredReservationId = Guid.NewGuid();
        var email = $"quota-test-{userId:N}@local.invalid";
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
                    DisplayName = "Quota expiry test",
                    RoleCode = "USER",
                    StatusCode = "ACTIVE",
                    MonthlyQuotaMinutes = 10,
                    EmailConfirmed = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                setup.UsageReservations.Add(new UsageReservation
                {
                    ReservationId = expiredReservationId,
                    UserId = userId,
                    FeatureCode = "subtitle.transcribe",
                    StatusCode = "HELD",
                    EstimatedMinutes = 8,
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                    QuotaPeriodStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                    ExpiresAtUtc = nowUtc.AddMinutes(-1),
                    CreatedAtUtc = nowUtc.AddHours(-3),
                    UpdatedAtUtc = nowUtc.AddHours(-3),
                });
                await setup.SaveChangesAsync();
            }

            await using var database = CreateDatabase();
            var service = new QuotaService(
                database,
                new EntitlementService(database),
                Options.Create(new QuotaOptions { ReservationLifetimeMinutes = 120 }));
            var result = await service.ReserveAsync(
                userId,
                new ReserveQuotaRequest
                {
                    RequestId = Guid.NewGuid(),
                    FeatureCode = "subtitle.transcribe",
                    EstimatedMinutes = 6,
                },
                CancellationToken.None);

            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.Equal("EXPIRED", await database.UsageReservations
                .Where(item => item.ReservationId == expiredReservationId)
                .Select(item => item.StatusCode)
                .SingleAsync());
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            await cleanup.UsageReservations
                .Where(item => item.UserId == userId)
                .ExecuteDeleteAsync();
            await cleanup.Users
                .Where(item => item.UserId == userId)
                .ExecuteDeleteAsync();
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

    private static async Task<QuotaServiceResult> ReserveWithFreshContextAsync(
        Guid userId,
        ReserveQuotaRequest request)
    {
        await using var database = CreateDatabase();
        var service = new QuotaService(
            database,
            new EntitlementService(database),
            Options.Create(new QuotaOptions { ReservationLifetimeMinutes = 120 }));
        return await service.ReserveAsync(userId, request, CancellationToken.None);
    }
}

[CollectionDefinition("SQL Server integration", DisableParallelization = true)]
public sealed class SqlServerIntegrationCollection;

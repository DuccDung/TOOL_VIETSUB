using Microsoft.EntityFrameworkCore;
using SubVid.Server.Auth;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.App.Tests;

[Collection("SQL Server integration")]
public sealed class AdminSubscriptionServiceTests
{
    private const string ConnectionString =
        "Data Source=DUNGDEV;Initial Catalog=TOOL_VIETSUB;Integrated Security=True;Trust Server Certificate=True";

    [Fact]
    public async Task ChangePlanAsync_ActivatesSelectedPlanAndWritesAudit()
    {
        var userId = Guid.NewGuid();
        var email = $"admin-plan-test-{userId:N}@local.invalid";
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
                    DisplayName = "Subscription integration test",
                    RoleCode = "USER",
                    StatusCode = "ACTIVE",
                    EmailConfirmed = true,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
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
            var service = new AdminSubscriptionService(database);

            var before = await service.FindByEmailAsync(email, CancellationToken.None);
            var changedAtUtc = DateTime.UtcNow;
            var after = await service.ChangePlanAsync(
                adminId,
                email,
                "PRO",
                45,
                "127.0.0.1",
                CancellationToken.None);

            Assert.NotNull(before);
            Assert.Equal("FREE", before.PlanCode);
            Assert.Equal("PRO", after.PlanCode);
            Assert.NotNull(after.EndsAtUtc);
            Assert.InRange(after.EndsAtUtc!.Value, changedAtUtc.AddDays(44.9), changedAtUtc.AddDays(45.1));
            Assert.True(await database.UserSubscriptions.AnyAsync(item =>
                item.UserId == userId
                && item.StatusCode == "ACTIVE"
                && item.Plan.PlanCode == "PRO"));
            Assert.True(await database.SecurityAuditLogs.AnyAsync(item =>
                item.UserId == userId
                && item.EventCode == "ADMIN_SUBSCRIPTION_CHANGE"
                && item.OutcomeCode == "SUCCESS"));
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            await cleanup.SecurityAuditLogs
                .Where(item => item.UserId == userId)
                .ExecuteDeleteAsync();
            await cleanup.UserSubscriptions
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
}

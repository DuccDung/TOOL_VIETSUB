using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubVid.Server.Auth;
using SubVid.Server.Cloud;
using SubVid.Server.Data;
using SubVid.Server.Models;
using SubVid.Server.Pages.Admin.Users;

namespace SubVid.App.Tests;

[Collection("SQL Server integration")]
public sealed class AdminUserDetailPageTests : IDisposable
{
    private readonly string _keyRing = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_ADMIN_USER_DETAIL_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveCloudQuota_IgnoresUnrelatedFormErrors_AndCreatesQuotaWithAudit()
    {
        var userId = Guid.NewGuid();
        var email = $"admin-cloud-quota-{userId:N}@local.invalid";
        Directory.CreateDirectory(_keyRing);

        try
        {
            await CreateUserAsync(userId, email);
            await using var database = CreateDatabase();
            var adminId = await GetActiveAdminIdAsync(database);
            var page = CreatePage(database, adminId, userId, 10_000_000);
            page.ModelState.AddModelError(
                nameof(DetailModel.DedicatedCredentialName),
                "An unrelated modal field is invalid.");

            var result = await page.OnPostSaveCloudQuotaAsync(CancellationToken.None);

            var redirect = Assert.IsType<RedirectToPageResult>(result);
            Assert.Equal(userId, redirect.RouteValues?["id"]);
            Assert.Equal(10_000_000, await database.CloudQuotaLimits
                .Where(item => item.UserId == userId && item.UnitCode == CloudUsageUnits.LlmToken)
                .Select(item => item.MonthlyLimit)
                .SingleAsync());
            var balance = await CreateCloudAccess(database)
                .GetBalanceAsync(userId, CloudUsageUnits.LlmToken, CancellationToken.None);
            Assert.Equal(10_000_000, balance.MonthlyLimit);
            Assert.Equal(10_000_000, balance.RemainingUnits);
            Assert.True(await database.SecurityAuditLogs.AnyAsync(item =>
                item.UserId == userId
                && item.EventCode == "ADMIN_CLOUD_QUOTA_CHANGE"
                && item.OutcomeCode == "SUCCESS"));
        }
        finally
        {
            await CleanupAsync(userId);
        }
    }

    [Fact]
    public async Task SaveCloudQuota_WithTokenValidationError_LoadsDetailAndDoesNotWriteQuota()
    {
        var userId = Guid.NewGuid();
        var email = $"admin-cloud-invalid-{userId:N}@local.invalid";
        Directory.CreateDirectory(_keyRing);

        try
        {
            await CreateUserAsync(userId, email);
            await using var database = CreateDatabase();
            var adminId = await GetActiveAdminIdAsync(database);
            var page = CreatePage(database, adminId, userId, 10_000_000);
            page.ModelState.AddModelError(
                nameof(DetailModel.MonthlyLlmTokens),
                "Token value is invalid.");

            var result = await page.OnPostSaveCloudQuotaAsync(CancellationToken.None);

            Assert.IsType<PageResult>(result);
            Assert.Equal(userId, page.Detail.User.UserId);
            Assert.False(await database.CloudQuotaLimits.AnyAsync(item =>
                item.UserId == userId && item.UnitCode == CloudUsageUnits.LlmToken));
        }
        finally
        {
            await CleanupAsync(userId);
        }
    }

    private DetailModel CreatePage(
        SubVidDbContext database,
        Guid adminId,
        Guid userId,
        long monthlyLlmTokens)
    {
        var protector = new CloudCredentialProtector(DataProtectionProvider.Create(_keyRing));
        var cloudAccess = CreateCloudAccess(database, protector);
        var cloudService = new AdminCloudService(
            database,
            protector,
            cloudAccess,
            new CloudCredentialProbeService(new HttpClient()));
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, adminId.ToString("D"))],
            WebAdminAuthenticationDefaults.Scheme);
        var page = new DetailModel(
            new AdminUserService(database),
            cloudService,
            NullLogger<DetailModel>.Instance)
        {
            Id = userId,
            MonthlyLlmTokens = monthlyLlmTokens,
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity),
                },
            },
        };
        return page;
    }

    private CloudAccessService CreateCloudAccess(SubVidDbContext database) =>
        CreateCloudAccess(
            database,
            new CloudCredentialProtector(DataProtectionProvider.Create(_keyRing)));

    private static CloudAccessService CreateCloudAccess(
        SubVidDbContext database,
        CloudCredentialProtector protector) =>
        new(
            database,
            new EntitlementService(database),
            protector,
            Options.Create(new CloudAccessOptions()));

    private static async Task CreateUserAsync(Guid userId, string email)
    {
        await using var database = CreateDatabase();
        var nowUtc = DateTime.UtcNow;
        database.Users.Add(new User
        {
            UserId = userId,
            Email = email,
            EmailNormalized = email.ToUpperInvariant(),
            PasswordHash = "integration-test-not-a-login",
            DisplayName = "Admin Cloud quota page test",
            RoleCode = "USER",
            StatusCode = "ACTIVE",
            EmailConfirmed = true,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        });
        await database.SaveChangesAsync();
    }

    private static Task<Guid> GetActiveAdminIdAsync(SubVidDbContext database) =>
        database.Users.AsNoTracking()
            .Where(item => item.RoleCode == "ADMIN"
                && item.StatusCode == "ACTIVE"
                && item.DeletedAtUtc == null)
            .Select(item => item.UserId)
            .FirstAsync();

    private static async Task CleanupAsync(Guid userId)
    {
        await using var database = CreateDatabase();
        await database.SecurityAuditLogs.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await database.CloudUsageLedger.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await database.CloudUsageReservations.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await database.CloudQuotaLimits.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await database.CloudProviderCredentials.Where(item => item.AssignedUserId == userId).ExecuteDeleteAsync();
        await database.AuthSessions.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await database.Users.Where(item => item.UserId == userId).ExecuteDeleteAsync();
    }

    private static SubVidDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<SubVidDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .EnableDetailedErrors()
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

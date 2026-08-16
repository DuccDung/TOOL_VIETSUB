using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Auth;

public sealed class DevelopmentAdminSeeder(
    SubVidDbContext database,
    IPasswordHasher<User> passwordHasher,
    IConfiguration configuration,
    ILogger<DevelopmentAdminSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var email = configuration["BootstrapAdmin:Email"]?.Trim();
        var password = configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        if (password.Length < 12)
        {
            throw new InvalidOperationException(
                "BootstrapAdmin password must contain at least 12 characters.");
        }

        var normalizedEmail = email.ToUpperInvariant();
        if (await database.Users.AnyAsync(
            item => item.EmailNormalized == normalizedEmail && item.DeletedAtUtc == null,
            cancellationToken))
        {
            logger.LogInformation("Development bootstrap admin already exists.");
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = email,
            DisplayName = configuration["BootstrapAdmin:DisplayName"]?.Trim() ?? "Quản trị viên",
            RoleCode = "ADMIN",
            StatusCode = "ACTIVE",
            EmailConfirmed = true,
            PasswordChangedAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        database.Users.Add(user);

        var proPlan = await database.ServicePlans.SingleAsync(
            item => item.PlanCode == "PRO",
            cancellationToken);
        database.UserSubscriptions.Add(new UserSubscription
        {
            SubscriptionId = Guid.NewGuid(),
            UserId = user.UserId,
            PlanId = proPlan.PlanId,
            StatusCode = "ACTIVE",
            StartsAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        });

        await database.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Development bootstrap admin was created for {Email}.", email);
    }
}

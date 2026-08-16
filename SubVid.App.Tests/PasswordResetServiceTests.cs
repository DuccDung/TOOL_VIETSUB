using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubVid.Server.Data;
using SubVid.Server.Models;
using SubVid.Server.Registration;

namespace SubVid.App.Tests;

[Collection("SQL Server integration")]
public sealed class PasswordResetServiceTests
{
    private const string ConnectionString =
        "Data Source=DUNGDEV;Initial Catalog=TOOL_VIETSUB;Integrated Security=True;Trust Server Certificate=True";

    [Fact]
    public async Task ValidOtp_ChangesPasswordAndRevokesExistingSessions()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var email = $"password-reset-{userId:N}@local.invalid";
        var originalPassword = "Original-password-123";
        var newPassword = "New-password-456";
        var passwordHasher = new PasswordHasher<User>();
        var sender = new CapturingEmailSender();

        try
        {
            await using (var setup = CreateDatabase())
            {
                var nowUtc = DateTime.UtcNow;
                var user = new User
                {
                    UserId = userId,
                    Email = email,
                    DisplayName = "Password reset test",
                    RoleCode = "USER",
                    StatusCode = "ACTIVE",
                    EmailConfirmed = true,
                    PasswordChangedAtUtc = nowUtc,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                };
                user.PasswordHash = passwordHasher.HashPassword(user, originalPassword);
                setup.Users.Add(user);
                setup.AuthSessions.Add(new AuthSession
                {
                    SessionId = sessionId,
                    UserId = userId,
                    RefreshTokenHash = RandomNumberGenerator.GetBytes(32),
                    DeviceId = "PASSWORD-RESET-TEST",
                    CreatedAtUtc = nowUtc,
                    LastSeenAtUtc = nowUtc,
                    ExpiresAtUtc = nowUtc.AddDays(1),
                });
                await setup.SaveChangesAsync();
            }

            await using var database = CreateDatabase();
            var service = CreateService(database, passwordHasher, sender);
            var started = await service.StartAsync(
                email,
                "WEB-PASSWORD-RESET-TEST",
                "127.0.0.1",
                CancellationToken.None);

            Assert.True(started.Succeeded, started.ErrorMessage);
            Assert.Matches("^[0-9]{6}$", sender.LastPasswordResetOtp);

            var reset = await service.ResetAsync(
                started.Value!.ChallengeId,
                "WEB-PASSWORD-RESET-TEST",
                sender.LastPasswordResetOtp,
                newPassword,
                "127.0.0.1",
                CancellationToken.None);

            Assert.True(reset.Succeeded, reset.ErrorMessage);
            var updatedUser = await database.Users.SingleAsync(item => item.UserId == userId);
            Assert.Equal(
                PasswordVerificationResult.Success,
                passwordHasher.VerifyHashedPassword(updatedUser, updatedUser.PasswordHash, newPassword));
            Assert.NotNull(updatedUser.PasswordChangedAtUtc);
            Assert.True(await database.AuthSessions.AnyAsync(item =>
                item.SessionId == sessionId
                && item.RevokedAtUtc != null
                && item.RevokeReason == "PASSWORD_RESET"));
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            await cleanup.PasswordResetChallenges
                .Where(item => item.UserId == userId || item.Email == email)
                .ExecuteDeleteAsync();
            await cleanup.AuthSessions.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.SecurityAuditLogs.Where(item => item.UserId == userId).ExecuteDeleteAsync();
            await cleanup.Users.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        }
    }

    private static PasswordResetService CreateService(
        SubVidDbContext database,
        IPasswordHasher<User> passwordHasher,
        IRegistrationEmailSender sender)
    {
        var registrationOptions = new RegistrationOptions
        {
            OtpSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            OtpLifetimeMinutes = 5,
            ResendCooldownSeconds = 60,
            MaxAttempts = 5,
            MaxResends = 3,
        };
        var options = Options.Create(registrationOptions);
        return new PasswordResetService(
            database,
            passwordHasher,
            new OtpService(options),
            sender,
            options,
            NullLogger<PasswordResetService>.Instance);
    }

    private static SubVidDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<SubVidDbContext>()
            .UseSqlServer(ConnectionString)
            .EnableDetailedErrors()
            .Options;
        return new SubVidDbContext(options);
    }

    private sealed class CapturingEmailSender : IRegistrationEmailSender
    {
        public string LastPasswordResetOtp { get; private set; } = string.Empty;

        public Task SendOtpAsync(
            string recipientEmail,
            string displayName,
            string otp,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendPasswordResetOtpAsync(
            string recipientEmail,
            string displayName,
            string otp,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken)
        {
            LastPasswordResetOtp = otp;
            return Task.CompletedTask;
        }
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Auth;

public sealed class WebAccountAuthService(
    SubVidDbContext database,
    IPasswordHasher<User> passwordHasher)
{
    private readonly string _dummyPasswordHash =
        passwordHasher.HashPassword(new User(), "SUBVID_INVALID_WEB_ACCOUNT");

    public async Task<WebAccountLoginResult> LoginAsync(
        string email,
        string password,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var user = await database.Users.SingleOrDefaultAsync(
            item => item.EmailNormalized == normalizedEmail && item.DeletedAtUtc == null,
            cancellationToken);
        var verification = passwordHasher.VerifyHashedPassword(
            user ?? new User(),
            user?.PasswordHash ?? _dummyPasswordHash,
            password);

        if (user is null || verification == PasswordVerificationResult.Failed)
        {
            AddAudit(null, "WEB_LOGIN", "FAILED", ipAddress, normalizedEmail);
            await database.SaveChangesAsync(cancellationToken);
            return WebAccountLoginResult.Failure("Email hoặc mật khẩu không chính xác.");
        }

        if (!string.Equals(user.StatusCode, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            AddAudit(user.UserId, "WEB_LOGIN", "DENIED", ipAddress, normalizedEmail);
            await database.SaveChangesAsync(cancellationToken);
            return WebAccountLoginResult.Failure("Tài khoản hiện không được phép đăng nhập.");
        }

        var nowUtc = DateTime.UtcNow;
        user.LastLoginAtUtc = nowUtc;
        user.UpdatedAtUtc = nowUtc;
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            user.PasswordChangedAtUtc = nowUtc;
        }

        AddAudit(user.UserId, "WEB_LOGIN", "SUCCESS", ipAddress, normalizedEmail);
        await database.SaveChangesAsync(cancellationToken);
        return WebAccountLoginResult.Success(
            user.UserId,
            user.Email,
            user.DisplayName,
            user.RoleCode,
            GetPasswordVersion(user));
    }

    public static long GetPasswordVersion(User user) =>
        (user.PasswordChangedAtUtc ?? user.CreatedAtUtc).Ticks;

    private void AddAudit(
        Guid? userId,
        string eventCode,
        string outcomeCode,
        string? ipAddress,
        string normalizedEmail)
    {
        database.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            UserId = userId,
            EventCode = eventCode,
            OutcomeCode = outcomeCode,
            IpAddress = ipAddress,
            DeviceId = "WEB_ACCOUNT",
            DetailsJson = JsonSerializer.Serialize(new { normalizedEmail }),
            CreatedAtUtc = DateTime.UtcNow,
        });
    }
}

public sealed record WebAccountLoginResult(
    bool Succeeded,
    Guid? UserId = null,
    string? Email = null,
    string? DisplayName = null,
    string? Role = null,
    long? PasswordVersion = null,
    string? ErrorMessage = null)
{
    public static WebAccountLoginResult Success(
        Guid userId,
        string email,
        string displayName,
        string role,
        long passwordVersion) =>
        new(true, userId, email, displayName, role, passwordVersion);

    public static WebAccountLoginResult Failure(string message) =>
        new(false, ErrorMessage: message);
}

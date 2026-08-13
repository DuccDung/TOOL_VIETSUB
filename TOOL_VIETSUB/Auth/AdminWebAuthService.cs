using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TOOL_VIETSUB.Data;
using TOOL_VIETSUB.Models;

namespace TOOL_VIETSUB.Auth;

public sealed class AdminWebAuthService(
    ToolVietSubDbContext database,
    IPasswordHasher<User> passwordHasher)
{
    private readonly string _dummyPasswordHash =
        passwordHasher.HashPassword(new User(), "TOOL_VIETSUB_INVALID_WEB_ADMIN");

    public async Task<AdminWebLoginResult> LoginAsync(
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
            AddAudit(null, "ADMIN_WEB_LOGIN", "FAILED", ipAddress, normalizedEmail);
            await database.SaveChangesAsync(cancellationToken);
            return AdminWebLoginResult.Failure("Email hoặc mật khẩu không chính xác.");
        }

        if (!string.Equals(user.StatusCode, "ACTIVE", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(user.RoleCode, "ADMIN", StringComparison.OrdinalIgnoreCase))
        {
            AddAudit(user.UserId, "ADMIN_WEB_LOGIN", "DENIED", ipAddress, normalizedEmail);
            await database.SaveChangesAsync(cancellationToken);
            return AdminWebLoginResult.Failure("Tài khoản không có quyền quản trị hệ thống.");
        }

        var nowUtc = DateTime.UtcNow;
        user.LastLoginAtUtc = nowUtc;
        user.UpdatedAtUtc = nowUtc;
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
        }

        AddAudit(user.UserId, "ADMIN_WEB_LOGIN", "SUCCESS", ipAddress, normalizedEmail);
        await database.SaveChangesAsync(cancellationToken);
        return AdminWebLoginResult.Success(user.UserId, user.Email, user.DisplayName);
    }

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
            DeviceId = "WEB_ADMIN",
            DetailsJson = JsonSerializer.Serialize(new { normalizedEmail }),
            CreatedAtUtc = DateTime.UtcNow,
        });
    }
}

public sealed record AdminWebLoginResult(
    bool Succeeded,
    Guid? UserId = null,
    string? Email = null,
    string? DisplayName = null,
    string? ErrorMessage = null)
{
    public static AdminWebLoginResult Success(Guid userId, string email, string displayName) =>
        new(true, userId, email, displayName);

    public static AdminWebLoginResult Failure(string message) =>
        new(false, ErrorMessage: message);
}

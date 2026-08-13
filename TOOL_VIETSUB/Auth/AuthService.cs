using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TOOL_VIETSUB.Contracts;
using TOOL_VIETSUB.Data;
using TOOL_VIETSUB.Models;

namespace TOOL_VIETSUB.Auth;

public sealed class AuthService(
    ToolVietSubDbContext database,
    IPasswordHasher<User> passwordHasher,
    TokenService tokenService,
    ILogger<AuthService> logger)
{
    private readonly string _dummyPasswordHash =
        passwordHasher.HashPassword(new User(), "TOOL_VIETSUB_INVALID_ACCOUNT");

    public async Task<AuthServiceResult> LoginAsync(
        LoginRequest request,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = request.Email.Trim().ToUpperInvariant();
        var deviceId = request.DeviceId.Trim();
        var user = await database.Users.SingleOrDefaultAsync(
            item => item.EmailNormalized == normalizedEmail && item.DeletedAtUtc == null,
            cancellationToken);

        var verification = passwordHasher.VerifyHashedPassword(
            user ?? new User(),
            user?.PasswordHash ?? _dummyPasswordHash,
            request.Password);

        if (user is null || verification == PasswordVerificationResult.Failed)
        {
            await WriteAuditAsync(null, "LOGIN", "FAILED", ipAddress, deviceId, cancellationToken);
            return AuthServiceResult.Failure(
                "AUTH_INVALID_CREDENTIALS",
                "Email hoặc mật khẩu không chính xác.");
        }

        if (!string.Equals(user.StatusCode, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAuditAsync(user.UserId, "LOGIN", "DENIED", ipAddress, deviceId, cancellationToken);
            return AuthServiceResult.Failure(
                "AUTH_ACCOUNT_UNAVAILABLE",
                "Tài khoản hiện không được phép đăng nhập.",
                forbidden: true);
        }

        var nowUtc = DateTime.UtcNow;
        var sessionId = Guid.NewGuid();
        var refresh = tokenService.CreateRefreshToken(nowUtc);
        var access = tokenService.CreateAccessToken(user, sessionId, nowUtc);

        database.AuthSessions.Add(new AuthSession
        {
            SessionId = sessionId,
            UserId = user.UserId,
            RefreshTokenHash = refresh.Hash,
            DeviceId = deviceId,
            DeviceName = NormalizeOptional(request.DeviceName),
            AppVersion = NormalizeOptional(request.AppVersion),
            IpAddress = ipAddress,
            CreatedAtUtc = nowUtc,
            LastSeenAtUtc = nowUtc,
            ExpiresAtUtc = refresh.ExpiresAtUtc,
        });

        user.LastLoginAtUtc = nowUtc;
        user.UpdatedAtUtc = nowUtc;
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        }

        AddAudit(user.UserId, "LOGIN", "SUCCESS", ipAddress, deviceId, nowUtc);
        await database.SaveChangesAsync(cancellationToken);

        return AuthServiceResult.Success(new TokenPairResponse(
            access.Token,
            access.ExpiresAtUtc,
            refresh.Token,
            refresh.ExpiresAtUtc,
            AccountMapper.ToResponse(user)));
    }

    public async Task<AuthServiceResult> RefreshAsync(
        RefreshTokenRequest request,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var deviceId = request.DeviceId.Trim();
        var tokenHash = TokenService.HashRefreshToken(request.RefreshToken);

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var currentSession = await database.AuthSessions
            .Include(item => item.User)
            .SingleOrDefaultAsync(item => item.RefreshTokenHash == tokenHash, cancellationToken);

        if (currentSession is null)
        {
            await WriteAuditAsync(null, "REFRESH", "FAILED", ipAddress, deviceId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthServiceResult.Failure("AUTH_REFRESH_INVALID", "Phiên đăng nhập không hợp lệ.");
        }

        if (currentSession.RevokedAtUtc is not null)
        {
            await database.AuthSessions
                .Where(item => item.UserId == currentSession.UserId
                    && item.RevokedAtUtc == null
                    && item.ExpiresAtUtc > nowUtc)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(item => item.RevokedAtUtc, nowUtc)
                        .SetProperty(item => item.RevokeReason, "REFRESH_TOKEN_REUSE"),
                    cancellationToken);
            AddAudit(
                currentSession.UserId,
                "REFRESH_REUSE",
                "DENIED",
                ipAddress,
                deviceId,
                nowUtc);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogWarning(
                "Refresh token reuse detected for user {UserId} and device {DeviceId}.",
                currentSession.UserId,
                deviceId);
            return AuthServiceResult.Failure(
                "AUTH_REFRESH_REUSED",
                "Phiên đăng nhập đã bị thu hồi vì phát hiện token được sử dụng lại.");
        }

        if (currentSession.ExpiresAtUtc <= nowUtc)
        {
            currentSession.RevokedAtUtc = nowUtc;
            currentSession.RevokeReason = "EXPIRED";
            AddAudit(currentSession.UserId, "REFRESH", "DENIED", ipAddress, deviceId, nowUtc);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthServiceResult.Failure("AUTH_REFRESH_EXPIRED", "Phiên đăng nhập đã hết hạn.");
        }

        if (!string.Equals(currentSession.DeviceId, deviceId, StringComparison.Ordinal))
        {
            AddAudit(currentSession.UserId, "REFRESH", "DENIED", ipAddress, deviceId, nowUtc);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthServiceResult.Failure(
                "AUTH_DEVICE_MISMATCH",
                "Phiên đăng nhập không thuộc thiết bị này.");
        }

        if (!string.Equals(currentSession.User.StatusCode, "ACTIVE", StringComparison.OrdinalIgnoreCase)
            || currentSession.User.DeletedAtUtc is not null)
        {
            currentSession.RevokedAtUtc = nowUtc;
            currentSession.RevokeReason = "ACCOUNT_UNAVAILABLE";
            AddAudit(currentSession.UserId, "REFRESH", "DENIED", ipAddress, deviceId, nowUtc);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthServiceResult.Failure(
                "AUTH_ACCOUNT_UNAVAILABLE",
                "Tài khoản hiện không được phép sử dụng.",
                forbidden: true);
        }

        var nextSessionId = Guid.NewGuid();
        var nextRefresh = tokenService.CreateRefreshToken(nowUtc);
        var nextAccess = tokenService.CreateAccessToken(currentSession.User, nextSessionId, nowUtc);
        database.AuthSessions.Add(new AuthSession
        {
            SessionId = nextSessionId,
            UserId = currentSession.UserId,
            RefreshTokenHash = nextRefresh.Hash,
            DeviceId = deviceId,
            DeviceName = NormalizeOptional(request.DeviceName) ?? currentSession.DeviceName,
            AppVersion = NormalizeOptional(request.AppVersion) ?? currentSession.AppVersion,
            IpAddress = ipAddress,
            CreatedAtUtc = nowUtc,
            LastSeenAtUtc = nowUtc,
            ExpiresAtUtc = nextRefresh.ExpiresAtUtc,
        });
        currentSession.RevokedAtUtc = nowUtc;
        currentSession.RevokeReason = "ROTATED";
        currentSession.ReplacedBySessionId = nextSessionId;
        currentSession.LastSeenAtUtc = nowUtc;
        AddAudit(currentSession.UserId, "REFRESH", "SUCCESS", ipAddress, deviceId, nowUtc);

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return AuthServiceResult.Success(new TokenPairResponse(
            nextAccess.Token,
            nextAccess.ExpiresAtUtc,
            nextRefresh.Token,
            nextRefresh.ExpiresAtUtc,
            AccountMapper.ToResponse(currentSession.User)));
    }

    public async Task<bool> LogoutAsync(
        Guid userId,
        Guid sessionId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var session = await database.AuthSessions.SingleOrDefaultAsync(
            item => item.SessionId == sessionId && item.UserId == userId,
            cancellationToken);
        var revoked = session is not null && session.RevokedAtUtc is null;
        if (revoked)
        {
            session!.RevokedAtUtc = nowUtc;
            session.RevokeReason = "LOGOUT";
            session.LastSeenAtUtc = nowUtc;
        }

        AddAudit(userId, "LOGOUT", "SUCCESS", ipAddress, session?.DeviceId, nowUtc);
        await database.SaveChangesAsync(cancellationToken);
        return revoked;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task WriteAuditAsync(
        Guid? userId,
        string eventCode,
        string outcomeCode,
        string? ipAddress,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        AddAudit(userId, eventCode, outcomeCode, ipAddress, deviceId, DateTime.UtcNow);
        await database.SaveChangesAsync(cancellationToken);
    }

    private void AddAudit(
        Guid? userId,
        string eventCode,
        string outcomeCode,
        string? ipAddress,
        string? deviceId,
        DateTime createdAtUtc)
    {
        database.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            UserId = userId,
            EventCode = eventCode,
            OutcomeCode = outcomeCode,
            IpAddress = ipAddress,
            DeviceId = deviceId,
            CreatedAtUtc = createdAtUtc,
        });
    }
}

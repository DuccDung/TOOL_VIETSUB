using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Registration;

public sealed class PasswordResetService(
    SubVidDbContext database,
    IPasswordHasher<User> passwordHasher,
    OtpService otpService,
    IRegistrationEmailSender emailSender,
    IOptions<RegistrationOptions> options,
    ILogger<PasswordResetService> logger)
{
    private readonly RegistrationOptions _options = options.Value;

    public async Task<RegistrationServiceResult<PasswordResetChallengeResponse>> StartAsync(
        string email,
        string deviceId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        email = email.Trim();
        deviceId = deviceId.Trim();
        if (email.Length == 0 || deviceId.Length < 8)
        {
            return Failure<PasswordResetChallengeResponse>(
                "PASSWORD_RESET_REQUEST_INVALID",
                "Yêu cầu đặt lại mật khẩu không hợp lệ.",
                StatusCodes.Status400BadRequest);
        }

        var normalizedEmail = email.ToUpperInvariant();
        var nowUtc = DateTime.UtcNow;
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var current = await database.PasswordResetChallenges.SingleOrDefaultAsync(
            item => item.EmailNormalized == normalizedEmail && item.StatusCode == "PENDING",
            cancellationToken);
        if (current is not null && current.ResendAtUtc > nowUtc)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((current.ResendAtUtc - nowUtc).TotalSeconds));
            await transaction.CommitAsync(cancellationToken);
            return RegistrationServiceResult<PasswordResetChallengeResponse>.Failure(
                "PASSWORD_RESET_COOLDOWN",
                "Vui lòng chờ trước khi yêu cầu mã OTP mới.",
                StatusCodes.Status429TooManyRequests,
                retryAfter);
        }

        if (current is not null)
        {
            current.StatusCode = current.ExpiresAtUtc <= nowUtc ? "EXPIRED" : "CANCELLED";
            current.UpdatedAtUtc = nowUtc;
        }

        var user = await database.Users.SingleOrDefaultAsync(
            item => item.EmailNormalized == normalizedEmail && item.DeletedAtUtc == null,
            cancellationToken);
        var challengeId = Guid.NewGuid();
        var otp = otpService.GenerateCode();
        var challenge = new PasswordResetChallenge
        {
            ChallengeId = challengeId,
            UserId = user?.UserId,
            Email = email,
            OtpHash = otpService.HashCode(challengeId, normalizedEmail, otp),
            StatusCode = "PENDING",
            DeviceId = deviceId,
            IpAddress = ipAddress,
            ExpiresAtUtc = nowUtc.AddMinutes(_options.OtpLifetimeMinutes),
            ResendAtUtc = nowUtc.AddSeconds(_options.ResendCooldownSeconds),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        database.PasswordResetChallenges.Add(challenge);
        AddAudit(user?.UserId, "PASSWORD_RESET_START", "SUCCESS", ipAddress, deviceId, nowUtc);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (user is not null && string.Equals(user.StatusCode, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await emailSender.SendPasswordResetOtpAsync(
                    user.Email,
                    user.DisplayName,
                    otp,
                    challenge.ExpiresAtUtc,
                    cancellationToken);
                AddAudit(user.UserId, "PASSWORD_RESET_OTP_SEND", "SUCCESS", ipAddress, deviceId, DateTime.UtcNow);
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                challenge.StatusCode = "CANCELLED";
                challenge.UpdatedAtUtc = DateTime.UtcNow;
                AddAudit(user.UserId, "PASSWORD_RESET_OTP_SEND", "FAILED", ipAddress, deviceId, challenge.UpdatedAtUtc);
                await database.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning(exception, "Password-reset OTP delivery failed for challenge {ChallengeId}.", challengeId);
            }
        }

        return RegistrationServiceResult<PasswordResetChallengeResponse>.Success(ToResponse(challenge));
    }

    public async Task<RegistrationServiceResult<PasswordResetChallengeResponse>> ResendAsync(
        Guid challengeId,
        string deviceId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var challenge = await database.PasswordResetChallenges
            .Include(item => item.User)
            .SingleOrDefaultAsync(item => item.ChallengeId == challengeId, cancellationToken);
        var invalid = ValidatePending<PasswordResetChallengeResponse>(challenge, deviceId, nowUtc);
        if (invalid is not null)
        {
            if (challenge?.StatusCode == "EXPIRED")
            {
                await database.SaveChangesAsync(cancellationToken);
            }

            return invalid;
        }

        if (challenge!.ResendAtUtc > nowUtc)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((challenge.ResendAtUtc - nowUtc).TotalSeconds));
            return RegistrationServiceResult<PasswordResetChallengeResponse>.Failure(
                "PASSWORD_RESET_COOLDOWN",
                "Bạn chưa thể gửi lại OTP lúc này.",
                StatusCodes.Status429TooManyRequests,
                retryAfter);
        }

        if (challenge.ResendCount >= _options.MaxResends)
        {
            challenge.StatusCode = "LOCKED";
            challenge.UpdatedAtUtc = nowUtc;
            AddAudit(challenge.UserId, "PASSWORD_RESET_RESEND", "DENIED", ipAddress, deviceId, nowUtc);
            await database.SaveChangesAsync(cancellationToken);
            return Failure<PasswordResetChallengeResponse>(
                "PASSWORD_RESET_RESEND_LIMIT",
                "Bạn đã vượt quá số lần gửi lại OTP.",
                StatusCodes.Status429TooManyRequests);
        }

        var previousHash = challenge.OtpHash;
        var previousExpiresAt = challenge.ExpiresAtUtc;
        var otp = otpService.GenerateCode();
        challenge.OtpHash = otpService.HashCode(
            challenge.ChallengeId,
            challenge.EmailNormalized ?? challenge.Email.Trim().ToUpperInvariant(),
            otp);
        challenge.AttemptCount = 0;
        challenge.ResendCount += 1;
        challenge.ExpiresAtUtc = nowUtc.AddMinutes(_options.OtpLifetimeMinutes);
        challenge.ResendAtUtc = nowUtc.AddSeconds(_options.ResendCooldownSeconds);
        challenge.UpdatedAtUtc = nowUtc;
        await database.SaveChangesAsync(cancellationToken);

        if (challenge.User is not null
            && string.Equals(challenge.User.StatusCode, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await emailSender.SendPasswordResetOtpAsync(
                    challenge.User.Email,
                    challenge.User.DisplayName,
                    otp,
                    challenge.ExpiresAtUtc,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                challenge.OtpHash = previousHash;
                challenge.ExpiresAtUtc = previousExpiresAt;
                challenge.ResendAtUtc = DateTime.UtcNow;
                challenge.ResendCount -= 1;
                challenge.UpdatedAtUtc = DateTime.UtcNow;
                AddAudit(challenge.UserId, "PASSWORD_RESET_RESEND", "FAILED", ipAddress, deviceId, challenge.UpdatedAtUtc);
                await database.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning(exception, "Password-reset OTP resend failed for challenge {ChallengeId}.", challengeId);
                return Failure<PasswordResetChallengeResponse>(
                    "PASSWORD_RESET_EMAIL_FAILED",
                    "Chưa thể gửi lại email xác nhận.",
                    StatusCodes.Status503ServiceUnavailable);
            }
        }

        AddAudit(challenge.UserId, "PASSWORD_RESET_RESEND", "SUCCESS", ipAddress, deviceId, DateTime.UtcNow);
        await database.SaveChangesAsync(cancellationToken);
        return RegistrationServiceResult<PasswordResetChallengeResponse>.Success(ToResponse(challenge));
    }

    public async Task<RegistrationServiceResult<PasswordResetCompletedResponse>> ResetAsync(
        Guid challengeId,
        string deviceId,
        string otp,
        string newPassword,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (newPassword.Length is < 8 or > 256)
        {
            return Failure<PasswordResetCompletedResponse>(
                "PASSWORD_RESET_PASSWORD_INVALID",
                "Mật khẩu mới phải có từ 8 đến 256 ký tự.",
                StatusCodes.Status400BadRequest);
        }

        var nowUtc = DateTime.UtcNow;
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var challenge = await database.PasswordResetChallenges
            .Include(item => item.User)
            .SingleOrDefaultAsync(item => item.ChallengeId == challengeId, cancellationToken);
        var invalid = ValidatePending<PasswordResetCompletedResponse>(challenge, deviceId, nowUtc);
        if (invalid is not null)
        {
            if (challenge?.StatusCode == "EXPIRED")
            {
                await database.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return invalid;
        }

        var normalizedEmail = challenge!.EmailNormalized ?? challenge.Email.Trim().ToUpperInvariant();
        if (!otpService.VerifyCode(challenge.ChallengeId, normalizedEmail, otp, challenge.OtpHash))
        {
            challenge.AttemptCount += 1;
            challenge.StatusCode = challenge.AttemptCount >= _options.MaxAttempts ? "LOCKED" : "PENDING";
            challenge.UpdatedAtUtc = nowUtc;
            AddAudit(challenge.UserId, "PASSWORD_RESET_VERIFY", "FAILED", ipAddress, deviceId, nowUtc);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var attemptsRemaining = Math.Max(0, _options.MaxAttempts - challenge.AttemptCount);
            return Failure<PasswordResetCompletedResponse>(
                challenge.StatusCode == "LOCKED" ? "PASSWORD_RESET_OTP_LOCKED" : "PASSWORD_RESET_OTP_INVALID",
                challenge.StatusCode == "LOCKED"
                    ? "Mã OTP đã bị khóa do nhập sai quá nhiều lần."
                    : $"Mã OTP không chính xác. Bạn còn {attemptsRemaining} lần thử.",
                StatusCodes.Status400BadRequest);
        }

        var user = challenge.User;
        if (user is null
            || user.DeletedAtUtc is not null
            || !string.Equals(user.StatusCode, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            challenge.StatusCode = "CANCELLED";
            challenge.UpdatedAtUtc = nowUtc;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Failure<PasswordResetCompletedResponse>(
                "PASSWORD_RESET_ACCOUNT_UNAVAILABLE",
                "Không thể đặt lại mật khẩu cho tài khoản này.",
                StatusCodes.Status400BadRequest);
        }

        user.PasswordHash = passwordHasher.HashPassword(user, newPassword);
        user.PasswordChangedAtUtc = nowUtc;
        user.UpdatedAtUtc = nowUtc;
        await database.AuthSessions
            .Where(item => item.UserId == user.UserId && item.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(item => item.RevokedAtUtc, nowUtc)
                    .SetProperty(item => item.RevokeReason, "PASSWORD_RESET"),
                cancellationToken);
        challenge.StatusCode = "VERIFIED";
        challenge.VerifiedAtUtc = nowUtc;
        challenge.UpdatedAtUtc = nowUtc;
        AddAudit(user.UserId, "PASSWORD_RESET_VERIFY", "SUCCESS", ipAddress, deviceId, nowUtc);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RegistrationServiceResult<PasswordResetCompletedResponse>.Success(
            new PasswordResetCompletedResponse(user.UserId));
    }

    private RegistrationServiceResult<T>? ValidatePending<T>(
        PasswordResetChallenge? challenge,
        string deviceId,
        DateTime nowUtc)
    {
        if (challenge is null || !string.Equals(challenge.DeviceId, deviceId.Trim(), StringComparison.Ordinal))
        {
            return Failure<T>(
                "PASSWORD_RESET_CHALLENGE_INVALID",
                "Yêu cầu đặt lại mật khẩu không hợp lệ.",
                StatusCodes.Status404NotFound);
        }

        if (challenge.StatusCode != "PENDING")
        {
            return Failure<T>(
                "PASSWORD_RESET_CHALLENGE_CLOSED",
                "Yêu cầu đặt lại mật khẩu đã kết thúc hoặc bị khóa.",
                StatusCodes.Status409Conflict);
        }

        if (challenge.ExpiresAtUtc <= nowUtc)
        {
            challenge.StatusCode = "EXPIRED";
            challenge.UpdatedAtUtc = nowUtc;
            return Failure<T>(
                "PASSWORD_RESET_OTP_EXPIRED",
                "Mã OTP đã hết hạn. Vui lòng yêu cầu mã mới.",
                StatusCodes.Status410Gone);
        }

        return null;
    }

    private PasswordResetChallengeResponse ToResponse(PasswordResetChallenge challenge) => new(
        challenge.ChallengeId,
        MaskEmail(challenge.Email),
        challenge.ExpiresAtUtc,
        challenge.ResendAtUtc,
        Math.Max(0, _options.MaxResends - challenge.ResendCount));

    private static RegistrationServiceResult<T> Failure<T>(string code, string message, int statusCode) =>
        RegistrationServiceResult<T>.Failure(code, message, statusCode);

    private static string MaskEmail(string email)
    {
        var separator = email.IndexOf('@');
        if (separator <= 1)
        {
            return separator < 0 ? "***" : $"***{email[separator..]}";
        }

        return $"{email[0]}***{email[(separator - 1)..]}";
    }

    private void AddAudit(
        Guid? userId,
        string eventCode,
        string outcomeCode,
        string? ipAddress,
        string deviceId,
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

public sealed record PasswordResetChallengeResponse(
    Guid ChallengeId,
    string MaskedEmail,
    DateTime ExpiresAtUtc,
    DateTime ResendAtUtc,
    int ResendsRemaining);

public sealed record PasswordResetCompletedResponse(Guid UserId);

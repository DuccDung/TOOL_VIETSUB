using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TOOL_VIETSUB.Auth;
using TOOL_VIETSUB.Contracts;
using TOOL_VIETSUB.Data;
using TOOL_VIETSUB.Models;

namespace TOOL_VIETSUB.Registration;

public sealed class RegistrationService(
    ToolVietSubDbContext database,
    IPasswordHasher<User> passwordHasher,
    OtpService otpService,
    TokenService tokenService,
    IRegistrationEmailSender emailSender,
    IOptions<RegistrationOptions> options,
    ILogger<RegistrationService> logger)
{
    private readonly RegistrationOptions _options = options.Value;

    public async Task<RegistrationServiceResult<RegistrationChallengeResponse>> StartAsync(
        RegistrationStartRequest request,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        var normalizedEmail = email.ToUpperInvariant();
        var displayName = request.DisplayName.Trim();
        var deviceId = request.DeviceId.Trim();
        var nowUtc = DateTime.UtcNow;

        if (displayName.Length < 2 || deviceId.Length < 8)
        {
            return RegistrationServiceResult<RegistrationChallengeResponse>.Failure(
                "REGISTRATION_REQUEST_INVALID",
                "Thông tin đăng ký không hợp lệ.",
                StatusCodes.Status400BadRequest);
        }

        if (await database.Users.AsNoTracking().AnyAsync(
            item => item.EmailNormalized == normalizedEmail && item.DeletedAtUtc == null,
            cancellationToken))
        {
            AddAudit(null, "REGISTER_START", "DENIED", ipAddress, deviceId, nowUtc);
            await database.SaveChangesAsync(cancellationToken);
            return RegistrationServiceResult<RegistrationChallengeResponse>.Failure(
                "REGISTRATION_EMAIL_UNAVAILABLE",
                "Không thể sử dụng email này để đăng ký.",
                StatusCodes.Status409Conflict);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var current = await database.RegistrationChallenges.SingleOrDefaultAsync(
            item => item.EmailNormalized == normalizedEmail && item.StatusCode == "PENDING",
            cancellationToken);
        if (current is not null && current.ResendAtUtc > nowUtc)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((current.ResendAtUtc - nowUtc).TotalSeconds));
            await transaction.CommitAsync(cancellationToken);
            return RegistrationServiceResult<RegistrationChallengeResponse>.Failure(
                "REGISTRATION_COOLDOWN",
                "Vui lòng chờ trước khi yêu cầu mã OTP mới.",
                StatusCodes.Status429TooManyRequests,
                retryAfter);
        }

        if (current is not null)
        {
            current.StatusCode = current.ExpiresAtUtc <= nowUtc ? "EXPIRED" : "CANCELLED";
            current.UpdatedAtUtc = nowUtc;
            await database.SaveChangesAsync(cancellationToken);
        }

        var challengeId = Guid.NewGuid();
        var otp = otpService.GenerateCode();
        var expiresAtUtc = nowUtc.AddMinutes(_options.OtpLifetimeMinutes);
        var resendAtUtc = nowUtc.AddSeconds(_options.ResendCooldownSeconds);
        var passwordOwner = new User { Email = email, DisplayName = displayName };
        var challenge = new RegistrationChallenge
        {
            ChallengeId = challengeId,
            Email = email,
            DisplayName = displayName,
            PasswordHash = passwordHasher.HashPassword(passwordOwner, request.Password),
            OtpHash = otpService.HashCode(challengeId, normalizedEmail, otp),
            StatusCode = "PENDING",
            DeviceId = deviceId,
            DeviceName = NormalizeOptional(request.DeviceName),
            AppVersion = NormalizeOptional(request.AppVersion),
            IpAddress = ipAddress,
            ExpiresAtUtc = expiresAtUtc,
            ResendAtUtc = resendAtUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        database.RegistrationChallenges.Add(challenge);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        try
        {
            await emailSender.SendOtpAsync(
                challenge.Email,
                challenge.DisplayName,
                otp,
                challenge.ExpiresAtUtc,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            challenge.StatusCode = "CANCELLED";
            challenge.UpdatedAtUtc = DateTime.UtcNow;
            AddAudit(null, "REGISTER_OTP_SEND", "FAILED", ipAddress, deviceId, challenge.UpdatedAtUtc);
            await database.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(exception, "Registration OTP email delivery failed for challenge {ChallengeId}.", challengeId);
            return RegistrationServiceResult<RegistrationChallengeResponse>.Failure(
                "REGISTRATION_EMAIL_FAILED",
                "Chưa thể gửi email xác nhận. Vui lòng thử lại sau.",
                StatusCodes.Status503ServiceUnavailable);
        }

        AddAudit(null, "REGISTER_OTP_SEND", "SUCCESS", ipAddress, deviceId, DateTime.UtcNow);
        await database.SaveChangesAsync(cancellationToken);
        return RegistrationServiceResult<RegistrationChallengeResponse>.Success(
            ToResponse(challenge));
    }

    public async Task<RegistrationServiceResult<RegistrationChallengeResponse>> ResendAsync(
        RegistrationResendRequest request,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (request.ChallengeId == Guid.Empty)
        {
            return RegistrationServiceResult<RegistrationChallengeResponse>.Failure(
                "REGISTRATION_CHALLENGE_INVALID",
                "Yêu cầu đăng ký không hợp lệ.",
                StatusCodes.Status400BadRequest);
        }

        var nowUtc = DateTime.UtcNow;
        var deviceId = request.DeviceId.Trim();
        if (deviceId.Length < 8)
        {
            return RegistrationServiceResult<RegistrationChallengeResponse>.Failure(
                "REGISTRATION_REQUEST_INVALID",
                "Yêu cầu gửi lại OTP không hợp lệ.",
                StatusCodes.Status400BadRequest);
        }

        var challenge = await database.RegistrationChallenges.SingleOrDefaultAsync(
            item => item.ChallengeId == request.ChallengeId,
            cancellationToken);
        var invalidResult = ValidatePendingChallenge<RegistrationChallengeResponse>(
            challenge,
            deviceId,
            nowUtc);
        if (invalidResult is not null)
        {
            if (challenge?.StatusCode == "EXPIRED")
            {
                await database.SaveChangesAsync(cancellationToken);
            }

            return invalidResult;
        }

        if (challenge!.ResendAtUtc > nowUtc)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((challenge.ResendAtUtc - nowUtc).TotalSeconds));
            return RegistrationServiceResult<RegistrationChallengeResponse>.Failure(
                "REGISTRATION_COOLDOWN",
                "Bạn chưa thể gửi lại OTP lúc này.",
                StatusCodes.Status429TooManyRequests,
                retryAfter);
        }

        if (challenge.ResendCount >= _options.MaxResends)
        {
            challenge.StatusCode = "LOCKED";
            challenge.UpdatedAtUtc = nowUtc;
            AddAudit(null, "REGISTER_RESEND", "DENIED", ipAddress, deviceId, nowUtc);
            await database.SaveChangesAsync(cancellationToken);
            return RegistrationServiceResult<RegistrationChallengeResponse>.Failure(
                "REGISTRATION_RESEND_LIMIT",
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

        try
        {
            await emailSender.SendOtpAsync(
                challenge.Email,
                challenge.DisplayName,
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
            AddAudit(null, "REGISTER_RESEND", "FAILED", ipAddress, deviceId, challenge.UpdatedAtUtc);
            await database.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(exception, "Registration OTP resend failed for challenge {ChallengeId}.", challenge.ChallengeId);
            return RegistrationServiceResult<RegistrationChallengeResponse>.Failure(
                "REGISTRATION_EMAIL_FAILED",
                "Chưa thể gửi lại email xác nhận.",
                StatusCodes.Status503ServiceUnavailable);
        }

        AddAudit(null, "REGISTER_RESEND", "SUCCESS", ipAddress, deviceId, DateTime.UtcNow);
        await database.SaveChangesAsync(cancellationToken);
        return RegistrationServiceResult<RegistrationChallengeResponse>.Success(ToResponse(challenge));
    }

    public async Task<RegistrationServiceResult<TokenPairResponse>> VerifyAsync(
        RegistrationVerifyRequest request,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (request.ChallengeId == Guid.Empty)
        {
            return RegistrationServiceResult<TokenPairResponse>.Failure(
                "REGISTRATION_CHALLENGE_INVALID",
                "Yêu cầu đăng ký không hợp lệ.",
                StatusCodes.Status400BadRequest);
        }

        var nowUtc = DateTime.UtcNow;
        var deviceId = request.DeviceId.Trim();
        if (deviceId.Length < 8)
        {
            return RegistrationServiceResult<TokenPairResponse>.Failure(
                "REGISTRATION_REQUEST_INVALID",
                "Yêu cầu xác minh OTP không hợp lệ.",
                StatusCodes.Status400BadRequest);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var challenge = await database.RegistrationChallenges.SingleOrDefaultAsync(
            item => item.ChallengeId == request.ChallengeId,
            cancellationToken);
        var invalidResult = ValidatePendingChallenge<TokenPairResponse>(challenge, deviceId, nowUtc);
        if (invalidResult is not null)
        {
            if (challenge?.StatusCode == "EXPIRED")
            {
                await database.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return invalidResult;
        }

        var normalizedEmail = challenge!.EmailNormalized ?? challenge.Email.Trim().ToUpperInvariant();
        if (!otpService.VerifyCode(
            challenge.ChallengeId,
            normalizedEmail,
            request.Otp,
            challenge.OtpHash))
        {
            challenge.AttemptCount += 1;
            challenge.StatusCode = challenge.AttemptCount >= _options.MaxAttempts ? "LOCKED" : "PENDING";
            challenge.UpdatedAtUtc = nowUtc;
            AddAudit(null, "REGISTER_VERIFY", "FAILED", ipAddress, deviceId, nowUtc);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var attemptsRemaining = Math.Max(0, _options.MaxAttempts - challenge.AttemptCount);
            return RegistrationServiceResult<TokenPairResponse>.Failure(
                challenge.StatusCode == "LOCKED" ? "REGISTRATION_OTP_LOCKED" : "REGISTRATION_OTP_INVALID",
                challenge.StatusCode == "LOCKED"
                    ? "Mã OTP đã bị khóa do nhập sai quá nhiều lần."
                    : $"Mã OTP không chính xác. Bạn còn {attemptsRemaining} lần thử.",
                StatusCodes.Status400BadRequest);
        }

        if (await database.Users.AnyAsync(
            item => item.EmailNormalized == normalizedEmail && item.DeletedAtUtc == null,
            cancellationToken))
        {
            challenge.StatusCode = "CANCELLED";
            challenge.UpdatedAtUtc = nowUtc;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RegistrationServiceResult<TokenPairResponse>.Failure(
                "REGISTRATION_EMAIL_UNAVAILABLE",
                "Không thể hoàn tất đăng ký bằng email này.",
                StatusCodes.Status409Conflict);
        }

        var freePlan = await database.ServicePlans.SingleOrDefaultAsync(
            item => item.PlanCode == "FREE" && item.IsActive,
            cancellationToken);
        if (freePlan is null)
        {
            throw new InvalidOperationException("The FREE service plan has not been deployed.");
        }

        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = challenge.Email,
            PasswordHash = challenge.PasswordHash,
            DisplayName = challenge.DisplayName,
            RoleCode = "USER",
            StatusCode = "ACTIVE",
            EmailConfirmed = true,
            LastLoginAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        var sessionId = Guid.NewGuid();
        var refresh = tokenService.CreateRefreshToken(nowUtc);
        var access = tokenService.CreateAccessToken(user, sessionId, nowUtc);
        database.Users.Add(user);
        database.UserSubscriptions.Add(new UserSubscription
        {
            SubscriptionId = Guid.NewGuid(),
            UserId = user.UserId,
            PlanId = freePlan.PlanId,
            StatusCode = "ACTIVE",
            StartsAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        });
        database.AuthSessions.Add(new AuthSession
        {
            SessionId = sessionId,
            UserId = user.UserId,
            RefreshTokenHash = refresh.Hash,
            DeviceId = deviceId,
            DeviceName = NormalizeOptional(request.DeviceName) ?? challenge.DeviceName,
            AppVersion = NormalizeOptional(request.AppVersion) ?? challenge.AppVersion,
            IpAddress = ipAddress,
            CreatedAtUtc = nowUtc,
            LastSeenAtUtc = nowUtc,
            ExpiresAtUtc = refresh.ExpiresAtUtc,
        });
        challenge.StatusCode = "VERIFIED";
        challenge.VerifiedAtUtc = nowUtc;
        challenge.UpdatedAtUtc = nowUtc;
        AddAudit(user.UserId, "REGISTER_VERIFY", "SUCCESS", ipAddress, deviceId, nowUtc);

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RegistrationServiceResult<TokenPairResponse>.Success(new TokenPairResponse(
            access.Token,
            access.ExpiresAtUtc,
            refresh.Token,
            refresh.ExpiresAtUtc,
            AccountMapper.ToResponse(user)));
    }

    private RegistrationServiceResult<T>? ValidatePendingChallenge<T>(
        RegistrationChallenge? challenge,
        string deviceId,
        DateTime nowUtc)
    {
        if (challenge is null)
        {
            return RegistrationServiceResult<T>.Failure(
                "REGISTRATION_CHALLENGE_INVALID",
                "Yêu cầu đăng ký không tồn tại.",
                StatusCodes.Status404NotFound);
        }

        if (!string.Equals(challenge.DeviceId, deviceId, StringComparison.Ordinal))
        {
            return RegistrationServiceResult<T>.Failure(
                "REGISTRATION_DEVICE_MISMATCH",
                "Yêu cầu đăng ký không thuộc thiết bị này.",
                StatusCodes.Status403Forbidden);
        }

        if (challenge.StatusCode != "PENDING")
        {
            return RegistrationServiceResult<T>.Failure(
                "REGISTRATION_CHALLENGE_CLOSED",
                "Yêu cầu đăng ký đã kết thúc hoặc bị khóa.",
                StatusCodes.Status409Conflict);
        }

        if (challenge.ExpiresAtUtc <= nowUtc)
        {
            challenge.StatusCode = "EXPIRED";
            challenge.UpdatedAtUtc = nowUtc;
            return RegistrationServiceResult<T>.Failure(
                "REGISTRATION_OTP_EXPIRED",
                "Mã OTP đã hết hạn. Vui lòng bắt đầu lại đăng ký.",
                StatusCodes.Status410Gone);
        }

        return null;
    }

    private RegistrationChallengeResponse ToResponse(RegistrationChallenge challenge) => new(
        challenge.ChallengeId,
        MaskEmail(challenge.Email),
        challenge.ExpiresAtUtc,
        challenge.ResendAtUtc,
        Math.Max(0, _options.MaxResends - challenge.ResendCount));

    private static string MaskEmail(string email)
    {
        var separator = email.IndexOf('@');
        if (separator < 0)
        {
            return "***";
        }

        if (separator <= 1)
        {
            return $"***{email[separator..]}";
        }

        return $"{email[0]}***{email[(separator - 1)..]}";
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

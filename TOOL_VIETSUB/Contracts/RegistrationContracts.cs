using System.ComponentModel.DataAnnotations;

namespace TOOL_VIETSUB.Contracts;

public sealed class RegistrationStartRequest
{
    [Required, StringLength(200, MinimumLength = 2)]
    public string DisplayName { get; init; } = string.Empty;

    [Required, EmailAddress, StringLength(320)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(256, MinimumLength = 8)]
    public string Password { get; init; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 8)]
    public string DeviceId { get; init; } = string.Empty;

    [StringLength(200)]
    public string? DeviceName { get; init; }

    [StringLength(50)]
    public string? AppVersion { get; init; }
}

public sealed class RegistrationVerifyRequest
{
    public Guid ChallengeId { get; init; }

    [Required, RegularExpression("^[0-9]{6}$")]
    public string Otp { get; init; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 8)]
    public string DeviceId { get; init; } = string.Empty;

    [StringLength(200)]
    public string? DeviceName { get; init; }

    [StringLength(50)]
    public string? AppVersion { get; init; }
}

public sealed class RegistrationResendRequest
{
    public Guid ChallengeId { get; init; }

    [Required, StringLength(200, MinimumLength = 8)]
    public string DeviceId { get; init; } = string.Empty;
}

public sealed record RegistrationChallengeResponse(
    Guid ChallengeId,
    string MaskedEmail,
    DateTime ExpiresAtUtc,
    DateTime ResendAtUtc,
    int ResendsRemaining);

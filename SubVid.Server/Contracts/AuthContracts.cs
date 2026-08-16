using System.ComponentModel.DataAnnotations;

namespace SubVid.Server.Contracts;

public sealed class LoginRequest
{
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

public sealed class RefreshTokenRequest
{
    [Required, StringLength(500, MinimumLength = 40)]
    public string RefreshToken { get; init; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 8)]
    public string DeviceId { get; init; } = string.Empty;

    [StringLength(200)]
    public string? DeviceName { get; init; }

    [StringLength(50)]
    public string? AppVersion { get; init; }
}

public sealed record TokenPairResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    AccountResponse Account);

public sealed record LogoutResponse(bool Revoked);

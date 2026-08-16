using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("registration_challenges")]
public sealed class RegistrationChallenge
{
    [Key]
    [Column("challenge_id")]
    public Guid ChallengeId { get; set; }

    [Column("email")]
    [StringLength(320)]
    public string Email { get; set; } = null!;

    [Column("email_normalized")]
    [StringLength(320)]
    public string? EmailNormalized { get; set; }

    [Column("display_name")]
    [StringLength(200)]
    public string DisplayName { get; set; } = null!;

    [Column("password_hash")]
    [StringLength(1000)]
    public string PasswordHash { get; set; } = null!;

    [Column("otp_hash", TypeName = "binary(32)")]
    public byte[] OtpHash { get; set; } = null!;

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = null!;

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("resend_count")]
    public int ResendCount { get; set; }

    [Column("device_id")]
    [StringLength(200)]
    public string DeviceId { get; set; } = null!;

    [Column("device_name")]
    [StringLength(200)]
    public string? DeviceName { get; set; }

    [Column("app_version")]
    [StringLength(50)]
    public string? AppVersion { get; set; }

    [Column("ip_address")]
    [StringLength(45)]
    [Unicode(false)]
    public string? IpAddress { get; set; }

    [Column("expires_at_utc")]
    [Precision(3)]
    public DateTime ExpiresAtUtc { get; set; }

    [Column("resend_at_utc")]
    [Precision(3)]
    public DateTime ResendAtUtc { get; set; }

    [Column("verified_at_utc")]
    [Precision(3)]
    public DateTime? VerifiedAtUtc { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;
}

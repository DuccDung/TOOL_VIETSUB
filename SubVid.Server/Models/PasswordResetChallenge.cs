using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("password_reset_challenges")]
public sealed class PasswordResetChallenge
{
    [Key]
    [Column("challenge_id")]
    public Guid ChallengeId { get; set; }

    [Column("user_id")]
    public Guid? UserId { get; set; }

    [Column("email")]
    [StringLength(320)]
    public string Email { get; set; } = string.Empty;

    [Column("email_normalized")]
    [StringLength(320)]
    public string? EmailNormalized { get; set; }

    [Column("otp_hash", TypeName = "binary(32)")]
    public byte[] OtpHash { get; set; } = [];

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = "PENDING";

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("resend_count")]
    public int ResendCount { get; set; }

    [Column("device_id")]
    [StringLength(200)]
    public string DeviceId { get; set; } = string.Empty;

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
    public byte[] RowVersion { get; set; } = [];

    public User? User { get; set; }
}

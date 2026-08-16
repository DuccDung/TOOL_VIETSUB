using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("auth_sessions")]
public sealed class AuthSession
{
    [Key]
    [Column("session_id")]
    public Guid SessionId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("refresh_token_hash", TypeName = "binary(32)")]
    public byte[] RefreshTokenHash { get; set; } = null!;

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

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("last_seen_at_utc")]
    [Precision(3)]
    public DateTime LastSeenAtUtc { get; set; }

    [Column("expires_at_utc")]
    [Precision(3)]
    public DateTime ExpiresAtUtc { get; set; }

    [Column("revoked_at_utc")]
    [Precision(3)]
    public DateTime? RevokedAtUtc { get; set; }

    [Column("revoke_reason")]
    [StringLength(200)]
    public string? RevokeReason { get; set; }

    [Column("replaced_by_session_id")]
    public Guid? ReplacedBySessionId { get; set; }

    public User User { get; set; } = null!;
}

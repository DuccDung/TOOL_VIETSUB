using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("cloud_provider_credentials")]
[Index(nameof(ProviderCode), nameof(StatusCode), nameof(Priority), Name = "IX_cloud_credentials_provider_active")]
public sealed class CloudProviderCredential
{
    [Key]
    [Column("credential_id")]
    public Guid CredentialId { get; set; }

    [Column("provider_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string ProviderCode { get; set; } = null!;

    [Column("display_name")]
    [StringLength(120)]
    public string DisplayName { get; set; } = null!;

    [Column("encrypted_api_key")]
    public string EncryptedApiKey { get; set; } = null!;

    [Column("key_fingerprint")]
    [StringLength(64)]
    [Unicode(false)]
    public string KeyFingerprint { get; set; } = null!;

    [Column("key_suffix")]
    [StringLength(12)]
    [Unicode(false)]
    public string KeySuffix { get; set; } = null!;

    [Column("assigned_user_id")]
    public Guid? AssignedUserId { get; set; }

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = "ACTIVE";

    [Column("priority")]
    public int Priority { get; set; } = 100;

    [Column("last_issued_at_utc")]
    [Precision(3)]
    public DateTime? LastIssuedAtUtc { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    public User? AssignedUser { get; set; }
}

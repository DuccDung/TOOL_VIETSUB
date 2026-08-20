using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("cloud_key_pools")]
[Index(nameof(PoolCode), Name = "UQ_cloud_key_pools_code", IsUnique = true)]
[Index(nameof(ProviderCode), nameof(StatusCode), Name = "IX_cloud_key_pools_provider_status")]
public sealed class CloudKeyPool
{
    [Key]
    [Column("pool_id")]
    public Guid PoolId { get; set; }

    [Column("pool_code")]
    [StringLength(60)]
    [Unicode(false)]
    public string PoolCode { get; set; } = null!;

    [Column("display_name")]
    [StringLength(120)]
    public string DisplayName { get; set; } = null!;

    [Column("provider_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string ProviderCode { get; set; } = null!;

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = "ACTIVE";

    [Column("is_legacy")]
    public bool IsLegacy { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    public ICollection<CloudProviderCredential> Credentials { get; set; } = [];

    public ICollection<CloudKeyPoolPlan> PlanLinks { get; set; } = [];
}


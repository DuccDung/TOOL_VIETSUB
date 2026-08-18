using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("cloud_quota_limits")]
public sealed class CloudQuotaLimit
{
    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("unit_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string UnitCode { get; set; } = null!;

    [Column("monthly_limit", TypeName = "decimal(20,0)")]
    public decimal MonthlyLimit { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("updated_by_user_id")]
    public Guid? UpdatedByUserId { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    public User User { get; set; } = null!;
}

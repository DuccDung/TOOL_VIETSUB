using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("service_plans")]
public sealed class ServicePlan
{
    [Key]
    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("plan_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string PlanCode { get; set; } = null!;

    [Column("display_name")]
    [StringLength(100)]
    public string DisplayName { get; set; } = null!;

    [Column("description")]
    [StringLength(500)]
    public string? Description { get; set; }

    [Column("monthly_quota_minutes", TypeName = "decimal(12, 2)")]
    public decimal? MonthlyQuotaMinutes { get; set; }

    [Column("max_video_minutes", TypeName = "decimal(12, 2)")]
    public decimal? MaxVideoMinutes { get; set; }

    [Column("features_json")]
    public string FeaturesJson { get; set; } = "[]";

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    public ICollection<UserSubscription> UserSubscriptions { get; set; } = [];
}

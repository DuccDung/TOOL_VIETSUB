using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("service_plan_cloud_policies")]
public sealed class ServicePlanCloudPolicy
{
    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("provider_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string ProviderCode { get; set; } = null!;

    [Column("allocation_mode")]
    [StringLength(20)]
    [Unicode(false)]
    public string AllocationMode { get; set; } = "SHARED";

    [Column("monthly_token_limit", TypeName = "decimal(20, 0)")]
    public decimal MonthlyTokenLimit { get; set; }

    [Column("allowed_models_json")]
    public string AllowedModelsJson { get; set; } = "[\"*\"]";

    [Column("allow_shared_fallback")]
    public bool AllowSharedFallback { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    public ServicePlan Plan { get; set; } = null!;
}


using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TOOL_VIETSUB.Models;

[Table("user_subscriptions")]
public sealed class UserSubscription
{
    [Key]
    [Column("subscription_id")]
    public Guid SubscriptionId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = null!;

    [Column("starts_at_utc")]
    [Precision(3)]
    public DateTime StartsAtUtc { get; set; }

    [Column("ends_at_utc")]
    [Precision(3)]
    public DateTime? EndsAtUtc { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    public User User { get; set; } = null!;

    public ServicePlan Plan { get; set; } = null!;
}

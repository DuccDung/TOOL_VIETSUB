using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("cloud_key_pool_plans")]
public sealed class CloudKeyPoolPlan
{
    [Column("pool_id")]
    public Guid PoolId { get; set; }

    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    public CloudKeyPool Pool { get; set; } = null!;

    public ServicePlan Plan { get; set; } = null!;
}


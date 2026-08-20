using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("cloud_credential_allocation_history")]
[Index(nameof(CredentialId), nameof(CreatedAtUtc), Name = "IX_cloud_allocation_history_credential", IsDescending = new[] { false, true })]
public sealed class CloudCredentialAllocationHistory
{
    [Key]
    [Column("allocation_history_id")]
    public Guid AllocationHistoryId { get; set; }

    [Column("credential_id")]
    public Guid CredentialId { get; set; }

    [Column("event_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string EventCode { get; set; } = null!;

    [Column("allocation_mode")]
    [StringLength(20)]
    [Unicode(false)]
    public string AllocationMode { get; set; } = null!;

    [Column("pool_id")]
    public Guid? PoolId { get; set; }

    [Column("assigned_user_id")]
    public Guid? AssignedUserId { get; set; }

    [Column("plan_id")]
    public Guid? PlanId { get; set; }

    [Column("source_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string? SourceCode { get; set; }

    [Column("actor_user_id")]
    public Guid? ActorUserId { get; set; }

    [Column("reason")]
    [StringLength(240)]
    public string? Reason { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    public CloudProviderCredential Credential { get; set; } = null!;

    public CloudKeyPool? Pool { get; set; }

    public User? AssignedUser { get; set; }

    public ServicePlan? Plan { get; set; }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TOOL_VIETSUB.Models;

[Table("usage_reservations")]
[Index(nameof(UserId), nameof(IdempotencyKey), Name = "UQ_usage_reservations_user_key", IsUnique = true)]
public sealed class UsageReservation
{
    [Key]
    [Column("reservation_id")]
    public Guid ReservationId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("project_id")]
    public Guid? ProjectId { get; set; }

    [Column("local_job_id")]
    public Guid? LocalJobId { get; set; }

    [Column("feature_code")]
    [StringLength(60)]
    [Unicode(false)]
    public string FeatureCode { get; set; } = null!;

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = null!;

    [Column("estimated_minutes", TypeName = "decimal(12,4)")]
    public decimal EstimatedMinutes { get; set; }

    [Column("committed_minutes", TypeName = "decimal(12,4)")]
    public decimal? CommittedMinutes { get; set; }

    [Column("idempotency_key")]
    [StringLength(100)]
    public string IdempotencyKey { get; set; } = null!;

    [Column("quota_period_start_utc")]
    [Precision(3)]
    public DateTime QuotaPeriodStartUtc { get; set; }

    [Column("expires_at_utc")]
    [Precision(3)]
    public DateTime ExpiresAtUtc { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("committed_at_utc")]
    [Precision(3)]
    public DateTime? CommittedAtUtc { get; set; }

    [Column("released_at_utc")]
    [Precision(3)]
    public DateTime? ReleasedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    public User User { get; set; } = null!;

    public Project? Project { get; set; }
}

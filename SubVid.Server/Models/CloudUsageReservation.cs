using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("cloud_usage_reservations")]
[Index(nameof(UserId), nameof(RequestId), Name = "UQ_cloud_reservations_user_request", IsUnique = true)]
[Index(nameof(UserId), nameof(QuotaPeriodStartUtc), nameof(StatusCode), Name = "IX_cloud_reservations_user_period")]
public sealed class CloudUsageReservation
{
    [Key]
    [Column("reservation_id")]
    public Guid ReservationId { get; set; }

    [Column("request_id")]
    public Guid RequestId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("project_id")]
    public Guid? ProjectId { get; set; }

    [Column("local_job_id")]
    public Guid? LocalJobId { get; set; }

    [Column("credential_id")]
    public Guid CredentialId { get; set; }

    [Column("operation_code")]
    [StringLength(40)]
    [Unicode(false)]
    public string OperationCode { get; set; } = null!;

    [Column("provider_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string ProviderCode { get; set; } = null!;

    [Column("model_id")]
    [StringLength(160)]
    public string ModelId { get; set; } = null!;

    [Column("unit_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string UnitCode { get; set; } = null!;

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = "HELD";

    [Column("estimated_input_units", TypeName = "decimal(20,0)")]
    public decimal EstimatedInputUnits { get; set; }

    [Column("estimated_output_units", TypeName = "decimal(20,0)")]
    public decimal EstimatedOutputUnits { get; set; }

    [Column("reserved_units", TypeName = "decimal(20,0)")]
    public decimal ReservedUnits { get; set; }

    [Column("committed_units", TypeName = "decimal(20,0)")]
    public decimal? CommittedUnits { get; set; }

    [Column("provider_request_id")]
    [StringLength(200)]
    public string? ProviderRequestId { get; set; }

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

    public CloudProviderCredential Credential { get; set; } = null!;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("cloud_usage_ledger")]
[Index(nameof(ReservationId), Name = "UQ_cloud_usage_ledger_reservation", IsUnique = true)]
[Index(nameof(UserId), nameof(QuotaPeriodStartUtc), nameof(UnitCode), Name = "IX_cloud_usage_ledger_user_period")]
public sealed class CloudUsageLedger
{
    [Key]
    [Column("ledger_id")]
    public Guid LedgerId { get; set; }

    [Column("reservation_id")]
    public Guid ReservationId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("credential_id")]
    public Guid CredentialId { get; set; }

    [Column("provider_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string ProviderCode { get; set; } = null!;

    [Column("model_id")]
    [StringLength(160)]
    public string ModelId { get; set; } = null!;

    [Column("operation_code")]
    [StringLength(40)]
    [Unicode(false)]
    public string OperationCode { get; set; } = null!;

    [Column("unit_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string UnitCode { get; set; } = null!;

    [Column("input_units", TypeName = "decimal(20,0)")]
    public decimal InputUnits { get; set; }

    [Column("output_units", TypeName = "decimal(20,0)")]
    public decimal OutputUnits { get; set; }

    [Column("cached_input_units", TypeName = "decimal(20,0)")]
    public decimal CachedInputUnits { get; set; }

    [Column("total_units", TypeName = "decimal(20,0)")]
    public decimal TotalUnits { get; set; }

    [Column("api_request_count")]
    public int ApiRequestCount { get; set; }

    [Column("retry_request_count")]
    public int RetryRequestCount { get; set; }

    [Column("provider_request_id")]
    [StringLength(200)]
    public string? ProviderRequestId { get; set; }

    [Column("quota_period_start_utc")]
    [Precision(3)]
    public DateTime QuotaPeriodStartUtc { get; set; }

    [Column("occurred_at_utc")]
    [Precision(3)]
    public DateTime OccurredAtUtc { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    public CloudUsageReservation Reservation { get; set; } = null!;
}

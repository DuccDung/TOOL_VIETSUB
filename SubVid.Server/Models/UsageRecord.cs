using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("usage_records")]
[Index("UserId", "OccurredAtUtc", Name = "IX_usage_records_user_period", IsDescending = new[] { false, true })]
public partial class UsageRecord
{
    [Key]
    [Column("usage_record_id")]
    public Guid UsageRecordId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("project_id")]
    public Guid? ProjectId { get; set; }

    [Column("job_id")]
    public Guid? JobId { get; set; }

    [Column("provider_code")]
    [StringLength(100)]
    public string ProviderCode { get; set; } = null!;

    [Column("operation_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string OperationCode { get; set; } = null!;

    [Column("quantity", TypeName = "decimal(18, 6)")]
    public decimal Quantity { get; set; }

    [Column("unit_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string UnitCode { get; set; } = null!;

    [Column("unit_cost", TypeName = "decimal(19, 8)")]
    public decimal? UnitCost { get; set; }

    [Column("total_cost", TypeName = "decimal(19, 6)")]
    public decimal? TotalCost { get; set; }

    [Column("currency_code")]
    [StringLength(3)]
    [Unicode(false)]
    public string CurrencyCode { get; set; } = null!;

    [Column("external_request_id")]
    [StringLength(200)]
    public string? ExternalRequestId { get; set; }

    [Column("metadata_json")]
    public string? MetadataJson { get; set; }

    [Column("occurred_at_utc")]
    [Precision(3)]
    public DateTime OccurredAtUtc { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [ForeignKey("JobId")]
    [InverseProperty("UsageRecords")]
    public virtual Job? Job { get; set; }

    [ForeignKey("ProjectId")]
    [InverseProperty("UsageRecords")]
    public virtual Project? Project { get; set; }

    [ForeignKey("UserId")]
    [InverseProperty("UsageRecords")]
    public virtual User User { get; set; } = null!;
}

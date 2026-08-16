using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("job_steps")]
[Index("StatusCode", "NextRetryAtUtc", Name = "IX_job_steps_retry")]
[Index("JobId", "StepCode", Name = "UQ_job_steps_code", IsUnique = true)]
[Index("JobId", "StepOrder", Name = "UQ_job_steps_order", IsUnique = true)]
public partial class JobStep
{
    [Key]
    [Column("job_step_id")]
    public Guid JobStepId { get; set; }

    [Column("job_id")]
    public Guid JobId { get; set; }

    [Column("step_order")]
    public byte StepOrder { get; set; }

    [Column("step_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string StepCode { get; set; } = null!;

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = null!;

    [Column("progress_percent", TypeName = "decimal(5, 2)")]
    public decimal ProgressPercent { get; set; }

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("max_attempts")]
    public int MaxAttempts { get; set; }

    [Column("checkpoint_media_file_id")]
    public Guid? CheckpointMediaFileId { get; set; }

    [Column("input_json")]
    public string? InputJson { get; set; }

    [Column("output_json")]
    public string? OutputJson { get; set; }

    [Column("error_code")]
    [StringLength(100)]
    public string? ErrorCode { get; set; }

    [Column("error_message")]
    [StringLength(2000)]
    public string? ErrorMessage { get; set; }

    [Column("next_retry_at_utc")]
    [Precision(3)]
    public DateTime? NextRetryAtUtc { get; set; }

    [Column("started_at_utc")]
    [Precision(3)]
    public DateTime? StartedAtUtc { get; set; }

    [Column("completed_at_utc")]
    [Precision(3)]
    public DateTime? CompletedAtUtc { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    [ForeignKey("CheckpointMediaFileId")]
    [InverseProperty("JobSteps")]
    public virtual MediaFile? CheckpointMediaFile { get; set; }

    [ForeignKey("JobId")]
    [InverseProperty("JobSteps")]
    public virtual Job Job { get; set; } = null!;

    [InverseProperty("JobStep")]
    public virtual ICollection<JobEvent> JobEvents { get; set; } = new List<JobEvent>();
}

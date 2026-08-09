using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TOOL_VIETSUB.Models;

[Table("jobs")]
[Index("ProjectId", "SubmittedAtUtc", Name = "IX_jobs_project_history", IsDescending = new[] { false, true })]
[Index("StatusCode", "PriorityNo", "NextRetryAtUtc", "SubmittedAtUtc", Name = "IX_jobs_queue", IsDescending = new[] { false, true, false, false })]
public partial class Job
{
    [Key]
    [Column("job_id")]
    public Guid JobId { get; set; }

    [Column("project_id")]
    public Guid ProjectId { get; set; }

    [Column("input_media_file_id")]
    public Guid? InputMediaFileId { get; set; }

    [Column("output_media_file_id")]
    public Guid? OutputMediaFileId { get; set; }

    [Column("job_type")]
    [StringLength(30)]
    [Unicode(false)]
    public string JobType { get; set; } = null!;

    [Column("status_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string StatusCode { get; set; } = null!;

    [Column("progress_percent", TypeName = "decimal(5, 2)")]
    public decimal ProgressPercent { get; set; }

    [Column("priority_no")]
    public byte PriorityNo { get; set; }

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("max_attempts")]
    public int MaxAttempts { get; set; }

    [Column("idempotency_key")]
    [StringLength(200)]
    public string? IdempotencyKey { get; set; }

    [Column("locked_by_worker")]
    [StringLength(200)]
    public string? LockedByWorker { get; set; }

    [Column("lock_expires_at_utc")]
    [Precision(3)]
    public DateTime? LockExpiresAtUtc { get; set; }

    [Column("next_retry_at_utc")]
    [Precision(3)]
    public DateTime? NextRetryAtUtc { get; set; }

    [Column("error_code")]
    [StringLength(100)]
    public string? ErrorCode { get; set; }

    [Column("error_message")]
    [StringLength(2000)]
    public string? ErrorMessage { get; set; }

    [Column("request_config_json")]
    public string? RequestConfigJson { get; set; }

    [Column("result_json")]
    public string? ResultJson { get; set; }

    [Column("submitted_at_utc")]
    [Precision(3)]
    public DateTime SubmittedAtUtc { get; set; }

    [Column("started_at_utc")]
    [Precision(3)]
    public DateTime? StartedAtUtc { get; set; }

    [Column("completed_at_utc")]
    [Precision(3)]
    public DateTime? CompletedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    [ForeignKey("InputMediaFileId")]
    [InverseProperty("JobInputMediaFiles")]
    public virtual MediaFile? InputMediaFile { get; set; }

    [InverseProperty("Job")]
    public virtual ICollection<JobEvent> JobEvents { get; set; } = new List<JobEvent>();

    [InverseProperty("Job")]
    public virtual ICollection<JobStep> JobSteps { get; set; } = new List<JobStep>();

    [ForeignKey("OutputMediaFileId")]
    [InverseProperty("JobOutputMediaFiles")]
    public virtual MediaFile? OutputMediaFile { get; set; }

    [ForeignKey("ProjectId")]
    [InverseProperty("Jobs")]
    public virtual Project Project { get; set; } = null!;

    [InverseProperty("SourceJob")]
    public virtual ICollection<Segment> Segments { get; set; } = new List<Segment>();

    [InverseProperty("Job")]
    public virtual ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();
}

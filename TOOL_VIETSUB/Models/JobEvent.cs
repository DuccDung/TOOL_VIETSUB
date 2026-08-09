using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TOOL_VIETSUB.Models;

[Table("job_events")]
[Index("JobId", "CreatedAtUtc", "JobEventId", Name = "IX_job_events_job_timeline", IsDescending = new[] { false, true, true })]
public partial class JobEvent
{
    [Key]
    [Column("job_event_id")]
    public long JobEventId { get; set; }

    [Column("job_id")]
    public Guid JobId { get; set; }

    [Column("job_step_id")]
    public Guid? JobStepId { get; set; }

    [Column("event_type")]
    [StringLength(30)]
    [Unicode(false)]
    public string EventType { get; set; } = null!;

    [Column("level_code")]
    [StringLength(10)]
    [Unicode(false)]
    public string LevelCode { get; set; } = null!;

    [Column("progress_percent", TypeName = "decimal(5, 2)")]
    public decimal? ProgressPercent { get; set; }

    [Column("message")]
    [StringLength(2000)]
    public string Message { get; set; } = null!;

    [Column("event_data_json")]
    public string? EventDataJson { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [ForeignKey("JobId")]
    [InverseProperty("JobEvents")]
    public virtual Job Job { get; set; } = null!;

    [ForeignKey("JobStepId")]
    [InverseProperty("JobEvents")]
    public virtual JobStep? JobStep { get; set; }
}

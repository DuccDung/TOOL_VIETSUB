using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("projects")]
public partial class Project
{
    [Key]
    [Column("project_id")]
    public Guid ProjectId { get; set; }

    [Column("owner_user_id")]
    public Guid OwnerUserId { get; set; }

    [Column("project_name")]
    [StringLength(250)]
    public string ProjectName { get; set; } = null!;

    [Column("status_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string StatusCode { get; set; } = null!;

    [Column("source_language_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string? SourceLanguageCode { get; set; }

    [Column("target_language_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string TargetLanguageCode { get; set; } = null!;

    [Column("current_transcript_version")]
    public int CurrentTranscriptVersion { get; set; }

    [Column("settings_json")]
    public string? SettingsJson { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("deleted_at_utc")]
    [Precision(3)]
    public DateTime? DeletedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    [InverseProperty("Project")]
    public virtual ICollection<GlossaryEntry> GlossaryEntries { get; set; } = new List<GlossaryEntry>();

    [InverseProperty("Project")]
    public virtual ICollection<Job> Jobs { get; set; } = new List<Job>();

    [InverseProperty("Project")]
    public virtual ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();

    [ForeignKey("OwnerUserId")]
    [InverseProperty("Projects")]
    public virtual User OwnerUser { get; set; } = null!;

    [InverseProperty("Project")]
    public virtual ICollection<Segment> Segments { get; set; } = new List<Segment>();

    [InverseProperty("Project")]
    public virtual ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();
}

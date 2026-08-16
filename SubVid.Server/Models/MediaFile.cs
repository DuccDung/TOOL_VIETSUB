using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("media_files")]
public partial class MediaFile
{
    [Key]
    [Column("media_file_id")]
    public Guid MediaFileId { get; set; }

    [Column("project_id")]
    public Guid ProjectId { get; set; }

    [Column("uploaded_by_user_id")]
    public Guid? UploadedByUserId { get; set; }

    [Column("parent_media_file_id")]
    public Guid? ParentMediaFileId { get; set; }

    [Column("file_role")]
    [StringLength(30)]
    [Unicode(false)]
    public string FileRole { get; set; } = null!;

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = null!;

    [Column("storage_provider")]
    [StringLength(30)]
    [Unicode(false)]
    public string StorageProvider { get; set; } = null!;

    [Column("storage_container")]
    [StringLength(200)]
    public string? StorageContainer { get; set; }

    [Column("object_key")]
    public string ObjectKey { get; set; } = null!;

    [Column("original_file_name")]
    [StringLength(260)]
    public string? OriginalFileName { get; set; }

    [Column("content_type")]
    [StringLength(127)]
    public string? ContentType { get; set; }

    [Column("file_extension")]
    [StringLength(20)]
    [Unicode(false)]
    public string? FileExtension { get; set; }

    [Column("size_bytes")]
    public long? SizeBytes { get; set; }

    [Column("duration_ms")]
    public long? DurationMs { get; set; }

    [Column("checksum_sha256")]
    [StringLength(64)]
    [Unicode(false)]
    public string? ChecksumSha256 { get; set; }

    [Column("metadata_json")]
    public string? MetadataJson { get; set; }

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

    [InverseProperty("ParentMediaFile")]
    public virtual ICollection<MediaFile> InverseParentMediaFile { get; set; } = new List<MediaFile>();

    [InverseProperty("InputMediaFile")]
    public virtual ICollection<Job> JobInputMediaFiles { get; set; } = new List<Job>();

    [InverseProperty("OutputMediaFile")]
    public virtual ICollection<Job> JobOutputMediaFiles { get; set; } = new List<Job>();

    [InverseProperty("CheckpointMediaFile")]
    public virtual ICollection<JobStep> JobSteps { get; set; } = new List<JobStep>();

    [ForeignKey("ParentMediaFileId")]
    [InverseProperty("InverseParentMediaFile")]
    public virtual MediaFile? ParentMediaFile { get; set; }

    [ForeignKey("ProjectId")]
    [InverseProperty("MediaFiles")]
    public virtual Project Project { get; set; } = null!;

    [InverseProperty("TtsMediaFile")]
    public virtual ICollection<Segment> Segments { get; set; } = new List<Segment>();

    [ForeignKey("UploadedByUserId")]
    [InverseProperty("MediaFiles")]
    public virtual User? UploadedByUser { get; set; }
}

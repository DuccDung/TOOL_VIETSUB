using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TOOL_VIETSUB.Models;

[Table("users")]
public partial class User
{
    [Key]
    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("email")]
    [StringLength(320)]
    public string Email { get; set; } = null!;

    [Column("email_normalized")]
    [StringLength(320)]
    public string? EmailNormalized { get; set; }

    [Column("password_hash")]
    [StringLength(1000)]
    public string PasswordHash { get; set; } = null!;

    [Column("display_name")]
    [StringLength(200)]
    public string DisplayName { get; set; } = null!;

    [Column("role_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string RoleCode { get; set; } = null!;

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = null!;

    [Column("monthly_quota_minutes", TypeName = "decimal(12, 2)")]
    public decimal? MonthlyQuotaMinutes { get; set; }

    [Column("email_confirmed")]
    public bool EmailConfirmed { get; set; }

    [Column("last_login_at_utc")]
    [Precision(3)]
    public DateTime? LastLoginAtUtc { get; set; }

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

    [InverseProperty("UploadedByUser")]
    public virtual ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();

    [InverseProperty("OwnerUser")]
    public virtual ICollection<Project> Projects { get; set; } = new List<Project>();

    [InverseProperty("ApprovedByUser")]
    public virtual ICollection<Segment> Segments { get; set; } = new List<Segment>();

    [InverseProperty("User")]
    public virtual ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();

    [InverseProperty("OwnerUser")]
    public virtual ICollection<Voice> Voices { get; set; } = new List<Voice>();
}

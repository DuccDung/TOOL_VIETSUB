using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TOOL_VIETSUB.Models;

[Table("voices")]
public partial class Voice
{
    [Key]
    [Column("voice_id")]
    public Guid VoiceId { get; set; }

    [Column("owner_user_id")]
    public Guid? OwnerUserId { get; set; }

    [Column("provider_code")]
    [StringLength(100)]
    public string ProviderCode { get; set; } = null!;

    [Column("provider_voice_id")]
    [StringLength(200)]
    public string ProviderVoiceId { get; set; } = null!;

    [Column("display_name")]
    [StringLength(200)]
    public string DisplayName { get; set; } = null!;

    [Column("language_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string LanguageCode { get; set; } = null!;

    [Column("voice_type")]
    [StringLength(20)]
    [Unicode(false)]
    public string VoiceType { get; set; } = null!;

    [Column("gender_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string GenderCode { get; set; } = null!;

    [Column("description")]
    [StringLength(1000)]
    public string? Description { get; set; }

    [Column("style_config_json")]
    public string? StyleConfigJson { get; set; }

    [Column("usage_rights_note")]
    [StringLength(1000)]
    public string? UsageRightsNote { get; set; }

    [Column("consent_confirmed_at_utc")]
    [Precision(3)]
    public DateTime? ConsentConfirmedAtUtc { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

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

    [ForeignKey("OwnerUserId")]
    [InverseProperty("Voices")]
    public virtual User? OwnerUser { get; set; }

    [InverseProperty("Voice")]
    public virtual ICollection<Segment> Segments { get; set; } = new List<Segment>();
}

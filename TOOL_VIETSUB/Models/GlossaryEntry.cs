using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TOOL_VIETSUB.Models;

[Table("glossary_entries")]
[Index("ProjectId", "SourceTerm", Name = "UQ_glossary_entries_project_term", IsUnique = true)]
public partial class GlossaryEntry
{
    [Key]
    [Column("glossary_entry_id")]
    public Guid GlossaryEntryId { get; set; }

    [Column("project_id")]
    public Guid ProjectId { get; set; }

    [Column("source_term")]
    [StringLength(300)]
    public string SourceTerm { get; set; } = null!;

    [Column("translated_term")]
    [StringLength(500)]
    public string TranslatedTerm { get; set; } = null!;

    [Column("note")]
    [StringLength(1000)]
    public string? Note { get; set; }

    [Column("is_case_sensitive")]
    public bool IsCaseSensitive { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [ForeignKey("ProjectId")]
    [InverseProperty("GlossaryEntries")]
    public virtual Project Project { get; set; } = null!;
}

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("schema_versions")]
public partial class SchemaVersion
{
    [Key]
    [Column("version_no")]
    public int VersionNo { get; set; }

    [Column("version_name")]
    [StringLength(100)]
    public string VersionName { get; set; } = null!;

    [Column("applied_at_utc")]
    [Precision(3)]
    public DateTime AppliedAtUtc { get; set; }
}

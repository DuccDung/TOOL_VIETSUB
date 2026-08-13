using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TOOL_VIETSUB.Models;

[Table("security_audit_logs")]
public sealed class SecurityAuditLog
{
    [Key]
    [Column("audit_log_id")]
    public long AuditLogId { get; set; }

    [Column("user_id")]
    public Guid? UserId { get; set; }

    [Column("event_code")]
    [StringLength(50)]
    [Unicode(false)]
    public string EventCode { get; set; } = null!;

    [Column("outcome_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string OutcomeCode { get; set; } = null!;

    [Column("ip_address")]
    [StringLength(45)]
    [Unicode(false)]
    public string? IpAddress { get; set; }

    [Column("device_id")]
    [StringLength(200)]
    public string? DeviceId { get; set; }

    [Column("details_json")]
    public string? DetailsJson { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    public User? User { get; set; }
}

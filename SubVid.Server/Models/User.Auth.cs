using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

public partial class User
{
    [Column("password_changed_at_utc")]
    [Precision(3)]
    public DateTime? PasswordChangedAtUtc { get; set; }

    public virtual ICollection<AuthSession> AuthSessions { get; set; } = [];

    public virtual ICollection<UserSubscription> UserSubscriptions { get; set; } = [];

    public virtual ICollection<SecurityAuditLog> SecurityAuditLogs { get; set; } = [];
}

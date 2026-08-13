namespace TOOL_VIETSUB.Models;

public partial class User
{
    public virtual ICollection<AuthSession> AuthSessions { get; set; } = [];

    public virtual ICollection<UserSubscription> UserSubscriptions { get; set; } = [];

    public virtual ICollection<SecurityAuditLog> SecurityAuditLogs { get; set; } = [];
}

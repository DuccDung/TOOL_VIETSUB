using Microsoft.EntityFrameworkCore;
using TOOL_VIETSUB.Models;

namespace TOOL_VIETSUB.Data;

public partial class ToolVietSubDbContext
{
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    public DbSet<SecurityAuditLog> SecurityAuditLogs => Set<SecurityAuditLog>();

    public DbSet<RegistrationChallenge> RegistrationChallenges => Set<RegistrationChallenge>();

    public DbSet<ServicePlan> ServicePlans => Set<ServicePlan>();

    public DbSet<UserSubscription> UserSubscriptions => Set<UserSubscription>();

    public DbSet<UsageReservation> UsageReservations => Set<UsageReservation>();

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServicePlan>(entity =>
        {
            entity.HasIndex(item => item.PlanCode, "UQ_service_plans_code").IsUnique();
            entity.Property(item => item.PlanId)
                .HasDefaultValueSql("(newsequentialid())", "DF_service_plans_id");
            entity.Property(item => item.IsActive)
                .HasDefaultValue(true, "DF_service_plans_is_active");
            entity.Property(item => item.CreatedAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_service_plans_created_at");
            entity.Property(item => item.UpdatedAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_service_plans_updated_at");
            entity.Property(item => item.RowVersion).IsRowVersion().IsConcurrencyToken();
        });

        modelBuilder.Entity<UserSubscription>(entity =>
        {
            entity.HasIndex(
                item => new { item.UserId, item.StatusCode, item.StartsAtUtc },
                "IX_user_subscriptions_current").IsDescending(false, false, true);
            entity.Property(item => item.SubscriptionId)
                .HasDefaultValueSql("(newsequentialid())", "DF_user_subscriptions_id");
            entity.Property(item => item.StatusCode)
                .HasDefaultValue("ACTIVE", "DF_user_subscriptions_status");
            entity.Property(item => item.StartsAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_user_subscriptions_starts_at");
            entity.Property(item => item.CreatedAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_user_subscriptions_created_at");
            entity.Property(item => item.UpdatedAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_user_subscriptions_updated_at");
            entity.Property(item => item.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasOne(item => item.User)
                .WithMany(user => user.UserSubscriptions)
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_user_subscriptions_user");
            entity.HasOne(item => item.Plan)
                .WithMany(plan => plan.UserSubscriptions)
                .HasForeignKey(item => item.PlanId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_user_subscriptions_plan");
        });

        modelBuilder.Entity<UsageReservation>(entity =>
        {
            entity.Property(item => item.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();

            entity.HasOne(item => item.User)
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_usage_reservations_user");

            entity.HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_usage_reservations_project");
        });

        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.HasIndex(item => item.RefreshTokenHash, "UX_auth_sessions_refresh_hash").IsUnique();
            entity.HasIndex(
                item => new { item.UserId, item.ExpiresAtUtc },
                "IX_auth_sessions_user_active").IsDescending(false, true);
            entity.Property(item => item.SessionId)
                .HasDefaultValueSql("(newsequentialid())", "DF_auth_sessions_id");
            entity.Property(item => item.CreatedAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_auth_sessions_created_at");
            entity.Property(item => item.LastSeenAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_auth_sessions_last_seen");
            entity.HasOne(item => item.User)
                .WithMany(user => user.AuthSessions)
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_auth_sessions_user");
            entity.HasOne<AuthSession>()
                .WithMany()
                .HasForeignKey(item => item.ReplacedBySessionId)
                .HasConstraintName("FK_auth_sessions_replacement");
        });

        modelBuilder.Entity<SecurityAuditLog>(entity =>
        {
            entity.HasIndex(
                item => new { item.UserId, item.CreatedAtUtc },
                "IX_security_audit_logs_user_timeline").IsDescending(false, true);
            entity.Property(item => item.AuditLogId).UseIdentityColumn();
            entity.Property(item => item.CreatedAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_security_audit_logs_created_at");
            entity.HasOne(item => item.User)
                .WithMany(user => user.SecurityAuditLogs)
                .HasForeignKey(item => item.UserId)
                .HasConstraintName("FK_security_audit_logs_user");
        });

        modelBuilder.Entity<RegistrationChallenge>(entity =>
        {
            entity.HasIndex(item => item.EmailNormalized, "UX_registration_challenges_pending_email")
                .IsUnique()
                .HasFilter("([status_code]='PENDING')");
            entity.HasIndex(
                item => new { item.StatusCode, item.ExpiresAtUtc },
                "IX_registration_challenges_expiry");
            entity.Property(item => item.ChallengeId)
                .HasDefaultValueSql("(newsequentialid())", "DF_registration_challenges_id");
            entity.Property(item => item.EmailNormalized)
                .HasComputedColumnSql("(upper(ltrim(rtrim([email]))))", true);
            entity.Property(item => item.StatusCode)
                .HasDefaultValue("PENDING", "DF_registration_challenges_status");
            entity.Property(item => item.CreatedAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_registration_challenges_created_at");
            entity.Property(item => item.UpdatedAtUtc)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_registration_challenges_updated_at");
            entity.Property(item => item.RowVersion).IsRowVersion().IsConcurrencyToken();
        });
    }
}

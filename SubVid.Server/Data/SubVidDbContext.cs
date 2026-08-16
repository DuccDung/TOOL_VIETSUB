using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Models;

namespace SubVid.Server.Data;

public partial class SubVidDbContext : DbContext
{
    public SubVidDbContext(DbContextOptions<SubVidDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<GlossaryEntry> GlossaryEntries { get; set; }

    public virtual DbSet<Job> Jobs { get; set; }

    public virtual DbSet<JobEvent> JobEvents { get; set; }

    public virtual DbSet<JobStep> JobSteps { get; set; }

    public virtual DbSet<MediaFile> MediaFiles { get; set; }

    public virtual DbSet<Project> Projects { get; set; }

    public virtual DbSet<SchemaVersion> SchemaVersions { get; set; }

    public virtual DbSet<Segment> Segments { get; set; }

    public virtual DbSet<UsageRecord> UsageRecords { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<Voice> Voices { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GlossaryEntry>(entity =>
        {
            entity.Property(e => e.GlossaryEntryId).HasDefaultValueSql("(newsequentialid())", "DF_glossary_entries_id");
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_glossary_created_at");
            entity.Property(e => e.IsActive).HasDefaultValue(true, "DF_glossary_is_active");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_glossary_updated_at");

            entity.HasOne(d => d.Project).WithMany(p => p.GlossaryEntries)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_glossary_entries_project");
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.IdempotencyKey }, "UX_jobs_project_idempotency")
                .IsUnique()
                .HasFilter("([idempotency_key] IS NOT NULL)");

            entity.Property(e => e.JobId).HasDefaultValueSql("(newsequentialid())", "DF_jobs_job_id");
            entity.Property(e => e.JobType).HasDefaultValue("FULL_PIPELINE", "DF_jobs_job_type");
            entity.Property(e => e.MaxAttempts).HasDefaultValue(3, "DF_jobs_max_attempts");
            entity.Property(e => e.PriorityNo).HasDefaultValue((byte)5, "DF_jobs_priority");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.StatusCode).HasDefaultValue("UPLOADED", "DF_jobs_status");
            entity.Property(e => e.SubmittedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_jobs_submitted_at");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_jobs_updated_at");

            entity.HasOne(d => d.InputMediaFile).WithMany(p => p.JobInputMediaFiles).HasConstraintName("FK_jobs_input_media");

            entity.HasOne(d => d.OutputMediaFile).WithMany(p => p.JobOutputMediaFiles).HasConstraintName("FK_jobs_output_media");

            entity.HasOne(d => d.Project).WithMany(p => p.Jobs)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_jobs_project");
        });

        modelBuilder.Entity<JobEvent>(entity =>
        {
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_job_events_created_at");
            entity.Property(e => e.LevelCode).HasDefaultValue("INFO", "DF_job_events_level");

            entity.HasOne(d => d.Job).WithMany(p => p.JobEvents)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_job_events_job");

            entity.HasOne(d => d.JobStep).WithMany(p => p.JobEvents).HasConstraintName("FK_job_events_step");
        });

        modelBuilder.Entity<JobStep>(entity =>
        {
            entity.Property(e => e.JobStepId).HasDefaultValueSql("(newsequentialid())", "DF_job_steps_id");
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_job_steps_created_at");
            entity.Property(e => e.MaxAttempts).HasDefaultValue(3, "DF_job_steps_max_attempts");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.StatusCode).HasDefaultValue("PENDING", "DF_job_steps_status");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_job_steps_updated_at");

            entity.HasOne(d => d.CheckpointMediaFile).WithMany(p => p.JobSteps).HasConstraintName("FK_job_steps_checkpoint_media");

            entity.HasOne(d => d.Job).WithMany(p => p.JobSteps).HasConstraintName("FK_job_steps_job");
        });

        modelBuilder.Entity<MediaFile>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.FileRole, e.StatusCode }, "IX_media_files_project_role").HasFilter("([deleted_at_utc] IS NULL)");

            entity.HasIndex(e => e.ObjectKey, "UX_media_files_object_key_active")
                .IsUnique()
                .HasFilter("([deleted_at_utc] IS NULL)");

            entity.Property(e => e.MediaFileId).HasDefaultValueSql("(newsequentialid())", "DF_media_files_id");
            entity.Property(e => e.ChecksumSha256).IsFixedLength();
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_media_files_created_at");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.StatusCode).HasDefaultValue("UPLOADING", "DF_media_files_status");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_media_files_updated_at");

            entity.HasOne(d => d.ParentMediaFile).WithMany(p => p.InverseParentMediaFile).HasConstraintName("FK_media_files_parent");

            entity.HasOne(d => d.Project).WithMany(p => p.MediaFiles)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_media_files_project");

            entity.HasOne(d => d.UploadedByUser).WithMany(p => p.MediaFiles).HasConstraintName("FK_media_files_uploader");
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasIndex(e => new { e.OwnerUserId, e.StatusCode, e.UpdatedAtUtc }, "IX_projects_owner_status")
                .IsDescending(false, false, true)
                .HasFilter("([deleted_at_utc] IS NULL)");

            entity.Property(e => e.ProjectId).HasDefaultValueSql("(newsequentialid())", "DF_projects_project_id");
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_projects_created_at");
            entity.Property(e => e.CurrentTranscriptVersion).HasDefaultValue(1, "DF_projects_transcript_version");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.StatusCode).HasDefaultValue("DRAFT", "DF_projects_status_code");
            entity.Property(e => e.TargetLanguageCode).HasDefaultValue("vi", "DF_projects_target_language");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_projects_updated_at");

            entity.HasOne(d => d.OwnerUser).WithMany(p => p.Projects)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_projects_owner");
        });

        modelBuilder.Entity<SchemaVersion>(entity =>
        {
            entity.Property(e => e.VersionNo).ValueGeneratedNever();
            entity.Property(e => e.AppliedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_schema_versions_applied_at");
        });

        modelBuilder.Entity<Segment>(entity =>
        {
            entity.Property(e => e.SegmentId).HasDefaultValueSql("(newsequentialid())", "DF_segments_id");
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_segments_created_at");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.SpeakerLabel).HasDefaultValue("speaker_1", "DF_segments_speaker");
            entity.Property(e => e.SpeechRate).HasDefaultValue(1.000m, "DF_segments_speech_rate");
            entity.Property(e => e.TranscriptVersion).HasDefaultValue(1, "DF_segments_transcript_version");
            entity.Property(e => e.TranslationStatus).HasDefaultValue("PENDING", "DF_segments_translation_status");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_segments_updated_at");

            entity.HasOne(d => d.ApprovedByUser).WithMany(p => p.Segments).HasConstraintName("FK_segments_approved_by");

            entity.HasOne(d => d.Project).WithMany(p => p.Segments)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_segments_project");

            entity.HasOne(d => d.SourceJob).WithMany(p => p.Segments).HasConstraintName("FK_segments_source_job");

            entity.HasOne(d => d.TtsMediaFile).WithMany(p => p.Segments).HasConstraintName("FK_segments_tts_media");

            entity.HasOne(d => d.Voice).WithMany(p => p.Segments).HasConstraintName("FK_segments_voice");
        });

        modelBuilder.Entity<UsageRecord>(entity =>
        {
            entity.HasIndex(e => new { e.ProviderCode, e.ExternalRequestId }, "UX_usage_records_provider_request")
                .IsUnique()
                .HasFilter("([external_request_id] IS NOT NULL)");

            entity.Property(e => e.UsageRecordId).HasDefaultValueSql("(newsequentialid())", "DF_usage_records_id");
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_usage_records_created_at");
            entity.Property(e => e.CurrencyCode)
                .IsFixedLength()
                .HasDefaultValue("USD", "DF_usage_records_currency");
            entity.Property(e => e.OccurredAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_usage_records_occurred_at");

            entity.HasOne(d => d.Job).WithMany(p => p.UsageRecords).HasConstraintName("FK_usage_records_job");

            entity.HasOne(d => d.Project).WithMany(p => p.UsageRecords).HasConstraintName("FK_usage_records_project");

            entity.HasOne(d => d.User).WithMany(p => p.UsageRecords)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_usage_records_user");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.EmailNormalized, "UX_users_email_active")
                .IsUnique()
                .HasFilter("([deleted_at_utc] IS NULL)");

            entity.Property(e => e.UserId).HasDefaultValueSql("(newsequentialid())", "DF_users_user_id");
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_users_created_at");
            entity.Property(e => e.EmailNormalized).HasComputedColumnSql("(upper(ltrim(rtrim([email]))))", true);
            entity.Property(e => e.RoleCode).HasDefaultValue("USER", "DF_users_role_code");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.StatusCode).HasDefaultValue("ACTIVE", "DF_users_status_code");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_users_updated_at");
        });

        modelBuilder.Entity<Voice>(entity =>
        {
            entity.HasIndex(e => new { e.LanguageCode, e.IsActive }, "IX_voices_language_active").HasFilter("([deleted_at_utc] IS NULL)");

            entity.HasIndex(e => new { e.ProviderCode, e.ProviderVoiceId }, "UX_voices_provider_voice_active")
                .IsUnique()
                .HasFilter("([deleted_at_utc] IS NULL)");

            entity.Property(e => e.VoiceId).HasDefaultValueSql("(newsequentialid())", "DF_voices_voice_id");
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_voices_created_at");
            entity.Property(e => e.GenderCode).HasDefaultValue("UNSPECIFIED", "DF_voices_gender_code");
            entity.Property(e => e.IsActive).HasDefaultValue(true, "DF_voices_is_active");
            entity.Property(e => e.LanguageCode).HasDefaultValue("vi-VN", "DF_voices_language_code");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_voices_updated_at");
            entity.Property(e => e.VoiceType).HasDefaultValue("PRESET", "DF_voices_voice_type");

            entity.HasOne(d => d.OwnerUser).WithMany(p => p.Voices).HasConstraintName("FK_voices_owner");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}

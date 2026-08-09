/*
    TOOL_VIETSUB - Database schema V1
    Target: Microsoft SQL Server 2019+

    This script:
      - Creates database TOOL_VIETSUB when it does not exist.
      - Creates the V1 tables, constraints, and indexes idempotently.
      - Stores all timestamps in UTC.

    Security note:
      API keys and provider secrets must be stored in application secrets or a
      dedicated secret manager, not in this database.
*/

USE [master];
GO

IF DB_ID(N'TOOL_VIETSUB') IS NULL
BEGIN
    EXEC(N'CREATE DATABASE [TOOL_VIETSUB]');
END;
GO

USE [TOOL_VIETSUB];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

/* Tracks database deployments. */
IF OBJECT_ID(N'dbo.schema_versions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.schema_versions
    (
        version_no     INT            NOT NULL,
        version_name   NVARCHAR(100)  NOT NULL,
        applied_at_utc DATETIME2(3)   NOT NULL
            CONSTRAINT DF_schema_versions_applied_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_schema_versions
            PRIMARY KEY CLUSTERED (version_no)
    );
END;
GO

/* Application accounts and quotas. */
IF OBJECT_ID(N'dbo.users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.users
    (
        user_id               UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_users_user_id DEFAULT NEWSEQUENTIALID(),
        email                 NVARCHAR(320)    NOT NULL,
        email_normalized      AS UPPER(LTRIM(RTRIM(email))) PERSISTED,
        password_hash         NVARCHAR(1000)   NOT NULL,
        display_name          NVARCHAR(200)    NOT NULL,
        role_code             VARCHAR(20)      NOT NULL
            CONSTRAINT DF_users_role_code DEFAULT 'USER',
        status_code           VARCHAR(20)      NOT NULL
            CONSTRAINT DF_users_status_code DEFAULT 'ACTIVE',
        monthly_quota_minutes DECIMAL(12,2)    NULL,
        email_confirmed       BIT              NOT NULL
            CONSTRAINT DF_users_email_confirmed DEFAULT 0,
        last_login_at_utc     DATETIME2(3)     NULL,
        created_at_utc        DATETIME2(3)     NOT NULL
            CONSTRAINT DF_users_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc        DATETIME2(3)     NOT NULL
            CONSTRAINT DF_users_updated_at DEFAULT SYSUTCDATETIME(),
        deleted_at_utc        DATETIME2(3)     NULL,
        row_version           ROWVERSION       NOT NULL,

        CONSTRAINT PK_users PRIMARY KEY CLUSTERED (user_id),
        CONSTRAINT CK_users_email_not_blank
            CHECK (LEN(LTRIM(RTRIM(email))) > 0),
        CONSTRAINT CK_users_display_name_not_blank
            CHECK (LEN(LTRIM(RTRIM(display_name))) > 0),
        CONSTRAINT CK_users_role_code
            CHECK (role_code IN ('USER', 'ADMIN')),
        CONSTRAINT CK_users_status_code
            CHECK (status_code IN ('ACTIVE', 'SUSPENDED', 'DISABLED')),
        CONSTRAINT CK_users_monthly_quota
            CHECK (monthly_quota_minutes IS NULL OR monthly_quota_minutes >= 0)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.users')
      AND name = N'UX_users_email_active'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_users_email_active
        ON dbo.users (email_normalized)
        WHERE deleted_at_utc IS NULL;
END;
GO

/* A project groups one source video and all generated artifacts. */
IF OBJECT_ID(N'dbo.projects', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.projects
    (
        project_id                UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_projects_project_id DEFAULT NEWSEQUENTIALID(),
        owner_user_id             UNIQUEIDENTIFIER NOT NULL,
        project_name              NVARCHAR(250)    NOT NULL,
        status_code               VARCHAR(30)      NOT NULL
            CONSTRAINT DF_projects_status_code DEFAULT 'DRAFT',
        source_language_code      VARCHAR(20)      NULL,
        target_language_code      VARCHAR(20)      NOT NULL
            CONSTRAINT DF_projects_target_language DEFAULT 'vi',
        current_transcript_version INT             NOT NULL
            CONSTRAINT DF_projects_transcript_version DEFAULT 1,
        settings_json             NVARCHAR(MAX)    NULL,
        created_at_utc            DATETIME2(3)     NOT NULL
            CONSTRAINT DF_projects_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc            DATETIME2(3)     NOT NULL
            CONSTRAINT DF_projects_updated_at DEFAULT SYSUTCDATETIME(),
        deleted_at_utc            DATETIME2(3)     NULL,
        row_version               ROWVERSION       NOT NULL,

        CONSTRAINT PK_projects PRIMARY KEY CLUSTERED (project_id),
        CONSTRAINT FK_projects_owner
            FOREIGN KEY (owner_user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT CK_projects_name_not_blank
            CHECK (LEN(LTRIM(RTRIM(project_name))) > 0),
        CONSTRAINT CK_projects_status_code
            CHECK (status_code IN
            (
                'DRAFT', 'UPLOADING', 'PROCESSING', 'WAITING_REVIEW',
                'COMPLETED', 'FAILED', 'ARCHIVED'
            )),
        CONSTRAINT CK_projects_transcript_version
            CHECK (current_transcript_version >= 1),
        CONSTRAINT CK_projects_settings_json
            CHECK (settings_json IS NULL OR ISJSON(settings_json) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.projects')
      AND name = N'IX_projects_owner_status'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_projects_owner_status
        ON dbo.projects (owner_user_id, status_code, updated_at_utc DESC)
        INCLUDE (project_name, target_language_code)
        WHERE deleted_at_utc IS NULL;
END;
GO

/* Available Vietnamese TTS voices. No provider credentials are stored here. */
IF OBJECT_ID(N'dbo.voices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.voices
    (
        voice_id               UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_voices_voice_id DEFAULT NEWSEQUENTIALID(),
        owner_user_id          UNIQUEIDENTIFIER NULL,
        provider_code          NVARCHAR(100)    NOT NULL,
        provider_voice_id      NVARCHAR(200)    NOT NULL,
        display_name           NVARCHAR(200)    NOT NULL,
        language_code          VARCHAR(20)      NOT NULL
            CONSTRAINT DF_voices_language_code DEFAULT 'vi-VN',
        voice_type             VARCHAR(20)      NOT NULL
            CONSTRAINT DF_voices_voice_type DEFAULT 'PRESET',
        gender_code            VARCHAR(20)      NOT NULL
            CONSTRAINT DF_voices_gender_code DEFAULT 'UNSPECIFIED',
        description            NVARCHAR(1000)   NULL,
        style_config_json      NVARCHAR(MAX)    NULL,
        usage_rights_note      NVARCHAR(1000)   NULL,
        consent_confirmed_at_utc DATETIME2(3)   NULL,
        is_active              BIT              NOT NULL
            CONSTRAINT DF_voices_is_active DEFAULT 1,
        created_at_utc         DATETIME2(3)     NOT NULL
            CONSTRAINT DF_voices_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc         DATETIME2(3)     NOT NULL
            CONSTRAINT DF_voices_updated_at DEFAULT SYSUTCDATETIME(),
        deleted_at_utc         DATETIME2(3)     NULL,
        row_version            ROWVERSION       NOT NULL,

        CONSTRAINT PK_voices PRIMARY KEY CLUSTERED (voice_id),
        CONSTRAINT FK_voices_owner
            FOREIGN KEY (owner_user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT CK_voices_type
            CHECK (voice_type IN ('PRESET', 'CUSTOM')),
        CONSTRAINT CK_voices_gender
            CHECK (gender_code IN ('MALE', 'FEMALE', 'NEUTRAL', 'UNSPECIFIED')),
        CONSTRAINT CK_voices_style_json
            CHECK (style_config_json IS NULL OR ISJSON(style_config_json) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.voices')
      AND name = N'UX_voices_provider_voice_active'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_voices_provider_voice_active
        ON dbo.voices (provider_code, provider_voice_id)
        WHERE deleted_at_utc IS NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.voices')
      AND name = N'IX_voices_language_active'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_voices_language_active
        ON dbo.voices (language_code, is_active)
        INCLUDE (display_name, provider_code, voice_type)
        WHERE deleted_at_utc IS NULL;
END;
GO

/* Original, intermediate, and generated files. object_key is app-global. */
IF OBJECT_ID(N'dbo.media_files', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.media_files
    (
        media_file_id       UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_media_files_id DEFAULT NEWSEQUENTIALID(),
        project_id          UNIQUEIDENTIFIER NOT NULL,
        uploaded_by_user_id UNIQUEIDENTIFIER NULL,
        parent_media_file_id UNIQUEIDENTIFIER NULL,
        file_role           VARCHAR(30)      NOT NULL,
        status_code         VARCHAR(20)      NOT NULL
            CONSTRAINT DF_media_files_status DEFAULT 'UPLOADING',
        storage_provider    VARCHAR(30)      NOT NULL,
        storage_container   NVARCHAR(200)    NULL,
        object_key          NVARCHAR(450)    NOT NULL,
        original_file_name  NVARCHAR(260)    NULL,
        content_type        NVARCHAR(127)    NULL,
        file_extension      VARCHAR(20)      NULL,
        size_bytes          BIGINT           NULL,
        duration_ms         BIGINT           NULL,
        checksum_sha256     CHAR(64)         NULL,
        metadata_json       NVARCHAR(MAX)    NULL,
        created_at_utc      DATETIME2(3)     NOT NULL
            CONSTRAINT DF_media_files_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc      DATETIME2(3)     NOT NULL
            CONSTRAINT DF_media_files_updated_at DEFAULT SYSUTCDATETIME(),
        deleted_at_utc      DATETIME2(3)     NULL,
        row_version         ROWVERSION       NOT NULL,

        CONSTRAINT PK_media_files PRIMARY KEY CLUSTERED (media_file_id),
        CONSTRAINT FK_media_files_project
            FOREIGN KEY (project_id) REFERENCES dbo.projects(project_id),
        CONSTRAINT FK_media_files_uploader
            FOREIGN KEY (uploaded_by_user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT FK_media_files_parent
            FOREIGN KEY (parent_media_file_id) REFERENCES dbo.media_files(media_file_id),
        CONSTRAINT CK_media_files_role
            CHECK (file_role IN
            (
                'ORIGINAL_VIDEO', 'EXTRACTED_AUDIO', 'CLEAN_VOICE',
                'BACKGROUND_AUDIO', 'SEGMENT_VOICE', 'SUBTITLE',
                'TRANSCRIPT', 'OUTPUT_VIDEO', 'THUMBNAIL', 'OTHER'
            )),
        CONSTRAINT CK_media_files_status
            CHECK (status_code IN ('UPLOADING', 'AVAILABLE', 'PROCESSING', 'FAILED', 'DELETED')),
        CONSTRAINT CK_media_files_size
            CHECK (size_bytes IS NULL OR size_bytes >= 0),
        CONSTRAINT CK_media_files_duration
            CHECK (duration_ms IS NULL OR duration_ms >= 0),
        CONSTRAINT CK_media_files_metadata_json
            CHECK (metadata_json IS NULL OR ISJSON(metadata_json) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.media_files')
      AND name = N'UX_media_files_object_key_active'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_media_files_object_key_active
        ON dbo.media_files (object_key)
        WHERE deleted_at_utc IS NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.media_files')
      AND name = N'IX_media_files_project_role'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_media_files_project_role
        ON dbo.media_files (project_id, file_role, status_code)
        INCLUDE (object_key, content_type, duration_ms, size_bytes)
        WHERE deleted_at_utc IS NULL;
END;
GO

/* One row represents a complete asynchronous video-processing pipeline. */
IF OBJECT_ID(N'dbo.jobs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.jobs
    (
        job_id               UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_jobs_job_id DEFAULT NEWSEQUENTIALID(),
        project_id           UNIQUEIDENTIFIER NOT NULL,
        input_media_file_id  UNIQUEIDENTIFIER NULL,
        output_media_file_id UNIQUEIDENTIFIER NULL,
        job_type             VARCHAR(30)      NOT NULL
            CONSTRAINT DF_jobs_job_type DEFAULT 'FULL_PIPELINE',
        status_code          VARCHAR(30)      NOT NULL
            CONSTRAINT DF_jobs_status DEFAULT 'UPLOADED',
        progress_percent     DECIMAL(5,2)     NOT NULL
            CONSTRAINT DF_jobs_progress DEFAULT 0,
        priority_no          TINYINT          NOT NULL
            CONSTRAINT DF_jobs_priority DEFAULT 5,
        attempt_count        INT              NOT NULL
            CONSTRAINT DF_jobs_attempt_count DEFAULT 0,
        max_attempts         INT              NOT NULL
            CONSTRAINT DF_jobs_max_attempts DEFAULT 3,
        idempotency_key      NVARCHAR(200)    NULL,
        locked_by_worker     NVARCHAR(200)    NULL,
        lock_expires_at_utc  DATETIME2(3)     NULL,
        next_retry_at_utc    DATETIME2(3)     NULL,
        error_code           NVARCHAR(100)    NULL,
        error_message        NVARCHAR(2000)   NULL,
        request_config_json  NVARCHAR(MAX)    NULL,
        result_json          NVARCHAR(MAX)    NULL,
        submitted_at_utc     DATETIME2(3)     NOT NULL
            CONSTRAINT DF_jobs_submitted_at DEFAULT SYSUTCDATETIME(),
        started_at_utc       DATETIME2(3)     NULL,
        completed_at_utc     DATETIME2(3)     NULL,
        updated_at_utc       DATETIME2(3)     NOT NULL
            CONSTRAINT DF_jobs_updated_at DEFAULT SYSUTCDATETIME(),
        row_version          ROWVERSION       NOT NULL,

        CONSTRAINT PK_jobs PRIMARY KEY CLUSTERED (job_id),
        CONSTRAINT FK_jobs_project
            FOREIGN KEY (project_id) REFERENCES dbo.projects(project_id),
        CONSTRAINT FK_jobs_input_media
            FOREIGN KEY (input_media_file_id) REFERENCES dbo.media_files(media_file_id),
        CONSTRAINT FK_jobs_output_media
            FOREIGN KEY (output_media_file_id) REFERENCES dbo.media_files(media_file_id),
        CONSTRAINT CK_jobs_type
            CHECK (job_type IN
            (
                'FULL_PIPELINE', 'EXTRACT_AUDIO', 'TRANSCRIBE', 'TRANSLATE',
                'GENERATE_VOICE', 'MIX_EXPORT'
            )),
        CONSTRAINT CK_jobs_status
            CHECK (status_code IN
            (
                'UPLOADED', 'QUEUED', 'EXTRACTING_AUDIO', 'TRANSCRIBING',
                'TRANSLATING', 'WAITING_REVIEW', 'GENERATING_VOICE',
                'MIXING', 'COMPLETED', 'FAILED', 'CANCELLED'
            )),
        CONSTRAINT CK_jobs_progress
            CHECK (progress_percent >= 0 AND progress_percent <= 100),
        CONSTRAINT CK_jobs_priority
            CHECK (priority_no BETWEEN 0 AND 9),
        CONSTRAINT CK_jobs_attempts
            CHECK (attempt_count >= 0 AND max_attempts >= 1),
        CONSTRAINT CK_jobs_request_json
            CHECK (request_config_json IS NULL OR ISJSON(request_config_json) = 1),
        CONSTRAINT CK_jobs_result_json
            CHECK (result_json IS NULL OR ISJSON(result_json) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.jobs')
      AND name = N'UX_jobs_project_idempotency'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_jobs_project_idempotency
        ON dbo.jobs (project_id, idempotency_key)
        WHERE idempotency_key IS NOT NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.jobs')
      AND name = N'IX_jobs_queue'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_jobs_queue
        ON dbo.jobs (status_code, priority_no DESC, next_retry_at_utc, submitted_at_utc)
        INCLUDE (project_id, job_type, attempt_count, max_attempts, lock_expires_at_utc);
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.jobs')
      AND name = N'IX_jobs_project_history'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_jobs_project_history
        ON dbo.jobs (project_id, submitted_at_utc DESC)
        INCLUDE (status_code, progress_percent, job_type, completed_at_utc);
END;
GO

/* Checkpoints and retries for each pipeline stage. */
IF OBJECT_ID(N'dbo.job_steps', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.job_steps
    (
        job_step_id             UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_job_steps_id DEFAULT NEWSEQUENTIALID(),
        job_id                  UNIQUEIDENTIFIER NOT NULL,
        step_order              TINYINT          NOT NULL,
        step_code               VARCHAR(30)      NOT NULL,
        status_code             VARCHAR(20)      NOT NULL
            CONSTRAINT DF_job_steps_status DEFAULT 'PENDING',
        progress_percent        DECIMAL(5,2)     NOT NULL
            CONSTRAINT DF_job_steps_progress DEFAULT 0,
        attempt_count           INT              NOT NULL
            CONSTRAINT DF_job_steps_attempt_count DEFAULT 0,
        max_attempts            INT              NOT NULL
            CONSTRAINT DF_job_steps_max_attempts DEFAULT 3,
        checkpoint_media_file_id UNIQUEIDENTIFIER NULL,
        input_json              NVARCHAR(MAX)    NULL,
        output_json             NVARCHAR(MAX)    NULL,
        error_code              NVARCHAR(100)    NULL,
        error_message           NVARCHAR(2000)   NULL,
        next_retry_at_utc       DATETIME2(3)     NULL,
        started_at_utc          DATETIME2(3)     NULL,
        completed_at_utc        DATETIME2(3)     NULL,
        created_at_utc          DATETIME2(3)     NOT NULL
            CONSTRAINT DF_job_steps_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc          DATETIME2(3)     NOT NULL
            CONSTRAINT DF_job_steps_updated_at DEFAULT SYSUTCDATETIME(),
        row_version             ROWVERSION       NOT NULL,

        CONSTRAINT PK_job_steps PRIMARY KEY CLUSTERED (job_step_id),
        CONSTRAINT FK_job_steps_job
            FOREIGN KEY (job_id) REFERENCES dbo.jobs(job_id) ON DELETE CASCADE,
        CONSTRAINT FK_job_steps_checkpoint_media
            FOREIGN KEY (checkpoint_media_file_id) REFERENCES dbo.media_files(media_file_id),
        CONSTRAINT UQ_job_steps_order UNIQUE (job_id, step_order),
        CONSTRAINT UQ_job_steps_code UNIQUE (job_id, step_code),
        CONSTRAINT CK_job_steps_code
            CHECK (step_code IN
            (
                'EXTRACT_AUDIO', 'TRANSCRIBE', 'TRANSLATE', 'REVIEW',
                'GENERATE_VOICE', 'SYNC_AUDIO', 'MIX_EXPORT'
            )),
        CONSTRAINT CK_job_steps_status
            CHECK (status_code IN
            (
                'PENDING', 'RUNNING', 'WAITING_REVIEW', 'COMPLETED',
                'FAILED', 'SKIPPED', 'CANCELLED'
            )),
        CONSTRAINT CK_job_steps_progress
            CHECK (progress_percent >= 0 AND progress_percent <= 100),
        CONSTRAINT CK_job_steps_attempts
            CHECK (attempt_count >= 0 AND max_attempts >= 1),
        CONSTRAINT CK_job_steps_input_json
            CHECK (input_json IS NULL OR ISJSON(input_json) = 1),
        CONSTRAINT CK_job_steps_output_json
            CHECK (output_json IS NULL OR ISJSON(output_json) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.job_steps')
      AND name = N'IX_job_steps_retry'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_job_steps_retry
        ON dbo.job_steps (status_code, next_retry_at_utc)
        INCLUDE (job_id, step_code, attempt_count, max_attempts);
END;
GO

/* Timestamped transcript, translation, review, and TTS data. */
IF OBJECT_ID(N'dbo.segments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.segments
    (
        segment_id             UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_segments_id DEFAULT NEWSEQUENTIALID(),
        project_id             UNIQUEIDENTIFIER NOT NULL,
        source_job_id          UNIQUEIDENTIFIER NULL,
        transcript_version     INT              NOT NULL
            CONSTRAINT DF_segments_transcript_version DEFAULT 1,
        segment_index          INT              NOT NULL,
        start_ms               BIGINT           NOT NULL,
        end_ms                 BIGINT           NOT NULL,
        speaker_label          NVARCHAR(100)    NOT NULL
            CONSTRAINT DF_segments_speaker DEFAULT N'speaker_1',
        source_language_code   VARCHAR(20)      NULL,
        original_text          NVARCHAR(MAX)    NOT NULL,
        translated_text        NVARCHAR(MAX)    NULL,
        source_confidence      DECIMAL(5,4)     NULL,
        translation_status     VARCHAR(20)      NOT NULL
            CONSTRAINT DF_segments_translation_status DEFAULT 'PENDING',
        original_text_locked   BIT              NOT NULL
            CONSTRAINT DF_segments_original_locked DEFAULT 0,
        translation_locked     BIT              NOT NULL
            CONSTRAINT DF_segments_translation_locked DEFAULT 0,
        voice_id               UNIQUEIDENTIFIER NULL,
        tts_media_file_id      UNIQUEIDENTIFIER NULL,
        speech_rate            DECIMAL(5,3)     NOT NULL
            CONSTRAINT DF_segments_speech_rate DEFAULT 1.000,
        tts_duration_ms        BIGINT           NULL,
        approved_by_user_id    UNIQUEIDENTIFIER NULL,
        approved_at_utc        DATETIME2(3)     NULL,
        created_at_utc         DATETIME2(3)     NOT NULL
            CONSTRAINT DF_segments_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc         DATETIME2(3)     NOT NULL
            CONSTRAINT DF_segments_updated_at DEFAULT SYSUTCDATETIME(),
        row_version            ROWVERSION       NOT NULL,

        CONSTRAINT PK_segments PRIMARY KEY CLUSTERED (segment_id),
        CONSTRAINT FK_segments_project
            FOREIGN KEY (project_id) REFERENCES dbo.projects(project_id),
        CONSTRAINT FK_segments_source_job
            FOREIGN KEY (source_job_id) REFERENCES dbo.jobs(job_id),
        CONSTRAINT FK_segments_voice
            FOREIGN KEY (voice_id) REFERENCES dbo.voices(voice_id),
        CONSTRAINT FK_segments_tts_media
            FOREIGN KEY (tts_media_file_id) REFERENCES dbo.media_files(media_file_id),
        CONSTRAINT FK_segments_approved_by
            FOREIGN KEY (approved_by_user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT UQ_segments_project_version_index
            UNIQUE (project_id, transcript_version, segment_index),
        CONSTRAINT CK_segments_version
            CHECK (transcript_version >= 1),
        CONSTRAINT CK_segments_index
            CHECK (segment_index >= 0),
        CONSTRAINT CK_segments_timestamps
            CHECK (start_ms >= 0 AND end_ms > start_ms),
        CONSTRAINT CK_segments_confidence
            CHECK (source_confidence IS NULL OR source_confidence BETWEEN 0 AND 1),
        CONSTRAINT CK_segments_translation_status
            CHECK (translation_status IN ('PENDING', 'DRAFT', 'APPROVED', 'REJECTED')),
        CONSTRAINT CK_segments_speech_rate
            CHECK (speech_rate BETWEEN 0.500 AND 2.000),
        CONSTRAINT CK_segments_tts_duration
            CHECK (tts_duration_ms IS NULL OR tts_duration_ms >= 0)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.segments')
      AND name = N'IX_segments_timeline'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_segments_timeline
        ON dbo.segments (project_id, transcript_version, start_ms, end_ms)
        INCLUDE (segment_index, speaker_label, translation_status, voice_id);
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.segments')
      AND name = N'IX_segments_review_queue'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_segments_review_queue
        ON dbo.segments (project_id, transcript_version, translation_status)
        INCLUDE (segment_index, start_ms, end_ms);
END;
GO

/* Project-specific names and terminology used by the translation stage. */
IF OBJECT_ID(N'dbo.glossary_entries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.glossary_entries
    (
        glossary_entry_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_glossary_entries_id DEFAULT NEWSEQUENTIALID(),
        project_id        UNIQUEIDENTIFIER NOT NULL,
        source_term       NVARCHAR(300)    NOT NULL,
        translated_term   NVARCHAR(500)    NOT NULL,
        note              NVARCHAR(1000)   NULL,
        is_case_sensitive BIT              NOT NULL
            CONSTRAINT DF_glossary_case_sensitive DEFAULT 0,
        is_active         BIT              NOT NULL
            CONSTRAINT DF_glossary_is_active DEFAULT 1,
        created_at_utc    DATETIME2(3)     NOT NULL
            CONSTRAINT DF_glossary_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc    DATETIME2(3)     NOT NULL
            CONSTRAINT DF_glossary_updated_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_glossary_entries PRIMARY KEY CLUSTERED (glossary_entry_id),
        CONSTRAINT FK_glossary_entries_project
            FOREIGN KEY (project_id) REFERENCES dbo.projects(project_id),
        CONSTRAINT UQ_glossary_entries_project_term
            UNIQUE (project_id, source_term),
        CONSTRAINT CK_glossary_source_not_blank
            CHECK (LEN(LTRIM(RTRIM(source_term))) > 0),
        CONSTRAINT CK_glossary_translation_not_blank
            CHECK (LEN(LTRIM(RTRIM(translated_term))) > 0)
    );
END;
GO

/* Provider usage and estimated cost per operation. */
IF OBJECT_ID(N'dbo.usage_records', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.usage_records
    (
        usage_record_id    UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_usage_records_id DEFAULT NEWSEQUENTIALID(),
        user_id            UNIQUEIDENTIFIER NOT NULL,
        project_id         UNIQUEIDENTIFIER NULL,
        job_id             UNIQUEIDENTIFIER NULL,
        provider_code      NVARCHAR(100)    NOT NULL,
        operation_code     VARCHAR(30)      NOT NULL,
        quantity           DECIMAL(18,6)    NOT NULL,
        unit_code          VARCHAR(20)      NOT NULL,
        unit_cost          DECIMAL(19,8)    NULL,
        total_cost         DECIMAL(19,6)    NULL,
        currency_code      CHAR(3)          NOT NULL
            CONSTRAINT DF_usage_records_currency DEFAULT 'USD',
        external_request_id NVARCHAR(200)   NULL,
        metadata_json      NVARCHAR(MAX)    NULL,
        occurred_at_utc    DATETIME2(3)     NOT NULL
            CONSTRAINT DF_usage_records_occurred_at DEFAULT SYSUTCDATETIME(),
        created_at_utc     DATETIME2(3)     NOT NULL
            CONSTRAINT DF_usage_records_created_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_usage_records PRIMARY KEY CLUSTERED (usage_record_id),
        CONSTRAINT FK_usage_records_user
            FOREIGN KEY (user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT FK_usage_records_project
            FOREIGN KEY (project_id) REFERENCES dbo.projects(project_id),
        CONSTRAINT FK_usage_records_job
            FOREIGN KEY (job_id) REFERENCES dbo.jobs(job_id),
        CONSTRAINT CK_usage_records_operation
            CHECK (operation_code IN
            (
                'STORAGE', 'TRANSCRIPTION', 'TRANSLATION', 'TTS',
                'MEDIA_PROCESSING', 'EGRESS', 'OTHER'
            )),
        CONSTRAINT CK_usage_records_unit
            CHECK (unit_code IN
            (
                'MINUTE', 'SECOND', 'CHARACTER', 'TOKEN',
                'BYTE', 'REQUEST', 'FLAT'
            )),
        CONSTRAINT CK_usage_records_quantity
            CHECK (quantity >= 0),
        CONSTRAINT CK_usage_records_cost
            CHECK
            (
                (unit_cost IS NULL OR unit_cost >= 0)
                AND (total_cost IS NULL OR total_cost >= 0)
            ),
        CONSTRAINT CK_usage_records_metadata_json
            CHECK (metadata_json IS NULL OR ISJSON(metadata_json) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.usage_records')
      AND name = N'UX_usage_records_provider_request'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_usage_records_provider_request
        ON dbo.usage_records (provider_code, external_request_id)
        WHERE external_request_id IS NOT NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.usage_records')
      AND name = N'IX_usage_records_user_period'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_usage_records_user_period
        ON dbo.usage_records (user_id, occurred_at_utc DESC)
        INCLUDE (project_id, operation_code, quantity, unit_code, total_cost, currency_code);
END;
GO

/* Operational timeline for progress display, retries, and diagnostics. */
IF OBJECT_ID(N'dbo.job_events', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.job_events
    (
        job_event_id     BIGINT           IDENTITY(1,1) NOT NULL,
        job_id           UNIQUEIDENTIFIER NOT NULL,
        job_step_id      UNIQUEIDENTIFIER NULL,
        event_type       VARCHAR(30)      NOT NULL,
        level_code       VARCHAR(10)      NOT NULL
            CONSTRAINT DF_job_events_level DEFAULT 'INFO',
        progress_percent DECIMAL(5,2)     NULL,
        message          NVARCHAR(2000)   NOT NULL,
        event_data_json  NVARCHAR(MAX)    NULL,
        created_at_utc   DATETIME2(3)     NOT NULL
            CONSTRAINT DF_job_events_created_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_job_events PRIMARY KEY CLUSTERED (job_event_id),
        CONSTRAINT FK_job_events_job
            FOREIGN KEY (job_id) REFERENCES dbo.jobs(job_id),
        CONSTRAINT FK_job_events_step
            FOREIGN KEY (job_step_id) REFERENCES dbo.job_steps(job_step_id),
        CONSTRAINT CK_job_events_type
            CHECK (event_type IN
            (
                'STATUS_CHANGE', 'PROGRESS', 'RETRY', 'ERROR',
                'INFO', 'CHECKPOINT'
            )),
        CONSTRAINT CK_job_events_level
            CHECK (level_code IN ('DEBUG', 'INFO', 'WARNING', 'ERROR')),
        CONSTRAINT CK_job_events_progress
            CHECK
            (
                progress_percent IS NULL
                OR (progress_percent >= 0 AND progress_percent <= 100)
            ),
        CONSTRAINT CK_job_events_data_json
            CHECK (event_data_json IS NULL OR ISJSON(event_data_json) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.job_events')
      AND name = N'IX_job_events_job_timeline'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_job_events_job_timeline
        ON dbo.job_events (job_id, created_at_utc DESC, job_event_id DESC)
        INCLUDE (job_step_id, event_type, level_code, progress_percent, message);
END;
GO

/* Mark V1 only after all database objects above have been created. */
IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 1)
BEGIN
    INSERT INTO dbo.schema_versions (version_no, version_name)
    VALUES (1, N'Initial video translation and dubbing schema');
END;
GO

PRINT N'TOOL_VIETSUB database schema V1 deployed successfully.';
GO

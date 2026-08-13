/*
    TOOL_VIETSUB - Authentication, plans, and account schema V2
    Target: Microsoft SQL Server 2019+

    This deployment is additive and idempotent. It does not modify or delete
    V1 video-processing data.
*/

USE [TOOL_VIETSUB];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.service_plans', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.service_plans
    (
        plan_id                 UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_service_plans_id DEFAULT NEWSEQUENTIALID(),
        plan_code               VARCHAR(30)      NOT NULL,
        display_name            NVARCHAR(100)    NOT NULL,
        description             NVARCHAR(500)    NULL,
        monthly_quota_minutes   DECIMAL(12,2)    NULL,
        max_video_minutes       DECIMAL(12,2)    NULL,
        features_json           NVARCHAR(MAX)    NOT NULL,
        is_active               BIT              NOT NULL
            CONSTRAINT DF_service_plans_is_active DEFAULT 1,
        created_at_utc          DATETIME2(3)     NOT NULL
            CONSTRAINT DF_service_plans_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc          DATETIME2(3)     NOT NULL
            CONSTRAINT DF_service_plans_updated_at DEFAULT SYSUTCDATETIME(),
        row_version             ROWVERSION       NOT NULL,

        CONSTRAINT PK_service_plans PRIMARY KEY CLUSTERED (plan_id),
        CONSTRAINT UQ_service_plans_code UNIQUE (plan_code),
        CONSTRAINT CK_service_plans_code_not_blank
            CHECK (LEN(LTRIM(RTRIM(plan_code))) > 0),
        CONSTRAINT CK_service_plans_display_name_not_blank
            CHECK (LEN(LTRIM(RTRIM(display_name))) > 0),
        CONSTRAINT CK_service_plans_monthly_quota
            CHECK (monthly_quota_minutes IS NULL OR monthly_quota_minutes >= 0),
        CONSTRAINT CK_service_plans_max_video
            CHECK (max_video_minutes IS NULL OR max_video_minutes >= 0),
        CONSTRAINT CK_service_plans_features_json
            CHECK (ISJSON(features_json) = 1)
    );
END;
GO

IF OBJECT_ID(N'dbo.user_subscriptions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.user_subscriptions
    (
        subscription_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_user_subscriptions_id DEFAULT NEWSEQUENTIALID(),
        user_id          UNIQUEIDENTIFIER NOT NULL,
        plan_id          UNIQUEIDENTIFIER NOT NULL,
        status_code      VARCHAR(20)      NOT NULL
            CONSTRAINT DF_user_subscriptions_status DEFAULT 'ACTIVE',
        starts_at_utc    DATETIME2(3)     NOT NULL
            CONSTRAINT DF_user_subscriptions_starts_at DEFAULT SYSUTCDATETIME(),
        ends_at_utc      DATETIME2(3)     NULL,
        created_at_utc   DATETIME2(3)     NOT NULL
            CONSTRAINT DF_user_subscriptions_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc   DATETIME2(3)     NOT NULL
            CONSTRAINT DF_user_subscriptions_updated_at DEFAULT SYSUTCDATETIME(),
        row_version      ROWVERSION       NOT NULL,

        CONSTRAINT PK_user_subscriptions PRIMARY KEY CLUSTERED (subscription_id),
        CONSTRAINT FK_user_subscriptions_user
            FOREIGN KEY (user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT FK_user_subscriptions_plan
            FOREIGN KEY (plan_id) REFERENCES dbo.service_plans(plan_id),
        CONSTRAINT CK_user_subscriptions_status
            CHECK (status_code IN ('ACTIVE', 'EXPIRED', 'CANCELLED', 'SUSPENDED')),
        CONSTRAINT CK_user_subscriptions_period
            CHECK (ends_at_utc IS NULL OR ends_at_utc > starts_at_utc)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.user_subscriptions')
      AND name = N'IX_user_subscriptions_current'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_user_subscriptions_current
        ON dbo.user_subscriptions (user_id, status_code, starts_at_utc DESC)
        INCLUDE (plan_id, ends_at_utc, updated_at_utc);
END;
GO

IF OBJECT_ID(N'dbo.auth_sessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.auth_sessions
    (
        session_id              UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_auth_sessions_id DEFAULT NEWSEQUENTIALID(),
        user_id                 UNIQUEIDENTIFIER NOT NULL,
        refresh_token_hash      BINARY(32)       NOT NULL,
        device_id               NVARCHAR(200)    NOT NULL,
        device_name             NVARCHAR(200)    NULL,
        app_version             NVARCHAR(50)     NULL,
        ip_address              VARCHAR(45)      NULL,
        created_at_utc          DATETIME2(3)     NOT NULL
            CONSTRAINT DF_auth_sessions_created_at DEFAULT SYSUTCDATETIME(),
        last_seen_at_utc        DATETIME2(3)     NOT NULL
            CONSTRAINT DF_auth_sessions_last_seen DEFAULT SYSUTCDATETIME(),
        expires_at_utc          DATETIME2(3)     NOT NULL,
        revoked_at_utc          DATETIME2(3)     NULL,
        revoke_reason           NVARCHAR(200)    NULL,
        replaced_by_session_id  UNIQUEIDENTIFIER NULL,

        CONSTRAINT PK_auth_sessions PRIMARY KEY CLUSTERED (session_id),
        CONSTRAINT FK_auth_sessions_user
            FOREIGN KEY (user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT FK_auth_sessions_replacement
            FOREIGN KEY (replaced_by_session_id) REFERENCES dbo.auth_sessions(session_id),
        CONSTRAINT CK_auth_sessions_device_not_blank
            CHECK (LEN(LTRIM(RTRIM(device_id))) > 0),
        CONSTRAINT CK_auth_sessions_expiry
            CHECK (expires_at_utc > created_at_utc)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.auth_sessions')
      AND name = N'UX_auth_sessions_refresh_hash'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_auth_sessions_refresh_hash
        ON dbo.auth_sessions (refresh_token_hash);
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.auth_sessions')
      AND name = N'IX_auth_sessions_user_active'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_auth_sessions_user_active
        ON dbo.auth_sessions (user_id, expires_at_utc DESC)
        INCLUDE (device_id, last_seen_at_utc, revoked_at_utc);
END;
GO

IF OBJECT_ID(N'dbo.security_audit_logs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.security_audit_logs
    (
        audit_log_id   BIGINT IDENTITY(1,1) NOT NULL,
        user_id        UNIQUEIDENTIFIER NULL,
        event_code     VARCHAR(50)      NOT NULL,
        outcome_code   VARCHAR(20)      NOT NULL,
        ip_address     VARCHAR(45)      NULL,
        device_id      NVARCHAR(200)    NULL,
        details_json   NVARCHAR(MAX)    NULL,
        created_at_utc DATETIME2(3)     NOT NULL
            CONSTRAINT DF_security_audit_logs_created_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_security_audit_logs PRIMARY KEY CLUSTERED (audit_log_id),
        CONSTRAINT FK_security_audit_logs_user
            FOREIGN KEY (user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT CK_security_audit_logs_event_not_blank
            CHECK (LEN(LTRIM(RTRIM(event_code))) > 0),
        CONSTRAINT CK_security_audit_logs_outcome
            CHECK (outcome_code IN ('SUCCESS', 'FAILED', 'DENIED')),
        CONSTRAINT CK_security_audit_logs_details_json
            CHECK (details_json IS NULL OR ISJSON(details_json) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.security_audit_logs')
      AND name = N'IX_security_audit_logs_user_timeline'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_security_audit_logs_user_timeline
        ON dbo.security_audit_logs (user_id, created_at_utc DESC)
        INCLUDE (event_code, outcome_code, device_id);
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.service_plans WHERE plan_code = 'FREE')
BEGIN
    INSERT INTO dbo.service_plans
    (
        plan_code,
        display_name,
        description,
        monthly_quota_minutes,
        max_video_minutes,
        features_json
    )
    VALUES
    (
        'FREE',
        N'Miễn phí',
        N'Gói khởi đầu dành cho xử lý video ngắn.',
        60,
        20,
        N'["subtitle.transcribe","subtitle.translate","video.export"]'
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.service_plans WHERE plan_code = 'PRO')
BEGIN
    INSERT INTO dbo.service_plans
    (
        plan_code,
        display_name,
        description,
        monthly_quota_minutes,
        max_video_minutes,
        features_json
    )
    VALUES
    (
        'PRO',
        N'Chuyên nghiệp',
        N'Đầy đủ công cụ dịch, tạo giọng và xử lý hàng loạt.',
        1200,
        120,
        N'["subtitle.transcribe","subtitle.translate","voice.generate","ocr.detect","video.export","batch.process"]'
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 2)
BEGIN
    INSERT INTO dbo.schema_versions (version_no, version_name)
    VALUES (2, N'Authentication, service plans, subscriptions, and security audit');
END;
GO

PRINT N'TOOL_VIETSUB authentication schema V2 deployed successfully.';
GO

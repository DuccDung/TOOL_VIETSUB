/*
    SubVid - Cloud API credential and token quota V6
    Target: Microsoft SQL Server 2019+

    Additive and idempotent. The Server stores encrypted provider credentials;
    prompts, media and provider responses remain outside the Server.
*/

USE [TOOL_VIETSUB];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.schema_versions', N'U') IS NULL
BEGIN
    THROW 51000, 'Deploy SubVid_V1.sql before V6.', 1;
END;
GO

IF OBJECT_ID(N'dbo.cloud_provider_credentials', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.cloud_provider_credentials
    (
        credential_id      UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_cloud_credentials_id DEFAULT NEWSEQUENTIALID(),
        provider_code      VARCHAR(30)      NOT NULL,
        display_name       NVARCHAR(120)    NOT NULL,
        encrypted_api_key  NVARCHAR(MAX)    NOT NULL,
        key_fingerprint    VARCHAR(64)      NOT NULL,
        key_suffix         VARCHAR(12)      NOT NULL,
        assigned_user_id   UNIQUEIDENTIFIER NULL,
        status_code        VARCHAR(20)      NOT NULL
            CONSTRAINT DF_cloud_credentials_status DEFAULT 'ACTIVE',
        priority           INT              NOT NULL
            CONSTRAINT DF_cloud_credentials_priority DEFAULT 100,
        last_issued_at_utc DATETIME2(3)     NULL,
        created_at_utc     DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_credentials_created DEFAULT SYSUTCDATETIME(),
        updated_at_utc     DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_credentials_updated DEFAULT SYSUTCDATETIME(),
        row_version        ROWVERSION       NOT NULL,

        CONSTRAINT PK_cloud_provider_credentials PRIMARY KEY CLUSTERED (credential_id),
        CONSTRAINT FK_cloud_credentials_assigned_user FOREIGN KEY (assigned_user_id)
            REFERENCES dbo.users(user_id) ON DELETE SET NULL,
        CONSTRAINT UQ_cloud_credentials_provider_fingerprint
            UNIQUE (provider_code, key_fingerprint),
        CONSTRAINT CK_cloud_credentials_provider
            CHECK (provider_code IN ('openai', 'gemini', 'deepseek', 'groq')),
        CONSTRAINT CK_cloud_credentials_status
            CHECK (status_code IN ('ACTIVE', 'DISABLED')),
        CONSTRAINT CK_cloud_credentials_priority
            CHECK (priority BETWEEN 0 AND 10000),
        CONSTRAINT CK_cloud_credentials_name
            CHECK (LEN(LTRIM(RTRIM(display_name))) > 0)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.cloud_provider_credentials')
      AND name = N'IX_cloud_credentials_provider_active'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_cloud_credentials_provider_active
        ON dbo.cloud_provider_credentials
        (provider_code, status_code, priority, assigned_user_id, last_issued_at_utc)
        INCLUDE (display_name, key_suffix);
END;
GO

IF OBJECT_ID(N'dbo.cloud_quota_limits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.cloud_quota_limits
    (
        user_id            UNIQUEIDENTIFIER NOT NULL,
        unit_code          VARCHAR(30)      NOT NULL,
        monthly_limit      DECIMAL(20,0)    NOT NULL,
        updated_at_utc     DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_quota_limits_updated DEFAULT SYSUTCDATETIME(),
        updated_by_user_id UNIQUEIDENTIFIER NULL,
        row_version        ROWVERSION       NOT NULL,

        CONSTRAINT PK_cloud_quota_limits PRIMARY KEY CLUSTERED (user_id, unit_code),
        CONSTRAINT FK_cloud_quota_limits_user FOREIGN KEY (user_id)
            REFERENCES dbo.users(user_id) ON DELETE CASCADE,
        CONSTRAINT CK_cloud_quota_limits_unit
            CHECK (unit_code IN ('LLM_TOKEN', 'TTS_CHARACTER', 'STT_SECOND')),
        CONSTRAINT CK_cloud_quota_limits_value
            CHECK (monthly_limit BETWEEN 0 AND 1000000000000)
    );
END;
GO

IF OBJECT_ID(N'dbo.cloud_usage_reservations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.cloud_usage_reservations
    (
        reservation_id         UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_cloud_reservations_id DEFAULT NEWSEQUENTIALID(),
        request_id             UNIQUEIDENTIFIER NOT NULL,
        user_id                UNIQUEIDENTIFIER NOT NULL,
        project_id             UNIQUEIDENTIFIER NULL,
        local_job_id           UNIQUEIDENTIFIER NULL,
        credential_id          UNIQUEIDENTIFIER NOT NULL,
        operation_code         VARCHAR(40)      NOT NULL,
        provider_code          VARCHAR(30)      NOT NULL,
        model_id               NVARCHAR(160)    NOT NULL,
        unit_code              VARCHAR(30)      NOT NULL,
        status_code            VARCHAR(20)      NOT NULL
            CONSTRAINT DF_cloud_reservations_status DEFAULT 'HELD',
        estimated_input_units  DECIMAL(20,0)    NOT NULL,
        estimated_output_units DECIMAL(20,0)    NOT NULL,
        reserved_units         DECIMAL(20,0)    NOT NULL,
        committed_units        DECIMAL(20,0)    NULL,
        provider_request_id    NVARCHAR(200)    NULL,
        quota_period_start_utc DATETIME2(3)     NOT NULL,
        expires_at_utc         DATETIME2(3)     NOT NULL,
        created_at_utc         DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_reservations_created DEFAULT SYSUTCDATETIME(),
        updated_at_utc         DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_reservations_updated DEFAULT SYSUTCDATETIME(),
        committed_at_utc       DATETIME2(3)     NULL,
        released_at_utc        DATETIME2(3)     NULL,
        row_version            ROWVERSION       NOT NULL,

        CONSTRAINT PK_cloud_usage_reservations PRIMARY KEY CLUSTERED (reservation_id),
        CONSTRAINT FK_cloud_reservations_user FOREIGN KEY (user_id)
            REFERENCES dbo.users(user_id),
        CONSTRAINT FK_cloud_reservations_credential FOREIGN KEY (credential_id)
            REFERENCES dbo.cloud_provider_credentials(credential_id),
        CONSTRAINT UQ_cloud_reservations_user_request UNIQUE (user_id, request_id),
        CONSTRAINT CK_cloud_reservations_status
            CHECK (status_code IN ('HELD', 'COMMITTED', 'RELEASED', 'EXPIRED')),
        CONSTRAINT CK_cloud_reservations_units
            CHECK (estimated_input_units >= 0 AND estimated_output_units >= 0
                AND reserved_units > 0 AND (committed_units IS NULL OR committed_units > 0)),
        CONSTRAINT CK_cloud_reservations_expiry
            CHECK (expires_at_utc > created_at_utc)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.cloud_usage_reservations')
      AND name = N'IX_cloud_reservations_user_period'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_cloud_reservations_user_period
        ON dbo.cloud_usage_reservations
        (user_id, quota_period_start_utc, unit_code, status_code, expires_at_utc)
        INCLUDE (reserved_units, committed_units, provider_code, credential_id);
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.cloud_usage_reservations')
      AND name = N'IX_cloud_reservations_expiry'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_cloud_reservations_expiry
        ON dbo.cloud_usage_reservations (status_code, expires_at_utc)
        INCLUDE (user_id, reserved_units)
        WHERE status_code = 'HELD';
END;
GO

IF OBJECT_ID(N'dbo.cloud_usage_ledger', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.cloud_usage_ledger
    (
        ledger_id              UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_cloud_usage_ledger_id DEFAULT NEWSEQUENTIALID(),
        reservation_id         UNIQUEIDENTIFIER NOT NULL,
        user_id                UNIQUEIDENTIFIER NOT NULL,
        credential_id          UNIQUEIDENTIFIER NOT NULL,
        provider_code          VARCHAR(30)      NOT NULL,
        model_id               NVARCHAR(160)    NOT NULL,
        operation_code         VARCHAR(40)      NOT NULL,
        unit_code              VARCHAR(30)      NOT NULL,
        input_units            DECIMAL(20,0)    NOT NULL,
        output_units           DECIMAL(20,0)    NOT NULL,
        cached_input_units     DECIMAL(20,0)    NOT NULL,
        total_units            DECIMAL(20,0)    NOT NULL,
        api_request_count      INT              NOT NULL,
        retry_request_count    INT              NOT NULL,
        provider_request_id    NVARCHAR(200)    NULL,
        quota_period_start_utc DATETIME2(3)     NOT NULL,
        occurred_at_utc        DATETIME2(3)     NOT NULL,
        created_at_utc         DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_usage_ledger_created DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_cloud_usage_ledger PRIMARY KEY CLUSTERED (ledger_id),
        CONSTRAINT FK_cloud_usage_ledger_reservation FOREIGN KEY (reservation_id)
            REFERENCES dbo.cloud_usage_reservations(reservation_id),
        CONSTRAINT UQ_cloud_usage_ledger_reservation UNIQUE (reservation_id),
        CONSTRAINT CK_cloud_usage_ledger_units
            CHECK (input_units >= 0 AND output_units >= 0 AND cached_input_units >= 0
                AND total_units > 0 AND cached_input_units <= input_units),
        CONSTRAINT CK_cloud_usage_ledger_requests
            CHECK (api_request_count >= 0 AND retry_request_count >= 0)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.cloud_usage_ledger')
      AND name = N'IX_cloud_usage_ledger_user_period'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_cloud_usage_ledger_user_period
        ON dbo.cloud_usage_ledger
        (user_id, quota_period_start_utc, unit_code, occurred_at_utc DESC)
        INCLUDE (total_units, input_units, output_units, provider_code, model_id);
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 6)
BEGIN
    INSERT INTO dbo.schema_versions (version_no, version_name)
    VALUES (6, N'Lightweight Cloud API credential and usage quota control');
END;
GO

PRINT N'SubVid Cloud quota schema V6 deployed successfully.';
GO

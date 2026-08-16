/*
    SubVid - Schema V4
    Quota reservations for safe desktop processing.

    This script is additive and idempotent. Run it after V1, V2 and V3.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.schema_versions', N'U') IS NULL
BEGIN
    THROW 51000, 'Deploy SubVid_V1.sql before V4.', 1;
END;
GO

IF OBJECT_ID(N'dbo.usage_reservations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.usage_reservations
    (
        reservation_id        UNIQUEIDENTIFIER NOT NULL,
        user_id               UNIQUEIDENTIFIER NOT NULL,
        project_id            UNIQUEIDENTIFIER NULL,
        local_job_id          UNIQUEIDENTIFIER NULL,
        feature_code          VARCHAR(60)      NOT NULL,
        status_code           VARCHAR(20)      NOT NULL,
        estimated_minutes     DECIMAL(12,4)    NOT NULL,
        committed_minutes     DECIMAL(12,4)    NULL,
        idempotency_key       NVARCHAR(100)    NOT NULL,
        quota_period_start_utc DATETIME2(3)    NOT NULL,
        expires_at_utc        DATETIME2(3)     NOT NULL,
        created_at_utc        DATETIME2(3)     NOT NULL
            CONSTRAINT DF_usage_reservations_created DEFAULT SYSUTCDATETIME(),
        updated_at_utc        DATETIME2(3)     NOT NULL
            CONSTRAINT DF_usage_reservations_updated DEFAULT SYSUTCDATETIME(),
        committed_at_utc      DATETIME2(3)     NULL,
        released_at_utc       DATETIME2(3)     NULL,
        row_version           ROWVERSION       NOT NULL,

        CONSTRAINT PK_usage_reservations PRIMARY KEY CLUSTERED (reservation_id),
        CONSTRAINT FK_usage_reservations_user
            FOREIGN KEY (user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT FK_usage_reservations_project
            FOREIGN KEY (project_id) REFERENCES dbo.projects(project_id),
        CONSTRAINT UQ_usage_reservations_user_key
            UNIQUE (user_id, idempotency_key),
        CONSTRAINT CK_usage_reservations_status
            CHECK (status_code IN ('HELD', 'COMMITTED', 'RELEASED', 'EXPIRED')),
        CONSTRAINT CK_usage_reservations_estimated
            CHECK (estimated_minutes > 0),
        CONSTRAINT CK_usage_reservations_committed
            CHECK (committed_minutes IS NULL OR committed_minutes > 0),
        CONSTRAINT CK_usage_reservations_feature
            CHECK (LEN(LTRIM(RTRIM(feature_code))) > 0),
        CONSTRAINT CK_usage_reservations_expiry
            CHECK (expires_at_utc > created_at_utc)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.usage_reservations')
      AND name = N'IX_usage_reservations_active_user_period'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_usage_reservations_active_user_period
        ON dbo.usage_reservations
        (
            user_id,
            quota_period_start_utc,
            status_code,
            expires_at_utc
        )
        INCLUDE (estimated_minutes, project_id, local_job_id);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.usage_reservations')
      AND name = N'IX_usage_reservations_expiry'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_usage_reservations_expiry
        ON dbo.usage_reservations (status_code, expires_at_utc)
        INCLUDE (user_id, estimated_minutes)
        WHERE status_code = 'HELD';
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 4)
BEGIN
    INSERT INTO dbo.schema_versions (version_no, version_name)
    VALUES (4, N'Idempotent quota reservations for desktop processing');
END;
GO

PRINT N'SubVid quota schema V4 deployed successfully.';
GO

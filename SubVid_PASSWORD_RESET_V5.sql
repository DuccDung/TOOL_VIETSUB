/*
    SubVid - Web account password reset and cookie invalidation V5
    Target: Microsoft SQL Server 2019+

    Additive and idempotent. OTP values and passwords are never stored as plain text.
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

IF COL_LENGTH(N'dbo.users', N'password_changed_at_utc') IS NULL
BEGIN
    ALTER TABLE dbo.users ADD password_changed_at_utc DATETIME2(3) NULL;
END;
GO

UPDATE dbo.users
SET password_changed_at_utc = COALESCE(updated_at_utc, created_at_utc)
WHERE password_changed_at_utc IS NULL;
GO

IF OBJECT_ID(N'dbo.password_reset_challenges', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.password_reset_challenges
    (
        challenge_id       UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_password_reset_challenges_id DEFAULT NEWSEQUENTIALID(),
        user_id            UNIQUEIDENTIFIER NULL,
        email              NVARCHAR(320)    NOT NULL,
        email_normalized   AS UPPER(LTRIM(RTRIM(email))) PERSISTED,
        otp_hash           BINARY(32)       NOT NULL,
        status_code        VARCHAR(20)      NOT NULL
            CONSTRAINT DF_password_reset_challenges_status DEFAULT 'PENDING',
        attempt_count      INT              NOT NULL
            CONSTRAINT DF_password_reset_challenges_attempts DEFAULT 0,
        resend_count       INT              NOT NULL
            CONSTRAINT DF_password_reset_challenges_resends DEFAULT 0,
        device_id          NVARCHAR(200)    NOT NULL,
        ip_address         VARCHAR(45)      NULL,
        expires_at_utc     DATETIME2(3)     NOT NULL,
        resend_at_utc      DATETIME2(3)     NOT NULL,
        verified_at_utc    DATETIME2(3)     NULL,
        created_at_utc     DATETIME2(3)     NOT NULL
            CONSTRAINT DF_password_reset_challenges_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc     DATETIME2(3)     NOT NULL
            CONSTRAINT DF_password_reset_challenges_updated_at DEFAULT SYSUTCDATETIME(),
        row_version        ROWVERSION       NOT NULL,

        CONSTRAINT PK_password_reset_challenges PRIMARY KEY CLUSTERED (challenge_id),
        CONSTRAINT FK_password_reset_challenges_user FOREIGN KEY (user_id)
            REFERENCES dbo.users (user_id) ON DELETE SET NULL,
        CONSTRAINT CK_password_reset_challenges_email CHECK (LEN(LTRIM(RTRIM(email))) > 0),
        CONSTRAINT CK_password_reset_challenges_device CHECK (LEN(LTRIM(RTRIM(device_id))) >= 8),
        CONSTRAINT CK_password_reset_challenges_status
            CHECK (status_code IN ('PENDING', 'VERIFIED', 'EXPIRED', 'LOCKED', 'CANCELLED')),
        CONSTRAINT CK_password_reset_challenges_attempts
            CHECK (attempt_count >= 0 AND resend_count >= 0),
        CONSTRAINT CK_password_reset_challenges_expiry CHECK (expires_at_utc > created_at_utc),
        CONSTRAINT CK_password_reset_challenges_resend_time CHECK (resend_at_utc >= created_at_utc)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.password_reset_challenges')
      AND name = N'UX_password_reset_challenges_pending_email'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_password_reset_challenges_pending_email
        ON dbo.password_reset_challenges (email_normalized)
        WHERE status_code = 'PENDING';
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.password_reset_challenges')
      AND name = N'IX_password_reset_challenges_expiry'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_password_reset_challenges_expiry
        ON dbo.password_reset_challenges (status_code, expires_at_utc)
        INCLUDE (user_id, email, device_id, attempt_count, resend_count, updated_at_utc);
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 5)
BEGIN
    INSERT INTO dbo.schema_versions (version_no, version_name)
    VALUES (5, N'Web account password reset and secure cookie invalidation');
END;
GO

PRINT N'SubVid password-reset schema V5 deployed successfully.';
GO

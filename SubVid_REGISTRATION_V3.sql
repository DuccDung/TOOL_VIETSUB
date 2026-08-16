/*
    SubVid - Registration and email OTP schema V3
    Target: Microsoft SQL Server 2019+

    This deployment is additive and idempotent. Passwords and OTP values are
    never stored as plain text.
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

IF OBJECT_ID(N'dbo.registration_challenges', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.registration_challenges
    (
        challenge_id        UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_registration_challenges_id DEFAULT NEWSEQUENTIALID(),
        email               NVARCHAR(320)    NOT NULL,
        email_normalized    AS UPPER(LTRIM(RTRIM(email))) PERSISTED,
        display_name        NVARCHAR(200)    NOT NULL,
        password_hash       NVARCHAR(1000)   NOT NULL,
        otp_hash            BINARY(32)       NOT NULL,
        status_code         VARCHAR(20)      NOT NULL
            CONSTRAINT DF_registration_challenges_status DEFAULT 'PENDING',
        attempt_count       INT              NOT NULL
            CONSTRAINT DF_registration_challenges_attempts DEFAULT 0,
        resend_count        INT              NOT NULL
            CONSTRAINT DF_registration_challenges_resends DEFAULT 0,
        device_id           NVARCHAR(200)    NOT NULL,
        device_name         NVARCHAR(200)    NULL,
        app_version         NVARCHAR(50)     NULL,
        ip_address          VARCHAR(45)      NULL,
        expires_at_utc      DATETIME2(3)     NOT NULL,
        resend_at_utc       DATETIME2(3)     NOT NULL,
        verified_at_utc     DATETIME2(3)     NULL,
        created_at_utc      DATETIME2(3)     NOT NULL
            CONSTRAINT DF_registration_challenges_created_at DEFAULT SYSUTCDATETIME(),
        updated_at_utc      DATETIME2(3)     NOT NULL
            CONSTRAINT DF_registration_challenges_updated_at DEFAULT SYSUTCDATETIME(),
        row_version         ROWVERSION       NOT NULL,

        CONSTRAINT PK_registration_challenges PRIMARY KEY CLUSTERED (challenge_id),
        CONSTRAINT CK_registration_challenges_email
            CHECK (LEN(LTRIM(RTRIM(email))) > 0),
        CONSTRAINT CK_registration_challenges_display_name
            CHECK (LEN(LTRIM(RTRIM(display_name))) > 0),
        CONSTRAINT CK_registration_challenges_device
            CHECK (LEN(LTRIM(RTRIM(device_id))) > 0),
        CONSTRAINT CK_registration_challenges_status
            CHECK (status_code IN ('PENDING', 'VERIFIED', 'EXPIRED', 'LOCKED', 'CANCELLED')),
        CONSTRAINT CK_registration_challenges_attempts
            CHECK (attempt_count >= 0 AND resend_count >= 0),
        CONSTRAINT CK_registration_challenges_expiry
            CHECK (expires_at_utc > created_at_utc),
        CONSTRAINT CK_registration_challenges_resend_time
            CHECK (resend_at_utc >= created_at_utc)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.registration_challenges')
      AND name = N'UX_registration_challenges_pending_email'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_registration_challenges_pending_email
        ON dbo.registration_challenges (email_normalized)
        WHERE status_code = 'PENDING';
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.registration_challenges')
      AND name = N'IX_registration_challenges_expiry'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_registration_challenges_expiry
        ON dbo.registration_challenges (status_code, expires_at_utc)
        INCLUDE (email, device_id, attempt_count, resend_count, updated_at_utc);
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 3)
BEGIN
    INSERT INTO dbo.schema_versions (version_no, version_name)
    VALUES (3, N'User registration and email OTP verification');
END;
GO

PRINT N'SubVid registration schema V3 deployed successfully.';
GO

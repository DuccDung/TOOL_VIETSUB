/*
    SubVid - Cloud key allocation, plan pools and plan policies V7
    Target: Microsoft SQL Server 2019+

    Additive and idempotent. Existing dedicated keys remain dedicated. Existing
    global keys are moved to legacy shared pools so deployment does not interrupt
    Cloud access. New keys default to UNASSIGNED and cannot be issued.
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
    OR OBJECT_ID(N'dbo.cloud_provider_credentials', N'U') IS NULL
    OR OBJECT_ID(N'dbo.service_plans', N'U') IS NULL
BEGIN
    THROW 51000, 'Deploy SubVid_AUTH_V2.sql and SubVid_CLOUD_QUOTA_V6.sql before V7.', 1;
END;
GO

IF OBJECT_ID(N'dbo.cloud_key_pools', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.cloud_key_pools
    (
        pool_id         UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_cloud_key_pools_id DEFAULT NEWSEQUENTIALID(),
        pool_code       VARCHAR(60)      NOT NULL,
        display_name    NVARCHAR(120)    NOT NULL,
        provider_code   VARCHAR(30)      NOT NULL,
        status_code     VARCHAR(20)      NOT NULL
            CONSTRAINT DF_cloud_key_pools_status DEFAULT 'ACTIVE',
        is_legacy       BIT              NOT NULL
            CONSTRAINT DF_cloud_key_pools_legacy DEFAULT 0,
        created_at_utc  DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_key_pools_created DEFAULT SYSUTCDATETIME(),
        updated_at_utc  DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_key_pools_updated DEFAULT SYSUTCDATETIME(),
        row_version     ROWVERSION       NOT NULL,

        CONSTRAINT PK_cloud_key_pools PRIMARY KEY CLUSTERED (pool_id),
        CONSTRAINT UQ_cloud_key_pools_code UNIQUE (pool_code),
        CONSTRAINT CK_cloud_key_pools_provider
            CHECK (provider_code IN ('openai', 'gemini', 'deepseek', 'groq')),
        CONSTRAINT CK_cloud_key_pools_status
            CHECK (status_code IN ('ACTIVE', 'DISABLED')),
        CONSTRAINT CK_cloud_key_pools_name
            CHECK (LEN(LTRIM(RTRIM(display_name))) > 0)
    );
END;
GO

IF COL_LENGTH(N'dbo.service_plans', N'price_amount') IS NULL
BEGIN
    ALTER TABLE dbo.service_plans ADD price_amount DECIMAL(18,2) NOT NULL
        CONSTRAINT DF_service_plans_price DEFAULT 0;
END;
GO

IF COL_LENGTH(N'dbo.service_plans', N'currency_code') IS NULL
BEGIN
    ALTER TABLE dbo.service_plans ADD currency_code VARCHAR(3) NOT NULL
        CONSTRAINT DF_service_plans_currency DEFAULT 'VND';
END;
GO

IF COL_LENGTH(N'dbo.service_plans', N'billing_period_days') IS NULL
BEGIN
    ALTER TABLE dbo.service_plans ADD billing_period_days INT NOT NULL
        CONSTRAINT DF_service_plans_billing_days DEFAULT 30;
END;
GO

IF COL_LENGTH(N'dbo.service_plans', N'is_public') IS NULL
BEGIN
    ALTER TABLE dbo.service_plans ADD is_public BIT NOT NULL
        CONSTRAINT DF_service_plans_public DEFAULT 1;
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.service_plans')
      AND name = N'CK_service_plans_commercial_options'
)
BEGIN
    ALTER TABLE dbo.service_plans WITH CHECK
        ADD CONSTRAINT CK_service_plans_commercial_options
            CHECK (price_amount >= 0 AND billing_period_days BETWEEN 1 AND 3650
                AND LEN(currency_code) = 3);
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.cloud_key_pools')
      AND name = N'IX_cloud_key_pools_provider_status'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_cloud_key_pools_provider_status
        ON dbo.cloud_key_pools (provider_code, status_code, pool_code)
        INCLUDE (display_name, is_legacy);
END;
GO

IF COL_LENGTH(N'dbo.cloud_provider_credentials', N'allocation_mode') IS NULL
BEGIN
    ALTER TABLE dbo.cloud_provider_credentials ADD allocation_mode VARCHAR(20) NOT NULL
        CONSTRAINT DF_cloud_credentials_allocation_mode DEFAULT 'UNASSIGNED';
END;
GO

IF COL_LENGTH(N'dbo.cloud_provider_credentials', N'pool_id') IS NULL
    ALTER TABLE dbo.cloud_provider_credentials ADD pool_id UNIQUEIDENTIFIER NULL;
GO

IF COL_LENGTH(N'dbo.cloud_provider_credentials', N'allocation_plan_id') IS NULL
    ALTER TABLE dbo.cloud_provider_credentials ADD allocation_plan_id UNIQUEIDENTIFIER NULL;
GO

IF COL_LENGTH(N'dbo.cloud_provider_credentials', N'allocation_source_code') IS NULL
    ALTER TABLE dbo.cloud_provider_credentials ADD allocation_source_code VARCHAR(20) NULL;
GO

IF COL_LENGTH(N'dbo.cloud_provider_credentials', N'allocated_at_utc') IS NULL
    ALTER TABLE dbo.cloud_provider_credentials ADD allocated_at_utc DATETIME2(3) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_cloud_credentials_pool')
BEGIN
    ALTER TABLE dbo.cloud_provider_credentials WITH CHECK
        ADD CONSTRAINT FK_cloud_credentials_pool FOREIGN KEY (pool_id)
            REFERENCES dbo.cloud_key_pools(pool_id);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_cloud_credentials_allocation_plan')
BEGIN
    ALTER TABLE dbo.cloud_provider_credentials WITH CHECK
        ADD CONSTRAINT FK_cloud_credentials_allocation_plan FOREIGN KEY (allocation_plan_id)
            REFERENCES dbo.service_plans(plan_id);
END;
GO

IF OBJECT_ID(N'dbo.cloud_key_pool_plans', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.cloud_key_pool_plans
    (
        pool_id        UNIQUEIDENTIFIER NOT NULL,
        plan_id        UNIQUEIDENTIFIER NOT NULL,
        created_at_utc DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_key_pool_plans_created DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_cloud_key_pool_plans PRIMARY KEY CLUSTERED (pool_id, plan_id),
        CONSTRAINT FK_cloud_key_pool_plans_pool FOREIGN KEY (pool_id)
            REFERENCES dbo.cloud_key_pools(pool_id) ON DELETE CASCADE,
        CONSTRAINT FK_cloud_key_pool_plans_plan FOREIGN KEY (plan_id)
            REFERENCES dbo.service_plans(plan_id) ON DELETE CASCADE
    );
END;
GO

IF OBJECT_ID(N'dbo.service_plan_cloud_policies', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.service_plan_cloud_policies
    (
        plan_id               UNIQUEIDENTIFIER NOT NULL,
        provider_code         VARCHAR(30)      NOT NULL,
        allocation_mode       VARCHAR(20)      NOT NULL
            CONSTRAINT DF_service_plan_cloud_policy_mode DEFAULT 'SHARED',
        monthly_token_limit   DECIMAL(20,0)    NOT NULL,
        allowed_models_json   NVARCHAR(MAX)    NOT NULL
            CONSTRAINT DF_service_plan_cloud_policy_models DEFAULT N'["*"]',
        allow_shared_fallback BIT              NOT NULL
            CONSTRAINT DF_service_plan_cloud_policy_fallback DEFAULT 0,
        is_active             BIT              NOT NULL
            CONSTRAINT DF_service_plan_cloud_policy_active DEFAULT 1,
        created_at_utc        DATETIME2(3)     NOT NULL
            CONSTRAINT DF_service_plan_cloud_policy_created DEFAULT SYSUTCDATETIME(),
        updated_at_utc        DATETIME2(3)     NOT NULL
            CONSTRAINT DF_service_plan_cloud_policy_updated DEFAULT SYSUTCDATETIME(),
        row_version           ROWVERSION       NOT NULL,

        CONSTRAINT PK_service_plan_cloud_policies PRIMARY KEY CLUSTERED (plan_id, provider_code),
        CONSTRAINT FK_service_plan_cloud_policy_plan FOREIGN KEY (plan_id)
            REFERENCES dbo.service_plans(plan_id) ON DELETE CASCADE,
        CONSTRAINT CK_service_plan_cloud_policy_provider
            CHECK (provider_code IN ('openai', 'gemini', 'deepseek', 'groq')),
        CONSTRAINT CK_service_plan_cloud_policy_mode
            CHECK (allocation_mode IN ('SHARED', 'DEDICATED')),
        CONSTRAINT CK_service_plan_cloud_policy_tokens
            CHECK (monthly_token_limit BETWEEN 0 AND 1000000000000),
        CONSTRAINT CK_service_plan_cloud_policy_models
            CHECK (ISJSON(allowed_models_json) = 1)
    );
END;
GO

IF OBJECT_ID(N'dbo.cloud_credential_allocation_history', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.cloud_credential_allocation_history
    (
        allocation_history_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_cloud_allocation_history_id DEFAULT NEWSEQUENTIALID(),
        credential_id          UNIQUEIDENTIFIER NOT NULL,
        event_code             VARCHAR(30)      NOT NULL,
        allocation_mode        VARCHAR(20)      NOT NULL,
        pool_id                UNIQUEIDENTIFIER NULL,
        assigned_user_id       UNIQUEIDENTIFIER NULL,
        plan_id                UNIQUEIDENTIFIER NULL,
        source_code            VARCHAR(20)      NULL,
        actor_user_id          UNIQUEIDENTIFIER NULL,
        reason                 NVARCHAR(240)    NULL,
        created_at_utc         DATETIME2(3)     NOT NULL
            CONSTRAINT DF_cloud_allocation_history_created DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_cloud_credential_allocation_history
            PRIMARY KEY CLUSTERED (allocation_history_id),
        CONSTRAINT FK_cloud_allocation_history_credential FOREIGN KEY (credential_id)
            REFERENCES dbo.cloud_provider_credentials(credential_id),
        CONSTRAINT FK_cloud_allocation_history_pool FOREIGN KEY (pool_id)
            REFERENCES dbo.cloud_key_pools(pool_id),
        CONSTRAINT FK_cloud_allocation_history_user FOREIGN KEY (assigned_user_id)
            REFERENCES dbo.users(user_id),
        CONSTRAINT FK_cloud_allocation_history_plan FOREIGN KEY (plan_id)
            REFERENCES dbo.service_plans(plan_id),
        CONSTRAINT CK_cloud_allocation_history_event
            CHECK (event_code IN ('ASSIGNED', 'RELEASED', 'MOVED', 'MIGRATED')),
        CONSTRAINT CK_cloud_allocation_history_mode
            CHECK (allocation_mode IN ('UNASSIGNED', 'SHARED', 'DEDICATED'))
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.cloud_credential_allocation_history')
      AND name = N'IX_cloud_allocation_history_credential'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_cloud_allocation_history_credential
        ON dbo.cloud_credential_allocation_history (credential_id, created_at_utc DESC)
        INCLUDE (event_code, allocation_mode, pool_id, assigned_user_id, plan_id);
END;
GO

INSERT INTO dbo.cloud_key_pools (pool_code, display_name, provider_code, status_code, is_legacy)
SELECT seed.pool_code, seed.display_name, seed.provider_code, 'ACTIVE', seed.is_legacy
FROM (VALUES
    ('LEGACY_OPENAI',   N'Dùng chung hiện tại · OpenAI',   'openai',   CAST(1 AS BIT)),
    ('LEGACY_GEMINI',   N'Dùng chung hiện tại · Gemini',   'gemini',   CAST(1 AS BIT)),
    ('LEGACY_DEEPSEEK', N'Dùng chung hiện tại · DeepSeek', 'deepseek', CAST(1 AS BIT)),
    ('LEGACY_GROQ',     N'Dùng chung hiện tại · Groq',     'groq',     CAST(1 AS BIT)),
    ('FREE_OPENAI',     N'FREE · OpenAI',                   'openai',   CAST(0 AS BIT)),
    ('FREE_GEMINI',     N'FREE · Gemini',                   'gemini',   CAST(0 AS BIT)),
    ('FREE_DEEPSEEK',   N'FREE · DeepSeek',                 'deepseek', CAST(0 AS BIT)),
    ('FREE_GROQ',       N'FREE · Groq',                     'groq',     CAST(0 AS BIT)),
    ('PRO_OPENAI',      N'Chuyên nghiệp · OpenAI',          'openai',   CAST(0 AS BIT)),
    ('PRO_GEMINI',      N'Chuyên nghiệp · Gemini',          'gemini',   CAST(0 AS BIT)),
    ('PRO_DEEPSEEK',    N'Chuyên nghiệp · DeepSeek',        'deepseek', CAST(0 AS BIT)),
    ('PRO_GROQ',        N'Chuyên nghiệp · Groq',            'groq',     CAST(0 AS BIT))
) seed(pool_code, display_name, provider_code, is_legacy)
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.cloud_key_pools existing WHERE existing.pool_code = seed.pool_code
);
GO

INSERT INTO dbo.cloud_key_pool_plans (pool_id, plan_id)
SELECT pool.pool_id, service_plan.plan_id
FROM (VALUES
    ('LEGACY_OPENAI', 'FREE'), ('LEGACY_OPENAI', 'PRO'),
    ('LEGACY_GEMINI', 'FREE'), ('LEGACY_GEMINI', 'PRO'),
    ('LEGACY_DEEPSEEK', 'FREE'), ('LEGACY_DEEPSEEK', 'PRO'),
    ('LEGACY_GROQ', 'FREE'), ('LEGACY_GROQ', 'PRO'),
    ('FREE_OPENAI', 'FREE'), ('FREE_GEMINI', 'FREE'),
    ('FREE_DEEPSEEK', 'FREE'), ('FREE_GROQ', 'FREE'),
    ('PRO_OPENAI', 'PRO'), ('PRO_GEMINI', 'PRO'),
    ('PRO_DEEPSEEK', 'PRO'), ('PRO_GROQ', 'PRO')
) map(pool_code, plan_code)
JOIN dbo.cloud_key_pools pool ON pool.pool_code = map.pool_code
JOIN dbo.service_plans service_plan ON service_plan.plan_code = map.plan_code
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.cloud_key_pool_plans existing
    WHERE existing.pool_id = pool.pool_id AND existing.plan_id = service_plan.plan_id
);
GO

INSERT INTO dbo.service_plan_cloud_policies
(
    plan_id, provider_code, allocation_mode, monthly_token_limit,
    allowed_models_json, allow_shared_fallback, is_active
)
SELECT service_plan.plan_id, provider.provider_code, 'SHARED',
    CASE service_plan.plan_code WHEN 'FREE' THEN 100000 ELSE 2000000 END,
    N'["*"]', 0, 1
FROM dbo.service_plans service_plan
CROSS JOIN (VALUES ('openai'), ('gemini'), ('deepseek'), ('groq')) provider(provider_code)
WHERE service_plan.plan_code IN ('FREE', 'PRO')
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.service_plan_cloud_policies existing
      WHERE existing.plan_id = service_plan.plan_id
        AND existing.provider_code = provider.provider_code
  );
GO

IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 7)
BEGIN
    UPDATE credential
    SET allocation_mode = 'DEDICATED',
        pool_id = NULL,
        allocation_plan_id = NULL,
        allocation_source_code = 'MIGRATION',
        allocated_at_utc = COALESCE(credential.updated_at_utc, SYSUTCDATETIME())
    FROM dbo.cloud_provider_credentials credential
    WHERE credential.assigned_user_id IS NOT NULL;

    UPDATE credential
    SET allocation_mode = 'SHARED',
        pool_id = pool.pool_id,
        allocation_plan_id = NULL,
        allocation_source_code = 'MIGRATION',
        allocated_at_utc = COALESCE(credential.updated_at_utc, SYSUTCDATETIME())
    FROM dbo.cloud_provider_credentials credential
    JOIN dbo.cloud_key_pools pool
      ON pool.pool_code = CONCAT('LEGACY_', UPPER(credential.provider_code))
    WHERE credential.assigned_user_id IS NULL;

    INSERT INTO dbo.cloud_credential_allocation_history
    (
        credential_id, event_code, allocation_mode, pool_id,
        assigned_user_id, plan_id, source_code, reason
    )
    SELECT credential_id, 'MIGRATED', allocation_mode, pool_id,
        assigned_user_id, allocation_plan_id, allocation_source_code,
        N'Chuyển đổi dữ liệu phân bổ key hiện có sang schema V7.'
    FROM dbo.cloud_provider_credentials;
END;
GO

IF EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.cloud_provider_credentials')
      AND name = N'CK_cloud_credentials_status'
)
BEGIN
    ALTER TABLE dbo.cloud_provider_credentials DROP CONSTRAINT CK_cloud_credentials_status;
END;
GO

ALTER TABLE dbo.cloud_provider_credentials WITH CHECK
    ADD CONSTRAINT CK_cloud_credentials_status
        CHECK (status_code IN ('ACTIVE', 'DISABLED', 'ERROR'));
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.cloud_provider_credentials')
      AND name = N'CK_cloud_credentials_allocation_mode'
)
BEGIN
    ALTER TABLE dbo.cloud_provider_credentials WITH CHECK
        ADD CONSTRAINT CK_cloud_credentials_allocation_mode
            CHECK (allocation_mode IN ('UNASSIGNED', 'SHARED', 'DEDICATED'));
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.cloud_provider_credentials')
      AND name = N'CK_cloud_credentials_allocation_target'
)
BEGIN
    ALTER TABLE dbo.cloud_provider_credentials WITH CHECK
        ADD CONSTRAINT CK_cloud_credentials_allocation_target CHECK
        (
            (allocation_mode = 'UNASSIGNED'
                AND assigned_user_id IS NULL AND pool_id IS NULL
                AND allocation_plan_id IS NULL AND allocation_source_code IS NULL
                AND allocated_at_utc IS NULL)
            OR
            (allocation_mode = 'SHARED'
                AND assigned_user_id IS NULL AND pool_id IS NOT NULL
                AND allocation_plan_id IS NULL AND allocation_source_code IS NOT NULL
                AND allocated_at_utc IS NOT NULL)
            OR
            (allocation_mode = 'DEDICATED'
                AND assigned_user_id IS NOT NULL AND pool_id IS NULL
                AND allocation_source_code IS NOT NULL AND allocated_at_utc IS NOT NULL)
        );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.cloud_provider_credentials')
      AND name = N'CK_cloud_credentials_allocation_source'
)
BEGIN
    ALTER TABLE dbo.cloud_provider_credentials WITH CHECK
        ADD CONSTRAINT CK_cloud_credentials_allocation_source
            CHECK (allocation_source_code IS NULL OR allocation_source_code IN ('ADMIN', 'PLAN', 'MIGRATION'));
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.cloud_provider_credentials')
      AND name = N'IX_cloud_credentials_allocation'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_cloud_credentials_allocation
        ON dbo.cloud_provider_credentials
        (provider_code, status_code, allocation_mode, pool_id, assigned_user_id, priority, last_issued_at_utc)
        INCLUDE (display_name, key_suffix, allocation_plan_id, allocation_source_code);
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 7)
BEGIN
    INSERT INTO dbo.schema_versions (version_no, version_name)
    VALUES (7, N'Cloud key pools, explicit allocation modes, and service plan Cloud policies');
END;
GO

PRINT N'SubVid Cloud pool and plan policy schema V7 deployed successfully.';
GO

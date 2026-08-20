/*
    SubVid - Purchase orders and idempotent payment webhook ledger V8
    Target: Microsoft SQL Server 2019+

    Additive and idempotent. This migration does not modify existing users,
    subscriptions, plans, credentials, quotas, or Cloud usage records.
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
    OR OBJECT_ID(N'dbo.users', N'U') IS NULL
    OR OBJECT_ID(N'dbo.service_plans', N'U') IS NULL
    OR OBJECT_ID(N'dbo.user_subscriptions', N'U') IS NULL
    OR OBJECT_ID(N'dbo.cloud_provider_credentials', N'U') IS NULL
BEGIN
    THROW 51000, 'Deploy SubVid V1 through V7 before purchase schema V8.', 1;
END;
GO

IF OBJECT_ID(N'dbo.purchase_orders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.purchase_orders
    (
        order_id                   UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_purchase_orders_id DEFAULT NEWSEQUENTIALID(),
        order_number               VARCHAR(64)      NOT NULL,
        user_id                    UNIQUEIDENTIFIER NOT NULL,
        plan_id                    UNIQUEIDENTIFIER NOT NULL,
        status_code                VARCHAR(20)      NOT NULL
            CONSTRAINT DF_purchase_orders_status DEFAULT 'PENDING',
        payment_provider_code      VARCHAR(30)      NOT NULL,
        external_payment_id        VARCHAR(120)     NULL,
        idempotency_key            VARCHAR(100)     NOT NULL,
        price_amount               DECIMAL(18,2)    NOT NULL,
        currency_code              VARCHAR(3)       NOT NULL,
        billing_period_days        INT              NOT NULL,
        plan_code_snapshot         VARCHAR(30)      NOT NULL,
        plan_name_snapshot         NVARCHAR(100)    NOT NULL,
        source_code                VARCHAR(30)      NOT NULL,
        test_run_id                VARCHAR(50)      NULL,
        created_by_admin_id        UNIQUEIDENTIFIER NULL,
        fake_credential_id         UNIQUEIDENTIFIER NULL,
        activated_subscription_id  UNIQUEIDENTIFIER NULL,
        created_at_utc             DATETIME2(3)     NOT NULL
            CONSTRAINT DF_purchase_orders_created DEFAULT SYSUTCDATETIME(),
        updated_at_utc             DATETIME2(3)     NOT NULL
            CONSTRAINT DF_purchase_orders_updated DEFAULT SYSUTCDATETIME(),
        paid_at_utc                DATETIME2(3)     NULL,
        failed_at_utc              DATETIME2(3)     NULL,
        row_version                ROWVERSION       NOT NULL,

        CONSTRAINT PK_purchase_orders PRIMARY KEY CLUSTERED (order_id),
        CONSTRAINT FK_purchase_orders_user
            FOREIGN KEY (user_id) REFERENCES dbo.users(user_id),
        CONSTRAINT FK_purchase_orders_plan
            FOREIGN KEY (plan_id) REFERENCES dbo.service_plans(plan_id),
        CONSTRAINT FK_purchase_orders_admin
            FOREIGN KEY (created_by_admin_id) REFERENCES dbo.users(user_id),
        CONSTRAINT FK_purchase_orders_fake_credential
            FOREIGN KEY (fake_credential_id) REFERENCES dbo.cloud_provider_credentials(credential_id),
        CONSTRAINT FK_purchase_orders_subscription
            FOREIGN KEY (activated_subscription_id) REFERENCES dbo.user_subscriptions(subscription_id),
        CONSTRAINT CK_purchase_orders_status
            CHECK (status_code IN ('PENDING', 'PAID', 'FAILED', 'CANCELLED')),
        CONSTRAINT CK_purchase_orders_price
            CHECK (price_amount >= 0),
        CONSTRAINT CK_purchase_orders_currency
            CHECK (LEN(currency_code) = 3 AND currency_code = UPPER(currency_code)),
        CONSTRAINT CK_purchase_orders_billing_days
            CHECK (billing_period_days BETWEEN 1 AND 3650),
        CONSTRAINT CK_purchase_orders_paid_state
            CHECK
            (
                (status_code = 'PAID' AND paid_at_utc IS NOT NULL
                    AND external_payment_id IS NOT NULL
                    AND activated_subscription_id IS NOT NULL)
                OR
                (status_code <> 'PAID')
            ),
        CONSTRAINT CK_purchase_orders_test_source
            CHECK
            (
                test_run_id IS NULL
                OR
                (source_code = 'ADMIN_E2E'
                    AND payment_provider_code = 'FAKE_ADMIN'
                    AND test_run_id LIKE 'E2E[_]PURCHASE[_]%')
            )
    );

    CREATE UNIQUE INDEX UQ_purchase_orders_number
        ON dbo.purchase_orders(order_number);
    CREATE UNIQUE INDEX UQ_purchase_orders_idempotency
        ON dbo.purchase_orders(idempotency_key);
    CREATE UNIQUE INDEX UQ_purchase_orders_external_payment
        ON dbo.purchase_orders(external_payment_id)
        WHERE external_payment_id IS NOT NULL;
    CREATE UNIQUE INDEX UQ_purchase_orders_test_run
        ON dbo.purchase_orders(test_run_id)
        WHERE test_run_id IS NOT NULL;
    CREATE NONCLUSTERED INDEX IX_purchase_orders_user_timeline
        ON dbo.purchase_orders(user_id, created_at_utc DESC)
        INCLUDE (order_number, status_code, plan_code_snapshot, price_amount, currency_code, paid_at_utc);
    CREATE NONCLUSTERED INDEX IX_purchase_orders_status
        ON dbo.purchase_orders(status_code, created_at_utc DESC)
        INCLUDE (order_number, user_id, plan_id, payment_provider_code);
END;
GO

IF OBJECT_ID(N'dbo.payment_webhook_events', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.payment_webhook_events
    (
        event_id          UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_payment_webhook_events_id DEFAULT NEWSEQUENTIALID(),
        order_id          UNIQUEIDENTIFIER NOT NULL,
        provider_code     VARCHAR(30)      NOT NULL,
        external_event_id VARCHAR(120)     NOT NULL,
        event_code        VARCHAR(40)      NOT NULL,
        payload_sha256    VARCHAR(64)      NOT NULL,
        result_code       VARCHAR(20)      NOT NULL,
        received_at_utc   DATETIME2(3)     NOT NULL
            CONSTRAINT DF_payment_webhook_events_received DEFAULT SYSUTCDATETIME(),
        processed_at_utc  DATETIME2(3)     NULL,

        CONSTRAINT PK_payment_webhook_events PRIMARY KEY CLUSTERED (event_id),
        CONSTRAINT FK_payment_webhook_events_order
            FOREIGN KEY (order_id) REFERENCES dbo.purchase_orders(order_id),
        CONSTRAINT UQ_payment_webhook_events_external
            UNIQUE (provider_code, external_event_id),
        CONSTRAINT CK_payment_webhook_events_result
            CHECK (result_code IN ('PROCESSED', 'IGNORED', 'FAILED')),
        CONSTRAINT CK_payment_webhook_events_hash
            CHECK (LEN(payload_sha256) = 64)
    );

    CREATE NONCLUSTERED INDEX IX_payment_webhook_events_order
        ON dbo.payment_webhook_events(order_id, received_at_utc DESC)
        INCLUDE (provider_code, event_code, result_code, processed_at_utc);
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 8)
BEGIN
    INSERT INTO dbo.schema_versions (version_no, version_name)
    VALUES (8, N'Purchase orders and idempotent payment webhook ledger');
END;
GO

PRINT N'SubVid purchase schema V8 deployed successfully.';
GO

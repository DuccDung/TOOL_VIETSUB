/*
    SubVid - SePay checkout transactions and reconciliation ledger V9
    Target: Microsoft SQL Server 2019+

    Additive and idempotent. Deploy V1 through V8 first.
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
    OR OBJECT_ID(N'dbo.purchase_orders', N'U') IS NULL
    OR OBJECT_ID(N'dbo.payment_webhook_events', N'U') IS NULL
BEGIN
    THROW 51000, 'Deploy SubVid V1 through V8 before SePay schema V9.', 1;
END;
GO

IF OBJECT_ID(N'dbo.purchase_payment_transactions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.purchase_payment_transactions
    (
        payment_transaction_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_purchase_payment_transactions_id DEFAULT NEWSEQUENTIALID(),
        order_id                UNIQUEIDENTIFIER NOT NULL,
        provider_code           VARCHAR(30)      NOT NULL
            CONSTRAINT DF_purchase_payment_transactions_provider DEFAULT 'SEPAY',
        status_code             VARCHAR(20)      NOT NULL
            CONSTRAINT DF_purchase_payment_transactions_status DEFAULT 'PENDING',
        transaction_code        VARCHAR(64)      NOT NULL,
        provider_transaction_id VARCHAR(120)     NULL,
        bank_code               VARCHAR(50)      NOT NULL,
        receiver_bank_name      NVARCHAR(255)    NOT NULL,
        receiver_account_number VARCHAR(80)      NOT NULL,
        receiver_account_name   NVARCHAR(255)    NOT NULL,
        qr_url                   NVARCHAR(2000)   NOT NULL,
        transfer_content         NVARCHAR(255)    NOT NULL,
        expected_amount          DECIMAL(18,2)    NOT NULL,
        paid_amount              DECIMAL(18,2)    NULL,
        expires_at_utc           DATETIME2(3)     NOT NULL,
        paid_at_utc              DATETIME2(3)     NULL,
        created_at_utc           DATETIME2(3)     NOT NULL
            CONSTRAINT DF_purchase_payment_transactions_created DEFAULT SYSUTCDATETIME(),
        updated_at_utc           DATETIME2(3)     NOT NULL
            CONSTRAINT DF_purchase_payment_transactions_updated DEFAULT SYSUTCDATETIME(),
        provider_response_json   NVARCHAR(MAX)    NULL,
        row_version              ROWVERSION       NOT NULL,

        CONSTRAINT PK_purchase_payment_transactions PRIMARY KEY CLUSTERED (payment_transaction_id),
        CONSTRAINT FK_purchase_payment_transactions_order
            FOREIGN KEY (order_id) REFERENCES dbo.purchase_orders(order_id),
        CONSTRAINT CK_purchase_payment_transactions_status
            CHECK (status_code IN ('PENDING', 'PROCESSING', 'PAID', 'EXPIRED', 'FAILED', 'CANCELLED', 'REFUNDED')),
        CONSTRAINT CK_purchase_payment_transactions_amount
            CHECK (expected_amount >= 0 AND (paid_amount IS NULL OR paid_amount >= 0))
    );

    CREATE UNIQUE INDEX UQ_purchase_payment_transactions_code
        ON dbo.purchase_payment_transactions(transaction_code);
    CREATE UNIQUE INDEX UQ_purchase_payment_transactions_provider_tx
        ON dbo.purchase_payment_transactions(provider_transaction_id)
        WHERE provider_transaction_id IS NOT NULL;
    CREATE NONCLUSTERED INDEX IX_purchase_payment_transactions_order_status
        ON dbo.purchase_payment_transactions(order_id, status_code)
        INCLUDE (transaction_code, expected_amount, expires_at_utc, paid_at_utc);
END;
GO

IF OBJECT_ID(N'dbo.CK_purchase_orders_status', N'C') IS NOT NULL
    ALTER TABLE dbo.purchase_orders DROP CONSTRAINT CK_purchase_orders_status;
GO

ALTER TABLE dbo.purchase_orders WITH CHECK ADD CONSTRAINT CK_purchase_orders_status
    CHECK (status_code IN ('PENDING', 'PAID', 'FAILED', 'CANCELLED', 'REFUNDED'));
GO

IF COL_LENGTH(N'dbo.payment_webhook_events', N'payment_transaction_id') IS NULL
    ALTER TABLE dbo.payment_webhook_events ADD payment_transaction_id UNIQUEIDENTIFIER NULL;
GO

IF COL_LENGTH(N'dbo.payment_webhook_events', N'transfer_content') IS NULL
    ALTER TABLE dbo.payment_webhook_events ADD transfer_content NVARCHAR(1000) NULL;
GO

IF COL_LENGTH(N'dbo.payment_webhook_events', N'transfer_amount') IS NULL
    ALTER TABLE dbo.payment_webhook_events ADD transfer_amount DECIMAL(18,2) NULL;
GO

IF COL_LENGTH(N'dbo.payment_webhook_events', N'raw_payload') IS NULL
    ALTER TABLE dbo.payment_webhook_events ADD raw_payload NVARCHAR(MAX) NULL;
GO

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.payment_webhook_events')
      AND name = N'order_id'
      AND is_nullable = 0
)
    ALTER TABLE dbo.payment_webhook_events ALTER COLUMN order_id UNIQUEIDENTIFIER NULL;
GO

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.payment_webhook_events')
      AND name = N'result_code'
      AND max_length < 30
)
    ALTER TABLE dbo.payment_webhook_events ALTER COLUMN result_code VARCHAR(30) NOT NULL;
GO

IF OBJECT_ID(N'dbo.CK_payment_webhook_events_result', N'C') IS NOT NULL
    ALTER TABLE dbo.payment_webhook_events DROP CONSTRAINT CK_payment_webhook_events_result;
GO

ALTER TABLE dbo.payment_webhook_events WITH CHECK ADD CONSTRAINT CK_payment_webhook_events_result
    CHECK (result_code IN ('RECEIVED', 'PROCESSED', 'IGNORED', 'UNMATCHED', 'AMBIGUOUS', 'FAILED'));
GO

IF OBJECT_ID(N'dbo.FK_payment_webhook_events_payment_transaction', N'F') IS NULL
    ALTER TABLE dbo.payment_webhook_events WITH CHECK ADD CONSTRAINT FK_payment_webhook_events_payment_transaction
        FOREIGN KEY (payment_transaction_id)
        REFERENCES dbo.purchase_payment_transactions(payment_transaction_id);
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.payment_webhook_events')
      AND name = N'IX_payment_webhook_events_payment_transaction'
)
    CREATE NONCLUSTERED INDEX IX_payment_webhook_events_payment_transaction
        ON dbo.payment_webhook_events(payment_transaction_id, received_at_utc DESC)
        INCLUDE (provider_code, external_event_id, result_code);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version_no = 9)
BEGIN
    INSERT INTO dbo.schema_versions (version_no, version_name)
    VALUES (9, N'SePay checkout transactions and reconciliation ledger');
END;
GO

PRINT N'SubVid SePay schema V9 deployed successfully.';
GO

IF DB_ID(N'MiniCore') IS NULL
    CREATE DATABASE MiniCore;
GO

USE MiniCore;
GO

DROP TABLE IF EXISTS dbo.AuditLog;
DROP TABLE IF EXISTS dbo.IdempotencyKeys;
DROP TABLE IF EXISTS dbo.Movements;
DROP TABLE IF EXISTS dbo.Transfers;
DROP TABLE IF EXISTS dbo.Accounts;
GO

CREATE TABLE dbo.Accounts
(
    Id          NVARCHAR(20)  NOT NULL CONSTRAINT PK_Accounts PRIMARY KEY,
    OwnerId     NVARCHAR(20)  NOT NULL,
    HolderName  NVARCHAR(100) NOT NULL,
    Status      NVARCHAR(10)  NOT NULL,
    Balance     DECIMAL(18,2) NOT NULL,
    Currency    CHAR(3)       NOT NULL CONSTRAINT DF_Accounts_Currency DEFAULT ('USD'),
    RowVersion  ROWVERSION    NOT NULL,
    CONSTRAINT CK_Accounts_Status  CHECK (Status IN (N'Active', N'Blocked', N'Closed')),
    CONSTRAINT CK_Accounts_Balance CHECK (Balance >= 0)
);
GO

CREATE TABLE dbo.Transfers
(
    Id                   UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Transfers PRIMARY KEY,
    SourceAccountId      NVARCHAR(20)     NOT NULL,
    DestinationAccountId NVARCHAR(20)     NOT NULL,
    Amount               DECIMAL(18,2)    NOT NULL,
    Fee                  DECIMAL(18,2)    NOT NULL,
    Status               NVARCHAR(10)     NOT NULL,
    CreatedAtUtc         DATETIME2(3)     NOT NULL,
    ReversedAtUtc        DATETIME2(3)     NULL,
    IdempotencyKey       NVARCHAR(100)    NOT NULL,
    Description          NVARCHAR(200)    NULL,
    CONSTRAINT FK_Transfers_Source      FOREIGN KEY (SourceAccountId)      REFERENCES dbo.Accounts (Id),
    CONSTRAINT FK_Transfers_Destination FOREIGN KEY (DestinationAccountId) REFERENCES dbo.Accounts (Id),
    CONSTRAINT CK_Transfers_Status CHECK (Status IN (N'Completed', N'Reversed')),
    CONSTRAINT CK_Transfers_Amount CHECK (Amount > 0)
);
GO

CREATE NONCLUSTERED INDEX IX_Transfers_Source_CreatedAt
    ON dbo.Transfers (SourceAccountId, CreatedAtUtc)
    INCLUDE (Amount, Status);
GO

CREATE TABLE dbo.Movements
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Movements PRIMARY KEY,
    AccountId    NVARCHAR(20)     NOT NULL,
    TransferId   UNIQUEIDENTIFIER NOT NULL,
    Type         NVARCHAR(20)     NOT NULL,
    Amount       DECIMAL(18,2)    NOT NULL,
    BalanceAfter DECIMAL(18,2)    NOT NULL,
    CreatedAtUtc DATETIME2(3)     NOT NULL,
    CONSTRAINT FK_Movements_Account  FOREIGN KEY (AccountId)  REFERENCES dbo.Accounts (Id),
    CONSTRAINT FK_Movements_Transfer FOREIGN KEY (TransferId) REFERENCES dbo.Transfers (Id),
    CONSTRAINT CK_Movements_Type CHECK (Type IN
        (N'Debit', N'Credit', N'Fee', N'ReversalDebit', N'ReversalCredit', N'FeeRefund'))
);
GO

CREATE NONCLUSTERED INDEX IX_Movements_Account_CreatedAt
    ON dbo.Movements (AccountId, CreatedAtUtc DESC, Id DESC)
    INCLUDE (TransferId, Type, Amount, BalanceAfter);
GO

CREATE TABLE dbo.IdempotencyKeys
(
    ClientId       NVARCHAR(50)  NOT NULL,
    [Key]          NVARCHAR(100) NOT NULL,
    RequestHash    CHAR(64)      NOT NULL,
    ResponseStatus INT           NULL,
    ResponseBody   NVARCHAR(MAX) NULL,
    CreatedAtUtc   DATETIME2(3)  NOT NULL,
    CONSTRAINT PK_IdempotencyKeys PRIMARY KEY (ClientId, [Key])
);
GO

CREATE TABLE dbo.AuditLog
(
    Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    TimestampUtc  DATETIME2(3)  NOT NULL,
    ActorId       NVARCHAR(50)  NOT NULL,
    Action        NVARCHAR(50)  NOT NULL,
    EntityId      NVARCHAR(50)  NOT NULL,
    CorrelationId NVARCHAR(100) NULL,
    Details       NVARCHAR(500) NULL
);
GO

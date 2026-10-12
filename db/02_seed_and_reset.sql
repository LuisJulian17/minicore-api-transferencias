USE MiniCore;
GO

DELETE FROM dbo.AuditLog;
DELETE FROM dbo.IdempotencyKeys;
DELETE FROM dbo.Movements;
DELETE FROM dbo.Transfers;
DELETE FROM dbo.Accounts;
GO

INSERT INTO dbo.Accounts (Id, OwnerId, HolderName, Status, Balance, Currency) VALUES
    (N'ACC-1001', N'CUST-01', N'Ana Torres',                N'Active',  10000.00, 'USD'),
    (N'ACC-1002', N'CUST-02', N'Luis Pérez',                N'Active',    250.00, 'USD'), 
    (N'ACC-1003', N'CUST-03', N'Carmen Ruiz',               N'Blocked',  5000.00, 'USD'),
    (N'ACC-1004', N'CUST-04', N'Empresa XYZ',               N'Active',  120000.00, 'USD'),
    (N'ACC-1005', N'CUST-05', N'Cuenta histórica',          N'Closed',      0.00, 'USD'),
    (N'ACC-1006', N'CUST-06', N'Pedro Gil',                 N'Active',    501.00, 'USD'); 
GO
-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Add TenantId to Vendor table
IF COL_LENGTH('dmscs.Vendor', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.Vendor ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Vendor_Tenant')
BEGIN
    ALTER TABLE dmscs.Vendor ADD CONSTRAINT FK_Vendor_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Vendor_TenantId' AND object_id = OBJECT_ID('dmscs.Vendor'))
    CREATE INDEX IX_Vendor_TenantId ON dmscs.Vendor (TenantId);
GO

-- Add TenantId to ClaimSet table
IF COL_LENGTH('dmscs.ClaimSet', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.ClaimSet ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ClaimSet_Tenant')
BEGIN
    ALTER TABLE dmscs.ClaimSet ADD CONSTRAINT FK_ClaimSet_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ClaimSet_TenantId' AND object_id = OBJECT_ID('dmscs.ClaimSet'))
    CREATE INDEX IX_ClaimSet_TenantId ON dmscs.ClaimSet (TenantId);
GO

-- Add TenantId to AuthorizationStrategy table
IF COL_LENGTH('dmscs.AuthorizationStrategy', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.AuthorizationStrategy ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_AuthorizationStrategy_Tenant')
BEGIN
    ALTER TABLE dmscs.AuthorizationStrategy ADD CONSTRAINT FK_AuthorizationStrategy_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuthorizationStrategy_TenantId' AND object_id = OBJECT_ID('dmscs.AuthorizationStrategy'))
    CREATE INDEX IX_AuthorizationStrategy_TenantId ON dmscs.AuthorizationStrategy (TenantId);
GO

-- Add TenantId to ResourceClaim table
IF COL_LENGTH('dmscs.ResourceClaim', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.ResourceClaim ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ResourceClaim_Tenant')
BEGIN
    ALTER TABLE dmscs.ResourceClaim ADD CONSTRAINT FK_ResourceClaim_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ResourceClaim_TenantId' AND object_id = OBJECT_ID('dmscs.ResourceClaim'))
    CREATE INDEX IX_ResourceClaim_TenantId ON dmscs.ResourceClaim (TenantId);
GO

-- Add TenantId to DataStore table
IF COL_LENGTH('dmscs.DataStore', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.DataStore ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_DataStore_Tenant')
BEGIN
    ALTER TABLE dmscs.DataStore ADD CONSTRAINT FK_DataStore_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DataStore_TenantId' AND object_id = OBJECT_ID('dmscs.DataStore'))
    CREATE INDEX IX_DataStore_TenantId ON dmscs.DataStore (TenantId);
GO

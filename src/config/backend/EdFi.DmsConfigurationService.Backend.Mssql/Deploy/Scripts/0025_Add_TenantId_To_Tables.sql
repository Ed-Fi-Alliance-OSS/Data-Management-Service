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
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'fk_vendor_tenant')
BEGIN
    ALTER TABLE dmscs.Vendor ADD CONSTRAINT fk_vendor_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_vendor_tenantid' AND object_id = OBJECT_ID('dmscs.Vendor'))
    CREATE INDEX idx_vendor_tenantid ON dmscs.Vendor (TenantId);
GO

-- Add TenantId to ClaimSet table
IF COL_LENGTH('dmscs.ClaimSet', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.ClaimSet ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'fk_claimset_tenant')
BEGIN
    ALTER TABLE dmscs.ClaimSet ADD CONSTRAINT fk_claimset_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_claimset_tenantid' AND object_id = OBJECT_ID('dmscs.ClaimSet'))
    CREATE INDEX idx_claimset_tenantid ON dmscs.ClaimSet (TenantId);
GO

-- Add TenantId to AuthorizationStrategy table
IF COL_LENGTH('dmscs.AuthorizationStrategy', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.AuthorizationStrategy ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'fk_authorizationstrategy_tenant')
BEGIN
    ALTER TABLE dmscs.AuthorizationStrategy ADD CONSTRAINT fk_authorizationstrategy_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_authorizationstrategy_tenantid' AND object_id = OBJECT_ID('dmscs.AuthorizationStrategy'))
    CREATE INDEX idx_authorizationstrategy_tenantid ON dmscs.AuthorizationStrategy (TenantId);
GO

-- Add TenantId to ResourceClaim table
IF COL_LENGTH('dmscs.ResourceClaim', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.ResourceClaim ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'fk_resourceclaim_tenant')
BEGIN
    ALTER TABLE dmscs.ResourceClaim ADD CONSTRAINT fk_resourceclaim_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_resourceclaim_tenantid' AND object_id = OBJECT_ID('dmscs.ResourceClaim'))
    CREATE INDEX idx_resourceclaim_tenantid ON dmscs.ResourceClaim (TenantId);
GO

-- Add TenantId to DataStore table
IF COL_LENGTH('dmscs.DataStore', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.DataStore ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'fk_datastore_tenant')
BEGIN
    ALTER TABLE dmscs.DataStore ADD CONSTRAINT fk_datastore_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_datastore_tenantid' AND object_id = OBJECT_ID('dmscs.DataStore'))
    CREATE INDEX idx_datastore_tenantid ON dmscs.DataStore (TenantId);
GO

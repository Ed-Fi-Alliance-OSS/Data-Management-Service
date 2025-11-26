-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Add TenantId to Vendor table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'dmscs' AND table_name = 'vendor' AND column_name = 'tenantid'
    ) THEN
        ALTER TABLE dmscs.Vendor ADD COLUMN TenantId BIGINT NULL;
        ALTER TABLE dmscs.Vendor ADD CONSTRAINT fk_vendor_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE CASCADE;
        COMMENT ON COLUMN dmscs.Vendor.TenantId IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';
    END IF;
END$$;

-- Add TenantId to ClaimSet table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'dmscs' AND table_name = 'claimset' AND column_name = 'tenantid'
    ) THEN
        ALTER TABLE dmscs.ClaimSet ADD COLUMN TenantId BIGINT NULL;
        ALTER TABLE dmscs.ClaimSet ADD CONSTRAINT fk_claimset_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE CASCADE;
        COMMENT ON COLUMN dmscs.ClaimSet.TenantId IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';
    END IF;
END$$;

-- Add TenantId to AuthorizationStrategy table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'dmscs' AND table_name = 'authorizationstrategy' AND column_name = 'tenantid'
    ) THEN
        ALTER TABLE dmscs.AuthorizationStrategy ADD COLUMN TenantId BIGINT NULL;
        ALTER TABLE dmscs.AuthorizationStrategy ADD CONSTRAINT fk_authorizationstrategy_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE CASCADE;
        COMMENT ON COLUMN dmscs.AuthorizationStrategy.TenantId IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';
    END IF;
END$$;

-- Add TenantId to ResourceClaim table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'dmscs' AND table_name = 'resourceclaim' AND column_name = 'tenantid'
    ) THEN
        ALTER TABLE dmscs.ResourceClaim ADD COLUMN TenantId BIGINT NULL;
        ALTER TABLE dmscs.ResourceClaim ADD CONSTRAINT fk_resourceclaim_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE CASCADE;
        COMMENT ON COLUMN dmscs.ResourceClaim.TenantId IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';
    END IF;
END$$;

-- Add TenantId to DmsInstance table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'dmscs' AND table_name = 'dmsinstance' AND column_name = 'tenantid'
    ) THEN
        ALTER TABLE dmscs.DmsInstance ADD COLUMN TenantId BIGINT NULL;
        ALTER TABLE dmscs.DmsInstance ADD CONSTRAINT fk_dmsinstance_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE CASCADE;
        COMMENT ON COLUMN dmscs.DmsInstance.TenantId IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';
    END IF;
END$$;

-- Create indexes on TenantId columns for efficient tenant-scoped queries
CREATE INDEX IF NOT EXISTS idx_vendor_tenantid ON dmscs.Vendor (TenantId);
CREATE INDEX IF NOT EXISTS idx_claimset_tenantid ON dmscs.ClaimSet (TenantId);
CREATE INDEX IF NOT EXISTS idx_authorizationstrategy_tenantid ON dmscs.AuthorizationStrategy (TenantId);
CREATE INDEX IF NOT EXISTS idx_resourceclaim_tenantid ON dmscs.ResourceClaim (TenantId);
CREATE INDEX IF NOT EXISTS idx_dmsinstance_tenantid ON dmscs.DmsInstance (TenantId);

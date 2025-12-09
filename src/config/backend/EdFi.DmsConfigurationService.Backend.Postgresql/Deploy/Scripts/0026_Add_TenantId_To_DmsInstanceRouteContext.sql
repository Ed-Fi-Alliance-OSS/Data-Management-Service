-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Add TenantId to DmsInstanceRouteContext table
-- This was overlooked in the original tenant migration (0025)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'dmscs' AND table_name = 'dmsinstanceroutecontext' AND column_name = 'tenantid'
    ) THEN
        ALTER TABLE dmscs.DmsInstanceRouteContext ADD COLUMN TenantId BIGINT NULL;
        ALTER TABLE dmscs.DmsInstanceRouteContext ADD CONSTRAINT fk_dmsinstanceroutecontext_tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE CASCADE;
        COMMENT ON COLUMN dmscs.DmsInstanceRouteContext.TenantId IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';
    END IF;
END$$;

-- Create index on TenantId column for efficient tenant-scoped queries
CREATE INDEX IF NOT EXISTS idx_dmsinstanceroutecontext_tenantid ON dmscs.DmsInstanceRouteContext (TenantId);

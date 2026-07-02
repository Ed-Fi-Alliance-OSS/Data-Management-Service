-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Add TenantId to Vendor table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name = 'Vendor'
          AND column_name = 'TenantId'
    ) THEN
        ALTER TABLE "dmscs"."Vendor" ADD COLUMN "TenantId" BIGINT NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_Vendor_Tenant'
          AND conrelid = '"dmscs"."Vendor"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Vendor" ADD CONSTRAINT "FK_Vendor_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."Vendor"."TenantId" IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';

-- Add TenantId to ClaimSet table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name = 'ClaimSet'
          AND column_name = 'TenantId'
    ) THEN
        ALTER TABLE "dmscs"."ClaimSet" ADD COLUMN "TenantId" BIGINT NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ClaimSet_Tenant'
          AND conrelid = '"dmscs"."ClaimSet"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ClaimSet" ADD CONSTRAINT "FK_ClaimSet_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."ClaimSet"."TenantId" IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';

-- Add TenantId to AuthorizationStrategy table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name = 'AuthorizationStrategy'
          AND column_name = 'TenantId'
    ) THEN
        ALTER TABLE "dmscs"."AuthorizationStrategy" ADD COLUMN "TenantId" BIGINT NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_AuthorizationStrategy_Tenant'
          AND conrelid = '"dmscs"."AuthorizationStrategy"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."AuthorizationStrategy" ADD CONSTRAINT "FK_AuthorizationStrategy_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."AuthorizationStrategy"."TenantId" IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';

-- Add TenantId to ResourceClaim table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name = 'ResourceClaim'
          AND column_name = 'TenantId'
    ) THEN
        ALTER TABLE "dmscs"."ResourceClaim" ADD COLUMN "TenantId" BIGINT NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ResourceClaim_Tenant'
          AND conrelid = '"dmscs"."ResourceClaim"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ResourceClaim" ADD CONSTRAINT "FK_ResourceClaim_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."ResourceClaim"."TenantId" IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';

-- Add TenantId to DataStore table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name = 'DataStore'
          AND column_name = 'TenantId'
    ) THEN
        ALTER TABLE "dmscs"."DataStore" ADD COLUMN "TenantId" BIGINT NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_DataStore_Tenant'
          AND conrelid = '"dmscs"."DataStore"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."DataStore" ADD CONSTRAINT "FK_DataStore_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."DataStore"."TenantId" IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';

-- Create indexes on TenantId columns for efficient tenant-scoped queries
CREATE INDEX IF NOT EXISTS "IX_Vendor_TenantId" ON "dmscs"."Vendor" ("TenantId");
CREATE INDEX IF NOT EXISTS "IX_ClaimSet_TenantId" ON "dmscs"."ClaimSet" ("TenantId");
CREATE INDEX IF NOT EXISTS "IX_AuthorizationStrategy_TenantId" ON "dmscs"."AuthorizationStrategy" ("TenantId");
CREATE INDEX IF NOT EXISTS "IX_ResourceClaim_TenantId" ON "dmscs"."ResourceClaim" ("TenantId");
CREATE INDEX IF NOT EXISTS "IX_DataStore_TenantId" ON "dmscs"."DataStore" ("TenantId");

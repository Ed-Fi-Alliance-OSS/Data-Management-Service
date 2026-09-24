-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Scopes vendor company names and profile names by tenant, and gives Profile a tenant column.
--
-- Uniqueness becomes (TenantId, Company) and (TenantId, ProfileName). NULLS NOT DISTINCT keeps
-- single-tenant deployments (TenantId IS NULL) rejecting duplicates exactly as before; PostgreSQL
-- would otherwise treat every NULL tenant as distinct. NULLS NOT DISTINCT requires PostgreSQL 15
-- or later; see "Database versions" in docs/OPERATIONS.md.
--
-- What happens to profiles that already exist. Nothing recorded which tenant created a profile, so
-- each existing profile is assigned by the applications that use it, through their vendor's tenant:
--   * A profile used only by applications of one tenant's vendors is assigned to that tenant. Its id
--     does not change.
--   * A profile used by several tenants keeps its original row, which stays unassigned if an
--     unassigned (NULL-tenant) vendor's application uses it, and otherwise goes to the lowest tenant
--     id that uses it. Every other tenant that uses it gets a copy with the same name, definition and
--     CreatedBy, and that tenant's application assignments are moved to the copy. Copies get new ids.
--   * A profile that no application uses stays unassigned. In multi-tenant mode no tenant lists it;
--     in single-tenant mode it is visible as before.
--   * Single-tenant databases, where every vendor is unassigned, are unchanged.
-- Only recorded application assignments are preserved. A client that selects a profile through the
-- profile header without an assignment loses access to it until the profile is re-created in that
-- client's tenant. See "Upgrading an Existing Multi-Tenant Deployment" in
-- docs/MULTI-TENANCY-GETTING-STARTED.md for the inventory and coordinated upgrade steps.

-- Vendor: tenant-scoped company uniqueness. The new constraint is added before the old one is
-- dropped, so uniqueness is never absent.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_Vendor_TenantId_Company'
          AND conrelid = '"dmscs"."Vendor"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Vendor" ADD CONSTRAINT "UX_Vendor_TenantId_Company" UNIQUE NULLS NOT DISTINCT ("TenantId", "Company");
    END IF;
END$$;

ALTER TABLE "dmscs"."Vendor" DROP CONSTRAINT IF EXISTS "UX_Vendor_Company";

-- Profile: tenant column, matching 0025_Add_TenantId_To_Tables.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name = 'Profile'
          AND column_name = 'TenantId'
    ) THEN
        ALTER TABLE "dmscs"."Profile" ADD COLUMN "TenantId" BIGINT NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_Profile_Tenant'
          AND conrelid = '"dmscs"."Profile"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Profile" ADD CONSTRAINT "FK_Profile_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."Profile"."TenantId" IS 'Tenant id for multi-tenancy support (null when multi-tenancy is disabled)';

CREATE INDEX IF NOT EXISTS "IX_Profile_TenantId" ON "dmscs"."Profile" ("TenantId");

-- Profile: tenant-scoped name uniqueness. This must precede the assignment below, because a copy
-- shares its original's name.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_Profile_TenantId_ProfileName'
          AND conrelid = '"dmscs"."Profile"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Profile" ADD CONSTRAINT "UX_Profile_TenantId_ProfileName" UNIQUE NULLS NOT DISTINCT ("TenantId", "ProfileName");
    END IF;
END$$;

ALTER TABLE "dmscs"."Profile" DROP CONSTRAINT IF EXISTS "UX_Profile_ProfileName";

-- Existing profiles: assign by usage and copy when shared, as the header describes. The loop reads
-- a snapshot of the usage taken before any row changes, and a DO block runs as one statement, so
-- the whole assignment commits or none of it does. Replaying it is a no-op: no unassigned profile
-- is still used by a tenant vendor's application, except an original kept for an unassigned
-- vendor, whose only usage rank is that vendor and therefore does no work.
DO $$
DECLARE
    profile_usage RECORD;
    copy_id INT;
BEGIN
    FOR profile_usage IN
        SELECT ranked_usage."ProfileId",
               ranked_usage."TenantId",
               ranked_usage."UsageRank"
        FROM (
            SELECT distinct_usage."ProfileId",
                   distinct_usage."TenantId",
                   ROW_NUMBER() OVER (
                       PARTITION BY distinct_usage."ProfileId"
                       ORDER BY distinct_usage."TenantId" NULLS FIRST
                   ) AS "UsageRank"
            FROM (
                SELECT DISTINCT application_profile."ProfileId", vendor."TenantId"
                FROM "dmscs"."ApplicationProfile" application_profile
                JOIN "dmscs"."Profile" profile
                    ON profile."Id" = application_profile."ProfileId"
                JOIN "dmscs"."Application" application
                    ON application."Id" = application_profile."ApplicationId"
                JOIN "dmscs"."Vendor" vendor
                    ON vendor."Id" = application."VendorId"
                WHERE profile."TenantId" IS NULL
            ) distinct_usage
        ) ranked_usage
        ORDER BY ranked_usage."ProfileId", ranked_usage."UsageRank"
    LOOP
        IF profile_usage."UsageRank" = 1 THEN
            IF profile_usage."TenantId" IS NOT NULL THEN
                UPDATE "dmscs"."Profile"
                SET "TenantId" = profile_usage."TenantId"
                WHERE "Id" = profile_usage."ProfileId";
            END IF;
        ELSE
            INSERT INTO "dmscs"."Profile" ("ProfileName", "Definition", "CreatedBy", "TenantId")
            SELECT original."ProfileName", original."Definition", original."CreatedBy", profile_usage."TenantId"
            FROM "dmscs"."Profile" original
            WHERE original."Id" = profile_usage."ProfileId"
            RETURNING "Id" INTO copy_id;

            UPDATE "dmscs"."ApplicationProfile" application_profile
            SET "ProfileId" = copy_id
            FROM "dmscs"."Application" application
            JOIN "dmscs"."Vendor" vendor
                ON vendor."Id" = application."VendorId"
            WHERE application_profile."ApplicationId" = application."Id"
              AND application_profile."ProfileId" = profile_usage."ProfileId"
              AND vendor."TenantId" = profile_usage."TenantId";
        END IF;
    END LOOP;
END$$;

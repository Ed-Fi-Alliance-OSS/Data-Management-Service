-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Education organizations read from DMS for each ordinary data store.
--
-- EducationOrganizationSnapshotTarget has one row per ordinary data store that has been refreshed. It
-- holds the generation counter, the published (active) generation and the refresh lease. Each refresh
-- stages its organizations under a new generation, and publishing moves ActiveGeneration to it, so
-- readers see one complete generation at a time. A target row references DataStore with ON DELETE
-- CASCADE, and organization rows reference their target row with ON DELETE CASCADE, so deleting an
-- ordinary data store removes its snapshot in the same statement, on every delete path. DataStore ->
-- target -> organizations is the only cascade path into either table, which SQL Server requires. The
-- target's TenantId foreign key is NO ACTION for the same reason; organization rows copy TenantId
-- from their target. All times are UTC.
--
-- Everything runs in one DO block, which executes as a single statement and therefore as one
-- transaction, so a failed run leaves nothing behind. The existence guards let a rerun after a lost
-- journal row succeed.
DO $$
BEGIN
    CREATE TABLE IF NOT EXISTS "dmscs"."EducationOrganizationSnapshotTarget" (
        "DataStoreId" INT NOT NULL,
        "TenantId" BIGINT NULL,
        "LastGeneration" BIGINT NOT NULL DEFAULT 0,
        "ActiveGeneration" BIGINT NULL,
        "RefreshedAt" TIMESTAMP NULL,
        "SourceContractVersion" VARCHAR(100) NULL,
        "LeaseOwner" VARCHAR(32) NULL,
        "LeaseGeneration" BIGINT NULL,
        "LeaseExpiresAt" TIMESTAMP NULL,
        "CreatedAt" TIMESTAMP NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
        "LastModifiedAt" TIMESTAMP NULL
    );

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'PK_EducationOrganizationSnapshotTarget'
          AND conrelid = '"dmscs"."EducationOrganizationSnapshotTarget"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."EducationOrganizationSnapshotTarget" ADD CONSTRAINT "PK_EducationOrganizationSnapshotTarget" PRIMARY KEY ("DataStoreId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_EducationOrganizationSnapshotTarget_DataStore'
          AND conrelid = '"dmscs"."EducationOrganizationSnapshotTarget"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."EducationOrganizationSnapshotTarget" ADD CONSTRAINT "FK_EducationOrganizationSnapshotTarget_DataStore" FOREIGN KEY ("DataStoreId") REFERENCES "dmscs"."DataStore"("Id") ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_EducationOrganizationSnapshotTarget_Tenant'
          AND conrelid = '"dmscs"."EducationOrganizationSnapshotTarget"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."EducationOrganizationSnapshotTarget" ADD CONSTRAINT "FK_EducationOrganizationSnapshotTarget_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE NO ACTION;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_EducationOrganizationSnapshotTarget_Generations'
          AND conrelid = '"dmscs"."EducationOrganizationSnapshotTarget"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."EducationOrganizationSnapshotTarget" ADD CONSTRAINT "CK_EducationOrganizationSnapshotTarget_Generations" CHECK (
            "LastGeneration" >= 0
            AND ("ActiveGeneration" IS NULL OR "ActiveGeneration" BETWEEN 1 AND "LastGeneration")
        );
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_EducationOrganizationSnapshotTarget_Lease'
          AND conrelid = '"dmscs"."EducationOrganizationSnapshotTarget"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."EducationOrganizationSnapshotTarget" ADD CONSTRAINT "CK_EducationOrganizationSnapshotTarget_Lease" CHECK (
            ("LeaseOwner" IS NULL AND "LeaseGeneration" IS NULL AND "LeaseExpiresAt" IS NULL)
            OR (
                "LeaseOwner" IS NOT NULL
                AND "LeaseGeneration" IS NOT NULL
                AND "LeaseExpiresAt" IS NOT NULL
                AND "LeaseGeneration" BETWEEN 1 AND "LastGeneration"
            )
        );
    END IF;

    CREATE INDEX IF NOT EXISTS "IX_EducationOrganizationSnapshotTarget_TenantId"
        ON "dmscs"."EducationOrganizationSnapshotTarget" ("TenantId");

    CREATE TABLE IF NOT EXISTS "dmscs"."EducationOrganizationSnapshot" (
        "DataStoreId" INT NOT NULL,
        "Generation" BIGINT NOT NULL,
        "TenantId" BIGINT NULL,
        "EducationOrganizationId" BIGINT NOT NULL,
        "NameOfInstitution" VARCHAR(256) NOT NULL,
        "ShortNameOfInstitution" VARCHAR(256) NULL,
        "Discriminator" VARCHAR(128) NOT NULL,
        "ParentId" BIGINT NULL,
        "RefreshedAt" TIMESTAMP NOT NULL,
        "SourceContractVersion" VARCHAR(100) NOT NULL
    );

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'PK_EducationOrganizationSnapshot'
          AND conrelid = '"dmscs"."EducationOrganizationSnapshot"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."EducationOrganizationSnapshot" ADD CONSTRAINT "PK_EducationOrganizationSnapshot" PRIMARY KEY ("DataStoreId", "Generation", "EducationOrganizationId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_EducationOrganizationSnapshot_Target'
          AND conrelid = '"dmscs"."EducationOrganizationSnapshot"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."EducationOrganizationSnapshot" ADD CONSTRAINT "FK_EducationOrganizationSnapshot_Target" FOREIGN KEY ("DataStoreId") REFERENCES "dmscs"."EducationOrganizationSnapshotTarget"("DataStoreId") ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_EducationOrganizationSnapshot_Generation'
          AND conrelid = '"dmscs"."EducationOrganizationSnapshot"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."EducationOrganizationSnapshot" ADD CONSTRAINT "CK_EducationOrganizationSnapshot_Generation" CHECK ("Generation" >= 1);
    END IF;
END$$;

COMMENT ON TABLE "dmscs"."EducationOrganizationSnapshotTarget" IS 'Refresh state of the education organization snapshot of one ordinary data store';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."DataStoreId" IS 'Ordinary data store id';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."TenantId" IS 'Tenant of the data store';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."LastGeneration" IS 'Highest generation allocated to a refresh';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."ActiveGeneration" IS 'Published generation that readers see';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."RefreshedAt" IS 'When the active generation was published (UTC)';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."SourceContractVersion" IS 'DMS projection contract version of the active generation';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."LeaseOwner" IS 'Refresh execution that holds the lease';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."LeaseGeneration" IS 'Generation the lease holder may stage and publish';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."LeaseExpiresAt" IS 'When the lease expires (UTC)';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshotTarget"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON TABLE "dmscs"."EducationOrganizationSnapshot" IS 'Education organizations of one data store snapshot generation';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."DataStoreId" IS 'Ordinary data store id';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."Generation" IS 'Snapshot generation the row belongs to';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."TenantId" IS 'Tenant of the data store';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."EducationOrganizationId" IS 'Education organization id';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."NameOfInstitution" IS 'Name of the education organization';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."ShortNameOfInstitution" IS 'Short name of the education organization';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."Discriminator" IS 'Education organization type, such as edfi.School';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."ParentId" IS 'Direct parent education organization id';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."RefreshedAt" IS 'When the generation was staged (UTC)';
COMMENT ON COLUMN "dmscs"."EducationOrganizationSnapshot"."SourceContractVersion" IS 'DMS projection contract version the generation was read with';

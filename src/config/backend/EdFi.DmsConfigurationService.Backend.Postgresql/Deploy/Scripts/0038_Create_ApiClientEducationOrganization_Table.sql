-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1639: stores each API client's education organizations, so a client can be limited to some
-- of its Application's education organizations.
--
-- Each client row references the Application's ApplicationEducationOrganization row with
-- ON DELETE CASCADE, so removing an education organization from an Application removes it from
-- every client. A second key, (ApiClientId, ApplicationId) to the new unique ApiClient
-- (Id, ApplicationId), ties every row to the client's current Application. It is NO ACTION
-- (SQL Server rejects a second cascade path into the table, and both engines keep the same
-- objects), so deleting a client or moving it to another Application is rejected until the code
-- deletes the client's rows first. Deleting an Application, directly or through its Vendor, still
-- removes every client row through the education organization cascade. The index leads with the
-- education organization and serves that cascade.
--
-- InheritsApplicationEducationOrganizations is stored rather than derived: a client narrowed to
-- every current education organization must not pick up ones added later.
-- IdentityProviderSyncPendingSince marks a client whose identity provider claims still need an
-- Application change applied. It is an instant (TIMESTAMPTZ, as OpenIddictToken.ExpirationDate
-- after 0032), so it round-trips whatever the session time zone.
--
-- Upgrade: every existing client inherits (the column default) and its rows are copied from its
-- Application's current list. Nothing reads the identity providers.
--
-- Everything runs in one DO block, which executes as a single statement and therefore as one
-- transaction. The constraints and the new columns take their table locks before the backfill
-- reads, and hold them until the block ends, so no client or Application education organization
-- can change between the backfill and the commit. The existence guards and NOT EXISTS make a retry
-- converge.
DO $$
BEGIN
    CREATE TABLE IF NOT EXISTS "dmscs"."ApiClientEducationOrganization" (
        "ApiClientId" INT NOT NULL,
        "ApplicationId" INT NOT NULL,
        "EducationOrganizationId" BIGINT NOT NULL,
        "CreatedAt" TIMESTAMP NOT NULL DEFAULT NOW(),
        "CreatedBy" VARCHAR(256),
        "LastModifiedAt" TIMESTAMP,
        "ModifiedBy" VARCHAR(256)
    );

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_ApiClient_Id_ApplicationId'
          AND conrelid = '"dmscs"."ApiClient"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClient" ADD CONSTRAINT "UX_ApiClient_Id_ApplicationId" UNIQUE ("Id", "ApplicationId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'PK_ApiClientEducationOrganization'
          AND conrelid = '"dmscs"."ApiClientEducationOrganization"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClientEducationOrganization" ADD CONSTRAINT "PK_ApiClientEducationOrganization" PRIMARY KEY ("ApiClientId", "EducationOrganizationId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ApiClientEducationOrganization_ApplicationEducationOrg'
          AND conrelid = '"dmscs"."ApiClientEducationOrganization"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClientEducationOrganization" ADD CONSTRAINT "FK_ApiClientEducationOrganization_ApplicationEducationOrg" FOREIGN KEY ("ApplicationId", "EducationOrganizationId") REFERENCES "dmscs"."ApplicationEducationOrganization"("ApplicationId", "EducationOrganizationId") ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ApiClientEducationOrganization_ApiClient'
          AND conrelid = '"dmscs"."ApiClientEducationOrganization"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClientEducationOrganization" ADD CONSTRAINT "FK_ApiClientEducationOrganization_ApiClient" FOREIGN KEY ("ApiClientId", "ApplicationId") REFERENCES "dmscs"."ApiClient"("Id", "ApplicationId") ON DELETE NO ACTION;
    END IF;

    CREATE INDEX IF NOT EXISTS "IX_ApiClientEducationOrganization_EducationOrganizationId"
        ON "dmscs"."ApiClientEducationOrganization" ("EducationOrganizationId", "ApplicationId");

    ALTER TABLE "dmscs"."ApiClient"
        ADD COLUMN IF NOT EXISTS "InheritsApplicationEducationOrganizations" BOOLEAN NOT NULL DEFAULT true,
        ADD COLUMN IF NOT EXISTS "IdentityProviderSyncPendingSince" TIMESTAMPTZ NULL;

    INSERT INTO "dmscs"."ApiClientEducationOrganization" ("ApiClientId", "ApplicationId", "EducationOrganizationId")
    SELECT client."Id", client."ApplicationId", application_edorg."EducationOrganizationId"
    FROM "dmscs"."ApiClient" client
    JOIN "dmscs"."ApplicationEducationOrganization" application_edorg
        ON application_edorg."ApplicationId" = client."ApplicationId"
    WHERE NOT EXISTS (
        SELECT 1
        FROM "dmscs"."ApiClientEducationOrganization" existing
        WHERE existing."ApiClientId" = client."Id"
          AND existing."EducationOrganizationId" = application_edorg."EducationOrganizationId"
    );
END$$;

COMMENT ON TABLE "dmscs"."ApiClientEducationOrganization" IS 'Relationship of API clients with their Application''s education organizations';
COMMENT ON COLUMN "dmscs"."ApiClientEducationOrganization"."ApiClientId" IS 'API client id';
COMMENT ON COLUMN "dmscs"."ApiClientEducationOrganization"."ApplicationId" IS 'The API client''s current Application id';
COMMENT ON COLUMN "dmscs"."ApiClientEducationOrganization"."EducationOrganizationId" IS 'Education organization id';
COMMENT ON COLUMN "dmscs"."ApiClientEducationOrganization"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."ApiClientEducationOrganization"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."ApiClientEducationOrganization"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."ApiClientEducationOrganization"."ModifiedBy" IS 'User or client ID who last modified the record';
COMMENT ON COLUMN "dmscs"."ApiClient"."InheritsApplicationEducationOrganizations" IS 'Whether the API client follows its Application''s education organizations';
COMMENT ON COLUMN "dmscs"."ApiClient"."IdentityProviderSyncPendingSince" IS 'When an Application change began awaiting this API client''s identity provider update (UTC)';

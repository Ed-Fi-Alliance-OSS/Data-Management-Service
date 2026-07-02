-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS "dmscs"."OpenIddictScope" (
    "Id" uuid NOT NULL,
    "Name" varchar(100) NOT NULL,
    "Description" varchar(200),
    "CreatedAt" TIMESTAMP NOT NULL DEFAULT NOW(),
    "CreatedBy" VARCHAR(256),
    "LastModifiedAt" TIMESTAMP,
    "ModifiedBy" VARCHAR(256)
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'PK_OpenIddictScope'
          AND conrelid = '"dmscs"."OpenIddictScope"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictScope" ADD CONSTRAINT "PK_OpenIddictScope" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_OpenIddictScope_Name'
          AND conrelid = '"dmscs"."OpenIddictScope"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictScope" ADD CONSTRAINT "UX_OpenIddictScope_Name" UNIQUE ("Name");
    END IF;
END$$;

COMMENT ON TABLE "dmscs"."OpenIddictScope" IS 'OpenIddict scopes storage.';

COMMENT ON COLUMN "dmscs"."OpenIddictScope"."Id" IS 'Scope unique identifier.';

COMMENT ON COLUMN "dmscs"."OpenIddictScope"."Name" IS 'Scope name.';

COMMENT ON COLUMN "dmscs"."OpenIddictScope"."Description" IS 'Scope description.';

COMMENT ON COLUMN "dmscs"."OpenIddictScope"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."OpenIddictScope"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."OpenIddictScope"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."OpenIddictScope"."ModifiedBy" IS 'User or client ID who last modified the record';

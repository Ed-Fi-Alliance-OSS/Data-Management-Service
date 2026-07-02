-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
CREATE TABLE IF NOT EXISTS "dmscs"."OpenIddictRole" (
    "Id" uuid NOT NULL,
    "Name" varchar(100) NOT NULL,
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
        WHERE conname = 'PK_OpenIddictRole'
          AND conrelid = '"dmscs"."OpenIddictRole"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictRole" ADD CONSTRAINT "PK_OpenIddictRole" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_OpenIddictRole_Name'
          AND conrelid = '"dmscs"."OpenIddictRole"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictRole" ADD CONSTRAINT "UX_OpenIddictRole_Name" UNIQUE ("Name");
    END IF;
END$$;

COMMENT ON TABLE "dmscs"."OpenIddictRole" IS 'OpenIddict roles storage.';
COMMENT ON COLUMN "dmscs"."OpenIddictRole"."Id" IS 'Role unique identifier.';
COMMENT ON COLUMN "dmscs"."OpenIddictRole"."Name" IS 'Role name.';
COMMENT ON COLUMN "dmscs"."OpenIddictRole"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."OpenIddictRole"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."OpenIddictRole"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."OpenIddictRole"."ModifiedBy" IS 'User or client ID who last modified the record';

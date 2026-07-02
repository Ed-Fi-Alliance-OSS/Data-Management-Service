-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
CREATE TABLE IF NOT EXISTS "dmscs"."OpenIddictClientRole" (
    "ClientId" uuid NOT NULL,
    "RoleId" uuid NOT NULL,
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
        WHERE conname = 'PK_OpenIddictClientRole'
          AND conrelid = '"dmscs"."OpenIddictClientRole"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictClientRole" ADD CONSTRAINT "PK_OpenIddictClientRole" PRIMARY KEY ("ClientId", "RoleId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_OpenIddictClientRole_OpenIddictApplication'
          AND conrelid = '"dmscs"."OpenIddictClientRole"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictClientRole" ADD CONSTRAINT "FK_OpenIddictClientRole_OpenIddictApplication" FOREIGN KEY ("ClientId") REFERENCES "dmscs"."OpenIddictApplication"("Id") ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_OpenIddictClientRole_OpenIddictRole'
          AND conrelid = '"dmscs"."OpenIddictClientRole"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictClientRole" ADD CONSTRAINT "FK_OpenIddictClientRole_OpenIddictRole" FOREIGN KEY ("RoleId") REFERENCES "dmscs"."OpenIddictRole"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON TABLE "dmscs"."OpenIddictClientRole" IS 'Join table for OpenIddict clients and roles.';
COMMENT ON COLUMN "dmscs"."OpenIddictClientRole"."ClientId" IS 'Client identifier.';
COMMENT ON COLUMN "dmscs"."OpenIddictClientRole"."RoleId" IS 'Role identifier.';
COMMENT ON COLUMN "dmscs"."OpenIddictClientRole"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."OpenIddictClientRole"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."OpenIddictClientRole"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."OpenIddictClientRole"."ModifiedBy" IS 'User or client ID who last modified the record';

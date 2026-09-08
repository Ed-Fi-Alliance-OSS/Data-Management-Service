-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS "dmscs"."OpenIddictApplicationScope" (
    "ApplicationId" uuid NOT NULL,
    "ScopeId" uuid NOT NULL,
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
        WHERE conname = 'PK_OpenIddictApplicationScope'
          AND conrelid = '"dmscs"."OpenIddictApplicationScope"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictApplicationScope" ADD CONSTRAINT "PK_OpenIddictApplicationScope" PRIMARY KEY ("ApplicationId", "ScopeId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_OpenIddictApplicationScope_OpenIddictApplication'
          AND conrelid = '"dmscs"."OpenIddictApplicationScope"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictApplicationScope" ADD CONSTRAINT "FK_OpenIddictApplicationScope_OpenIddictApplication" FOREIGN KEY ("ApplicationId") REFERENCES "dmscs"."OpenIddictApplication"("Id") ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_OpenIddictApplicationScope_OpenIddictScope'
          AND conrelid = '"dmscs"."OpenIddictApplicationScope"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictApplicationScope" ADD CONSTRAINT "FK_OpenIddictApplicationScope_OpenIddictScope" FOREIGN KEY ("ScopeId") REFERENCES "dmscs"."OpenIddictScope"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON TABLE "dmscs"."OpenIddictApplicationScope" IS 'Join table for OpenIddict applications and scopes.';
COMMENT ON COLUMN "dmscs"."OpenIddictApplicationScope"."ApplicationId" IS 'Application identifier.';
COMMENT ON COLUMN "dmscs"."OpenIddictApplicationScope"."ScopeId" IS 'Scope identifier.';
COMMENT ON COLUMN "dmscs"."OpenIddictApplicationScope"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."OpenIddictApplicationScope"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."OpenIddictApplicationScope"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."OpenIddictApplicationScope"."ModifiedBy" IS 'User or client ID who last modified the record';

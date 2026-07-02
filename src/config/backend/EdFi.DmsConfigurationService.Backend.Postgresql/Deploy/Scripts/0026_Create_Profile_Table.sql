-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
-- ============================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'dmscs') THEN
        EXECUTE 'CREATE SCHEMA "dmscs"';
    END IF;
END$$;

CREATE TABLE IF NOT EXISTS "dmscs"."Profile" (
    "Id" BIGINT GENERATED ALWAYS AS IDENTITY,
    "ProfileName" VARCHAR(500) NOT NULL,
    "Definition" TEXT NOT NULL,
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
        WHERE conname = 'PK_Profile'
          AND conrelid = '"dmscs"."Profile"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Profile" ADD CONSTRAINT "PK_Profile" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_Profile_ProfileName'
          AND conrelid = '"dmscs"."Profile"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Profile" ADD CONSTRAINT "UX_Profile_ProfileName" UNIQUE ("ProfileName");
    END IF;
END$$;

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

CREATE TABLE IF NOT EXISTS "dmscs"."ApplicationProfile" (
	"ApplicationId" BIGINT NOT NULL,
	"ProfileId" BIGINT NOT NULL,
	"CreatedAt" TIMESTAMP NOT NULL DEFAULT NOW(),
	"CreatedBy" VARCHAR(256)
);

DO $$
BEGIN
	IF NOT EXISTS (
		SELECT 1
		FROM pg_constraint
		WHERE conname = 'PK_ApplicationProfile'
		  AND conrelid = '"dmscs"."ApplicationProfile"'::regclass
	) THEN
		ALTER TABLE "dmscs"."ApplicationProfile" ADD CONSTRAINT "PK_ApplicationProfile" PRIMARY KEY ("ApplicationId", "ProfileId");
	END IF;

	IF NOT EXISTS (
		SELECT 1
		FROM pg_constraint
		WHERE conname = 'FK_ApplicationProfile_Application'
		  AND conrelid = '"dmscs"."ApplicationProfile"'::regclass
	) THEN
		ALTER TABLE "dmscs"."ApplicationProfile" ADD CONSTRAINT "FK_ApplicationProfile_Application" FOREIGN KEY ("ApplicationId") REFERENCES "dmscs"."Application"("Id") ON DELETE CASCADE;
	END IF;

	IF NOT EXISTS (
		SELECT 1
		FROM pg_constraint
		WHERE conname = 'FK_ApplicationProfile_Profile'
		  AND conrelid = '"dmscs"."ApplicationProfile"'::regclass
	) THEN
		ALTER TABLE "dmscs"."ApplicationProfile" ADD CONSTRAINT "FK_ApplicationProfile_Profile" FOREIGN KEY ("ProfileId") REFERENCES "dmscs"."Profile"("Id") ON DELETE RESTRICT;
	END IF;
END$$;

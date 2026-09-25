-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1437: one row per recurring job schedule. A schedule is identified by (TenantId, ScheduleType)
-- whether or not it is enabled, so its Id stays stable across disable and re-enable. PostgreSQL treats
-- NULLs as distinct in a unique index, so the single-tenant (null TenantId) key has its own partial index.
CREATE TABLE IF NOT EXISTS "dmscs"."JobSchedule" (
    "Id" BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    "TenantId" BIGINT NULL,
    "ScheduleType" VARCHAR(100) NOT NULL,
    "JobType" VARCHAR(100) NOT NULL,
    "PayloadVersion" SMALLINT NOT NULL,
    "Payload" VARCHAR(4000) NOT NULL,
    "IntervalMinutes" INT NOT NULL,
    "Enabled" BOOLEAN NOT NULL,
    "NextRunAt" TIMESTAMP NOT NULL,
    "LastEnqueuedOccurrence" TIMESTAMP NULL,
    "LeaseOwner" VARCHAR(200) NULL,
    "LeaseExpiresAt" TIMESTAMP NULL,
    "FencingToken" BIGINT NOT NULL DEFAULT 0,
    "CreatedAt" TIMESTAMP NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
    "CreatedBy" VARCHAR(256),
    "LastModifiedAt" TIMESTAMP,
    "ModifiedBy" VARCHAR(256)
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'PK_JobSchedule'
          AND conrelid = '"dmscs"."JobSchedule"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."JobSchedule" ADD CONSTRAINT "PK_JobSchedule" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_JobSchedule_Tenant'
          AND conrelid = '"dmscs"."JobSchedule"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."JobSchedule" ADD CONSTRAINT "FK_JobSchedule_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_JobSchedule_Payload_Object'
          AND conrelid = '"dmscs"."JobSchedule"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."JobSchedule" ADD CONSTRAINT "CK_JobSchedule_Payload_Object" CHECK (jsonb_typeof(("Payload")::jsonb) = 'object');
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_JobSchedule_IntervalMinutes'
          AND conrelid = '"dmscs"."JobSchedule"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."JobSchedule" ADD CONSTRAINT "CK_JobSchedule_IntervalMinutes" CHECK ("IntervalMinutes" BETWEEN 1 AND 527040);
    END IF;
END$$;

CREATE UNIQUE INDEX IF NOT EXISTS "UX_JobSchedule_Tenant_Type" ON "dmscs"."JobSchedule" ("TenantId", "ScheduleType") WHERE "TenantId" IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_JobSchedule_SingleTenant_Type" ON "dmscs"."JobSchedule" ("ScheduleType") WHERE "TenantId" IS NULL;
CREATE INDEX IF NOT EXISTS "IX_JobSchedule_Due" ON "dmscs"."JobSchedule" ("Enabled", "NextRunAt");
CREATE INDEX IF NOT EXISTS "IX_JobSchedule_TenantId" ON "dmscs"."JobSchedule" ("TenantId");

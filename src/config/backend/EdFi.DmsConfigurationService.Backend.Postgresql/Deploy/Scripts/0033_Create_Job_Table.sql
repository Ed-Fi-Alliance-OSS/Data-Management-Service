-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1437: durable background jobs. Id is the FIFO key; JobId is the public identifier. Every active row
-- carries NextAttemptAt, its eligibility time, so a claim is an ordered walk of IX_Job_Claim. A scheduled
-- occurrence is identified by (SourceScheduleId, ScheduledOccurrence); manual jobs leave both NULL.
CREATE TABLE IF NOT EXISTS "dmscs"."Job" (
    "Id" BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    "JobId" VARCHAR(150) NOT NULL,
    "TenantId" BIGINT NULL,
    "JobType" VARCHAR(100) NOT NULL,
    "PayloadVersion" SMALLINT NOT NULL,
    "Payload" VARCHAR(4000) NOT NULL,
    "SourceScheduleId" BIGINT NULL,
    "ScheduledOccurrence" TIMESTAMP NULL,
    "Status" VARCHAR(20) NOT NULL,
    "CreatedAt" TIMESTAMP NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
    "FinishedAt" TIMESTAMP NULL,
    "NextAttemptAt" TIMESTAMP NULL,
    "LeaseExpiresAt" TIMESTAMP NULL,
    "ErrorMessage" VARCHAR(1000) NULL,
    "AttemptCount" INT NOT NULL DEFAULT 0,
    "LeaseOwner" VARCHAR(200) NULL,
    "FencingToken" BIGINT NOT NULL DEFAULT 0,
    "CreatedBy" VARCHAR(256),
    "LastModifiedAt" TIMESTAMP,
    "ModifiedBy" VARCHAR(256)
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'PK_Job' AND conrelid = '"dmscs"."Job"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Job" ADD CONSTRAINT "PK_Job" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'UX_Job_JobId' AND conrelid = '"dmscs"."Job"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Job" ADD CONSTRAINT "UX_Job_JobId" UNIQUE ("JobId");
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'FK_Job_Tenant' AND conrelid = '"dmscs"."Job"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Job" ADD CONSTRAINT "FK_Job_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'FK_Job_JobSchedule' AND conrelid = '"dmscs"."Job"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Job" ADD CONSTRAINT "FK_Job_JobSchedule" FOREIGN KEY ("SourceScheduleId") REFERENCES "dmscs"."JobSchedule"("Id") ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'CK_Job_Payload_Object' AND conrelid = '"dmscs"."Job"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Job" ADD CONSTRAINT "CK_Job_Payload_Object" CHECK (jsonb_typeof(("Payload")::jsonb) = 'object');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'CK_Job_Occurrence_Pairing' AND conrelid = '"dmscs"."Job"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Job" ADD CONSTRAINT "CK_Job_Occurrence_Pairing" CHECK (("SourceScheduleId" IS NULL) = ("ScheduledOccurrence" IS NULL));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'CK_Job_Status' AND conrelid = '"dmscs"."Job"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Job" ADD CONSTRAINT "CK_Job_Status" CHECK ("Status" IN ('Pending', 'InProgress', 'Completed', 'Error'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'CK_Job_AttemptCount' AND conrelid = '"dmscs"."Job"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Job" ADD CONSTRAINT "CK_Job_AttemptCount" CHECK ("AttemptCount" >= 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'CK_Job_NextAttemptAt_Active' AND conrelid = '"dmscs"."Job"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Job" ADD CONSTRAINT "CK_Job_NextAttemptAt_Active" CHECK ("Status" IN ('Completed', 'Error') OR "NextAttemptAt" IS NOT NULL);
    END IF;
END$$;

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Job_SourceScheduleId_ScheduledOccurrence" ON "dmscs"."Job" ("SourceScheduleId", "ScheduledOccurrence")
    WHERE "SourceScheduleId" IS NOT NULL AND "ScheduledOccurrence" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_Job_Claim" ON "dmscs"."Job" ("NextAttemptAt", "Id") WHERE "Status" IN ('Pending', 'InProgress');
CREATE INDEX IF NOT EXISTS "IX_Job_Retention" ON "dmscs"."Job" ("Status", "FinishedAt") WHERE "Status" IN ('Completed', 'Error');
CREATE INDEX IF NOT EXISTS "IX_Job_TenantId" ON "dmscs"."Job" ("TenantId");

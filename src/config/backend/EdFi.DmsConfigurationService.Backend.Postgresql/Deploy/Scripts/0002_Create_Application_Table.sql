-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS "dmscs"."Application" (
    "Id" BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    "ApplicationName" VARCHAR(256) NOT NULL,
    "VendorId" BIGINT NOT NULL,
    "ClaimSetName" VARCHAR(256) NOT NULL,
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
        WHERE conname = 'PK_Application'
          AND conrelid = '"dmscs"."Application"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Application" ADD CONSTRAINT "PK_Application" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_Application_Vendor'
          AND conrelid = '"dmscs"."Application"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Application" ADD CONSTRAINT "FK_Application_Vendor" FOREIGN KEY ("VendorId") REFERENCES "dmscs"."Vendor"("Id") ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_Application_VendorId_ApplicationName'
          AND conrelid = '"dmscs"."Application"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Application" ADD CONSTRAINT "UX_Application_VendorId_ApplicationName" UNIQUE ("VendorId", "ApplicationName");
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."Application"."Id" IS 'Application id';
COMMENT ON COLUMN "dmscs"."Application"."ApplicationName" IS 'Application name';
COMMENT ON COLUMN "dmscs"."Application"."VendorId" IS 'Vendor or company id';
COMMENT ON COLUMN "dmscs"."Application"."ClaimSetName" IS 'Claim set name';
COMMENT ON COLUMN "dmscs"."Application"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."Application"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."Application"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."Application"."ModifiedBy" IS 'User or client ID who last modified the record';

CREATE TABLE IF NOT EXISTS "dmscs"."ApplicationEducationOrganization" (
    "ApplicationId" BIGINT NOT NULL,
    "EducationOrganizationId" BIGINT NOT NULL,
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
        WHERE conname = 'PK_ApplicationEducationOrganization'
          AND conrelid = '"dmscs"."ApplicationEducationOrganization"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApplicationEducationOrganization" ADD CONSTRAINT "PK_ApplicationEducationOrganization" PRIMARY KEY ("ApplicationId", "EducationOrganizationId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ApplicationEducationOrganization_Application'
          AND conrelid = '"dmscs"."ApplicationEducationOrganization"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApplicationEducationOrganization" ADD CONSTRAINT "FK_ApplicationEducationOrganization_Application" FOREIGN KEY ("ApplicationId") REFERENCES "dmscs"."Application"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON TABLE "dmscs"."ApplicationEducationOrganization" IS 'Relationship of applications with educational organizations';
COMMENT ON COLUMN "dmscs"."ApplicationEducationOrganization"."ApplicationId" IS 'Application id';
COMMENT ON COLUMN "dmscs"."ApplicationEducationOrganization"."EducationOrganizationId" IS 'Education organization id';
COMMENT ON COLUMN "dmscs"."ApplicationEducationOrganization"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."ApplicationEducationOrganization"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."ApplicationEducationOrganization"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."ApplicationEducationOrganization"."ModifiedBy" IS 'User or client ID who last modified the record';

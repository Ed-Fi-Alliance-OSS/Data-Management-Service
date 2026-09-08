-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS "dmscs"."Tenant" (
    "Id" BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    "Name" VARCHAR(256) NOT NULL,
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
        WHERE conname = 'PK_Tenant'
          AND conrelid = '"dmscs"."Tenant"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Tenant" ADD CONSTRAINT "PK_Tenant" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_Tenant_Name'
          AND conrelid = '"dmscs"."Tenant"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."Tenant" ADD CONSTRAINT "UX_Tenant_Name" UNIQUE ("Name");
    END IF;
END$$;

COMMENT ON TABLE "dmscs"."Tenant" IS 'Tenants for multi-tenancy support';
COMMENT ON COLUMN "dmscs"."Tenant"."Id" IS 'Tenant id';
COMMENT ON COLUMN "dmscs"."Tenant"."Name" IS 'Tenant name (unique)';
COMMENT ON COLUMN "dmscs"."Tenant"."CreatedAt" IS 'Date and time tenant was created';
COMMENT ON COLUMN "dmscs"."Tenant"."CreatedBy" IS 'User or client that created the tenant';
COMMENT ON COLUMN "dmscs"."Tenant"."LastModifiedAt" IS 'Date and time tenant was last modified';
COMMENT ON COLUMN "dmscs"."Tenant"."ModifiedBy" IS 'User or client that last modified the tenant';

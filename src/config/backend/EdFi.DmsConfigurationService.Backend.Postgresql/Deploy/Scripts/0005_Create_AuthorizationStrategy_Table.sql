-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS "dmscs"."AuthorizationStrategy" (
    "Id" BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    "TenantId" BIGINT,
    "AuthorizationStrategyName" VARCHAR(255) NOT NULL,
    "DisplayName" VARCHAR(255) NOT NULL,
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
        WHERE conname = 'PK_AuthorizationStrategy'
          AND conrelid = '"dmscs"."AuthorizationStrategy"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."AuthorizationStrategy" ADD CONSTRAINT "PK_AuthorizationStrategy" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_AuthorizationStrategy_TenantId_AuthorizationStrategyName'
          AND conrelid = '"dmscs"."AuthorizationStrategy"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."AuthorizationStrategy" ADD CONSTRAINT "UX_AuthorizationStrategy_TenantId_AuthorizationStrategyName" UNIQUE NULLS NOT DISTINCT ("TenantId", "AuthorizationStrategyName");
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."AuthorizationStrategy"."Id" IS 'Authorization Strategy Identifier.';
COMMENT ON COLUMN "dmscs"."AuthorizationStrategy"."TenantId" IS 'Tenant id';
COMMENT ON COLUMN "dmscs"."AuthorizationStrategy"."AuthorizationStrategyName" IS 'Authorization Strategy Name';
COMMENT ON COLUMN "dmscs"."AuthorizationStrategy"."DisplayName" IS 'Display Name';
COMMENT ON COLUMN "dmscs"."AuthorizationStrategy"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."AuthorizationStrategy"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."AuthorizationStrategy"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."AuthorizationStrategy"."ModifiedBy" IS 'User or client ID who last modified the record';

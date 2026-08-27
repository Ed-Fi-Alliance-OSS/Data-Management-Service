-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS "dmscs"."OwnershipToken" (
    "Id" SMALLINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    "Description" VARCHAR(50) NOT NULL,
    "CreatedAt" TIMESTAMP NOT NULL DEFAULT NOW(),
    "CreatedBy" VARCHAR(256),
    "LastModifiedAt" TIMESTAMP,
    "ModifiedBy" VARCHAR(256),
    "TenantId" BIGINT NULL
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'PK_OwnershipToken'
          AND conrelid = '"dmscs"."OwnershipToken"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OwnershipToken" ADD CONSTRAINT "PK_OwnershipToken" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_OwnershipToken_Tenant'
          AND conrelid = '"dmscs"."OwnershipToken"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OwnershipToken" ADD CONSTRAINT "FK_OwnershipToken_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE RESTRICT;
    ELSIF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_OwnershipToken_Tenant'
          AND conrelid = '"dmscs"."OwnershipToken"'::regclass
          AND confdeltype <> 'r'
    ) THEN
        ALTER TABLE "dmscs"."OwnershipToken" DROP CONSTRAINT "FK_OwnershipToken_Tenant";
        ALTER TABLE "dmscs"."OwnershipToken" ADD CONSTRAINT "FK_OwnershipToken_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name = 'ApiClient'
          AND column_name = 'CreatorOwnershipTokenId'
    ) THEN
        ALTER TABLE "dmscs"."ApiClient" ADD COLUMN "CreatorOwnershipTokenId" SMALLINT NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ApiClient_CreatorOwnershipToken'
          AND conrelid = '"dmscs"."ApiClient"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClient" ADD CONSTRAINT "FK_ApiClient_CreatorOwnershipToken" FOREIGN KEY ("CreatorOwnershipTokenId") REFERENCES "dmscs"."OwnershipToken"("Id") ON DELETE RESTRICT;
    END IF;
END$$;

CREATE TABLE IF NOT EXISTS "dmscs"."ApiClientOwnershipToken" (
    "ApiClientId" INT NOT NULL,
    "OwnershipTokenId" SMALLINT NOT NULL,
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
        WHERE conname = 'PK_ApiClientOwnershipToken'
          AND conrelid = '"dmscs"."ApiClientOwnershipToken"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClientOwnershipToken" ADD CONSTRAINT "PK_ApiClientOwnershipToken" PRIMARY KEY ("ApiClientId", "OwnershipTokenId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ApiClientOwnershipToken_ApiClient'
          AND conrelid = '"dmscs"."ApiClientOwnershipToken"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClientOwnershipToken" ADD CONSTRAINT "FK_ApiClientOwnershipToken_ApiClient" FOREIGN KEY ("ApiClientId") REFERENCES "dmscs"."ApiClient"("Id") ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ApiClientOwnershipToken_OwnershipToken'
          AND conrelid = '"dmscs"."ApiClientOwnershipToken"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClientOwnershipToken" ADD CONSTRAINT "FK_ApiClientOwnershipToken_OwnershipToken" FOREIGN KEY ("OwnershipTokenId") REFERENCES "dmscs"."OwnershipToken"("Id") ON DELETE RESTRICT;
    END IF;
END$$;

CREATE INDEX IF NOT EXISTS "IX_OwnershipToken_TenantId" ON "dmscs"."OwnershipToken" ("TenantId");
CREATE INDEX IF NOT EXISTS "IX_ApiClient_CreatorOwnershipTokenId" ON "dmscs"."ApiClient" ("CreatorOwnershipTokenId");
CREATE INDEX IF NOT EXISTS "IX_ApiClientOwnershipToken_OwnershipTokenId" ON "dmscs"."ApiClientOwnershipToken" ("OwnershipTokenId");

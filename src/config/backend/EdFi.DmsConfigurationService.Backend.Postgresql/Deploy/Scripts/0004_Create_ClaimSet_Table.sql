-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS "dmscs"."ClaimSet"
(
    "Id" BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    "TenantId" BIGINT,
    "ClaimSetName" VARCHAR(256) NOT NULL,
    "IsSystemReserved" BOOLEAN NOT NULL,
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
        WHERE conname = 'PK_ClaimSet'
          AND conrelid = '"dmscs"."ClaimSet"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ClaimSet" ADD CONSTRAINT "PK_ClaimSet" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_ClaimSet_ClaimSetName'
          AND conrelid = '"dmscs"."ClaimSet"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ClaimSet" ADD CONSTRAINT "UX_ClaimSet_ClaimSetName" UNIQUE ("ClaimSetName");
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."ClaimSet"."Id"
    IS 'ClaimSet id';

COMMENT ON COLUMN "dmscs"."ClaimSet"."TenantId"
    IS 'Tenant id';

COMMENT ON COLUMN "dmscs"."ClaimSet"."ClaimSetName"
    IS 'Claim set name and must be unique';

COMMENT ON COLUMN "dmscs"."ClaimSet"."IsSystemReserved"
    IS 'Is system reserved';

COMMENT ON COLUMN "dmscs"."ClaimSet"."CreatedAt"
    IS 'Timestamp when the record was created (UTC)';

COMMENT ON COLUMN "dmscs"."ClaimSet"."CreatedBy"
    IS 'User or client ID who created the record';

COMMENT ON COLUMN "dmscs"."ClaimSet"."LastModifiedAt"
    IS 'Timestamp when the record was last modified (UTC)';

COMMENT ON COLUMN "dmscs"."ClaimSet"."ModifiedBy"
    IS 'User or client ID who last modified the record';

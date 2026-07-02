-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS "dmscs"."DataStore" (
    "Id" BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    "DataStoreType" VARCHAR(50) NOT NULL,
    "Name" VARCHAR(256) NOT NULL,
    "ConnectionString" BYTEA,
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
        WHERE conname = 'PK_DataStore'
          AND conrelid = '"dmscs"."DataStore"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."DataStore" ADD CONSTRAINT "PK_DataStore" PRIMARY KEY ("Id");
    END IF;
END$$;

COMMENT ON COLUMN "dmscs"."DataStore"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."DataStore"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."DataStore"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."DataStore"."ModifiedBy" IS 'User or client ID who last modified the record';

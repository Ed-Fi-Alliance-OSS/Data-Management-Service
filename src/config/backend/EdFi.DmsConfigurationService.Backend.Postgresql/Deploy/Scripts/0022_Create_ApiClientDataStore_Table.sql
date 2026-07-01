-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS "dmscs"."ApiClientDataStore" (
    "ApiClientId" BIGINT NOT NULL,
    "DataStoreId" BIGINT NOT NULL,
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
        WHERE conname = 'PK_ApiClientDataStore'
          AND conrelid = '"dmscs"."ApiClientDataStore"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClientDataStore" ADD CONSTRAINT "PK_ApiClientDataStore" PRIMARY KEY ("ApiClientId", "DataStoreId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ApiClientDataStore_ApiClient'
          AND conrelid = '"dmscs"."ApiClientDataStore"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClientDataStore" ADD CONSTRAINT "FK_ApiClientDataStore_ApiClient" FOREIGN KEY ("ApiClientId") REFERENCES "dmscs"."ApiClient"("Id") ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_ApiClientDataStore_DataStore'
          AND conrelid = '"dmscs"."ApiClientDataStore"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClientDataStore" ADD CONSTRAINT "FK_ApiClientDataStore_DataStore" FOREIGN KEY ("DataStoreId") REFERENCES "dmscs"."DataStore"("Id") ON DELETE CASCADE;
    END IF;
END$$;

COMMENT ON TABLE "dmscs"."ApiClientDataStore" IS 'Relationship of API clients with data stores';
COMMENT ON COLUMN "dmscs"."ApiClientDataStore"."ApiClientId" IS 'API client id';
COMMENT ON COLUMN "dmscs"."ApiClientDataStore"."DataStoreId" IS 'Data store id';
COMMENT ON COLUMN "dmscs"."ApiClientDataStore"."CreatedAt" IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN "dmscs"."ApiClientDataStore"."CreatedBy" IS 'User or client ID who created the record';
COMMENT ON COLUMN "dmscs"."ApiClientDataStore"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN "dmscs"."ApiClientDataStore"."ModifiedBy" IS 'User or client ID who last modified the record';

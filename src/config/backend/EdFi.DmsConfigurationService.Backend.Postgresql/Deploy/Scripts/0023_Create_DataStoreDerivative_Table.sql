-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create DataStoreDerivative table for storing read replicas and snapshots
-- of data stores with encrypted connection strings

CREATE TABLE IF NOT EXISTS "dmscs"."DataStoreDerivative" (
    "Id" BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    "DataStoreId" BIGINT NOT NULL,
    "DerivativeType" VARCHAR(50) NOT NULL,
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
        WHERE conname = 'PK_DataStoreDerivative'
          AND conrelid = '"dmscs"."DataStoreDerivative"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."DataStoreDerivative" ADD CONSTRAINT "PK_DataStoreDerivative" PRIMARY KEY ("Id");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_DataStoreDerivative_DataStore'
          AND conrelid = '"dmscs"."DataStoreDerivative"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."DataStoreDerivative" ADD CONSTRAINT "FK_DataStoreDerivative_DataStore" FOREIGN KEY ("DataStoreId") REFERENCES "dmscs"."DataStore"("Id") ON DELETE CASCADE;
    END IF;
END$$;

-- Add table comment for documentation
COMMENT ON TABLE "dmscs"."DataStoreDerivative" IS
    'Stores derivative data stores (read replicas, snapshots) associated with a data store';

-- Add column comments
COMMENT ON COLUMN "dmscs"."DataStoreDerivative"."Id" IS
    'Unique identifier for the derivative data store';

COMMENT ON COLUMN "dmscs"."DataStoreDerivative"."DataStoreId" IS
    'Foreign key reference to the parent data store';

COMMENT ON COLUMN "dmscs"."DataStoreDerivative"."DerivativeType" IS
    'Type of derivative: "ReadReplica" or "Snapshot"';

COMMENT ON COLUMN "dmscs"."DataStoreDerivative"."ConnectionString" IS
    'Encrypted connection string for the derivative data store (BYTEA encrypted with AES)';

COMMENT ON COLUMN "dmscs"."DataStoreDerivative"."CreatedAt" IS 'Timestamp when the record was created (UTC)';

COMMENT ON COLUMN "dmscs"."DataStoreDerivative"."CreatedBy" IS 'User or client ID who created the record';

COMMENT ON COLUMN "dmscs"."DataStoreDerivative"."LastModifiedAt" IS 'Timestamp when the record was last modified (UTC)';

COMMENT ON COLUMN "dmscs"."DataStoreDerivative"."ModifiedBy" IS 'User or client ID who last modified the record';

-- Create index for querying by parent data store
CREATE INDEX IF NOT EXISTS "IX_DataStoreDerivative_DataStoreId"
    ON "dmscs"."DataStoreDerivative" ("DataStoreId");

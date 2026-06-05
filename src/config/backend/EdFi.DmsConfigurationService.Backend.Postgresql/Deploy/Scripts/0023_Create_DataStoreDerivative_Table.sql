-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create DataStoreDerivative table for storing read replicas and snapshots
-- of data stores with encrypted connection strings

CREATE TABLE IF NOT EXISTS dmscs.DataStoreDerivative (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    DataStoreId BIGINT NOT NULL,
    DerivativeType VARCHAR(50) NOT NULL,
    ConnectionString BYTEA,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT fk_datastorederivative_datastore
        FOREIGN KEY (DataStoreId)
        REFERENCES dmscs.DataStore(Id)
        ON DELETE CASCADE
);

-- Add table comment for documentation
COMMENT ON TABLE dmscs.DataStoreDerivative IS
    'Stores derivative data stores (read replicas, snapshots) associated with a data store';

-- Add column comments
COMMENT ON COLUMN dmscs.DataStoreDerivative.Id IS
    'Unique identifier for the derivative data store';

COMMENT ON COLUMN dmscs.DataStoreDerivative.DataStoreId IS
    'Foreign key reference to the parent data store';

COMMENT ON COLUMN dmscs.DataStoreDerivative.DerivativeType IS
    'Type of derivative: "ReadReplica" or "Snapshot"';

COMMENT ON COLUMN dmscs.DataStoreDerivative.ConnectionString IS
    'Encrypted connection string for the derivative data store (BYTEA encrypted with AES)';

COMMENT ON COLUMN dmscs.DataStoreDerivative.CreatedAt IS 'Timestamp when the record was created (UTC)';

COMMENT ON COLUMN dmscs.DataStoreDerivative.CreatedBy IS 'User or client ID who created the record';

COMMENT ON COLUMN dmscs.DataStoreDerivative.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';

COMMENT ON COLUMN dmscs.DataStoreDerivative.ModifiedBy IS 'User or client ID who last modified the record';

-- Create index for querying by parent data store
CREATE INDEX IF NOT EXISTS idx_datastorederivative_datastoreid
    ON dmscs.DataStoreDerivative (DataStoreId);

-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create DmsInstanceDerivative table for storing read replicas and snapshots
-- of DMS instances with encrypted connection strings

CREATE TABLE IF NOT EXISTS dmscs.DmsInstanceDerivative (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    InstanceId BIGINT NOT NULL,
    DerivativeType VARCHAR(50) NOT NULL,
    ConnectionString BYTEA,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT fk_dmsinstancederivative_instance
        FOREIGN KEY (InstanceId)
        REFERENCES dmscs.DmsInstance(Id)
        ON DELETE CASCADE
);

-- Add table comment for documentation
COMMENT ON TABLE dmscs.DmsInstanceDerivative IS
    'Stores derivative instances (read replicas, snapshots) associated with a DMS instance';

-- Add column comments
COMMENT ON COLUMN dmscs.DmsInstanceDerivative.Id IS
    'Unique identifier for the derivative instance';

COMMENT ON COLUMN dmscs.DmsInstanceDerivative.InstanceId IS
    'Foreign key reference to the parent DMS instance';

COMMENT ON COLUMN dmscs.DmsInstanceDerivative.DerivativeType IS
    'Type of derivative: "ReadReplica" or "Snapshot"';

COMMENT ON COLUMN dmscs.DmsInstanceDerivative.ConnectionString IS
    'Encrypted connection string for the derivative instance (BYTEA encrypted with AES)';

COMMENT ON COLUMN dmscs.DmsInstanceDerivative.CreatedAt IS 'Timestamp when the record was created (UTC)';

COMMENT ON COLUMN dmscs.DmsInstanceDerivative.CreatedBy IS 'User or client ID who created the record';

COMMENT ON COLUMN dmscs.DmsInstanceDerivative.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';

COMMENT ON COLUMN dmscs.DmsInstanceDerivative.ModifiedBy IS 'User or client ID who last modified the record';

-- Create index for querying by parent instance
CREATE INDEX IF NOT EXISTS idx_dmsinstancederivative_instanceid
    ON dmscs.DmsInstanceDerivative (InstanceId);

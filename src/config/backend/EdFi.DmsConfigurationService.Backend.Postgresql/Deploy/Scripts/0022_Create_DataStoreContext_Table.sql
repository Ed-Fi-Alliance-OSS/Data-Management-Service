-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.DataStoreContext (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    DataStoreId BIGINT NOT NULL,
    ContextKey VARCHAR(256) NOT NULL,
    ContextValue VARCHAR(256) NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT fk_datastorecontext_datastore FOREIGN KEY (DataStoreId) REFERENCES dmscs.DataStore(Id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_datastore_context_unique ON dmscs.DataStoreContext (DataStoreId, ContextKey);

COMMENT ON TABLE dmscs.DataStoreContext IS 'Route context information for data stores to support context-based routing (e.g., year-specific, district-specific deployments)';
COMMENT ON COLUMN dmscs.DataStoreContext.Id IS 'Data store context id';
COMMENT ON COLUMN dmscs.DataStoreContext.DataStoreId IS 'Data store id this context belongs to';
COMMENT ON COLUMN dmscs.DataStoreContext.ContextKey IS 'Context key for routing (e.g., schoolYear, districtId)';
COMMENT ON COLUMN dmscs.DataStoreContext.ContextValue IS 'Context value for routing (e.g., 2024, 255901)';
COMMENT ON COLUMN dmscs.DataStoreContext.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.DataStoreContext.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.DataStoreContext.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.DataStoreContext.ModifiedBy IS 'User or client ID who last modified the record';

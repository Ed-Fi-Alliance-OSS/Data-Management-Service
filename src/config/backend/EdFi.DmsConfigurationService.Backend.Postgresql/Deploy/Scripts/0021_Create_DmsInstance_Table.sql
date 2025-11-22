-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.DmsInstance (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    InstanceType VARCHAR(50) NOT NULL,
    InstanceName VARCHAR(256) NOT NULL,
    ConnectionString BYTEA,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256)
);

COMMENT ON COLUMN dmscs.DmsInstance.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.DmsInstance.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.DmsInstance.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.DmsInstance.ModifiedBy IS 'User or client ID who last modified the record';

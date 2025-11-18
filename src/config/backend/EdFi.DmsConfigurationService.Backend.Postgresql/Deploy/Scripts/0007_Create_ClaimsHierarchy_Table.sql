-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.ClaimsHierarchy (
  Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
  Hierarchy JSONB NOT NULL,
  LastModifiedDate TIMESTAMP NOT NULL DEFAULT NOW(),
  CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
  CreatedBy VARCHAR(256),
  LastModifiedAt TIMESTAMP,
  ModifiedBy VARCHAR(256)
);

COMMENT ON COLUMN dmscs.ClaimsHierarchy.Id IS 'Claims hierarchy internal identifier.';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.Hierarchy IS 'Contains the JSON representation of the hierarchy of resource claims defined in the model.';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.LastModifiedDate IS 'Timestamp when the record was last modified (legacy column for backward compatibility)';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.ModifiedBy IS 'User or client ID who last modified the record';

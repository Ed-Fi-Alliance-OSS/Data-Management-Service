-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dmscs.ClaimsHierarchy (
  Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
  Hierarchy JSONB NOT NULL,
  LastModifiedDate TIMESTAMP NOT NULL DEFAULT NOW()
);

COMMENT ON COLUMN dmscs.ClaimsHierarchy.Id IS 'Claims hierarchy internal identifier.';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.Hierarchy IS 'Contains the JSON representation of the hierarchy of resource claims defined in the model.';

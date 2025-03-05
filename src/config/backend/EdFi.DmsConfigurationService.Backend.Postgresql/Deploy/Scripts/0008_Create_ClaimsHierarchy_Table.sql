-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dmscs.ClaimsHierarchy (
  Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
  ProjectName VARCHAR(20) NOT NULL,
  Version VARCHAR(10) NOT NULL,
  Hierarchy JSONB NOT NULL
);

CREATE UNIQUE INDEX idx_ProjectName_Version ON dmscs.ClaimsHierarchy USING btree (ProjectName, Version);
COMMENT ON COLUMN dmscs.ClaimsHierarchy.Id IS 'Claims hierarchy internal identifier.';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.ProjectName IS 'Project name, also known as the schema, of the model represented by the hierarchy.';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.Version IS 'The version associated with the project/schema (e.g. 1.0.0, 1.1.0, 4.0.0, 5.2.0, etc.).';
COMMENT ON COLUMN dmscs.ClaimsHierarchy.Hierarchy IS 'Contains the JSON representation of the hierarchy of resource claims defined in the model.';

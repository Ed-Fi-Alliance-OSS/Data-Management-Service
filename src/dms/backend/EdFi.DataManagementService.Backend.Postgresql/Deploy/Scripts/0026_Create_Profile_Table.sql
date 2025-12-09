-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create Profile table for storing API Profile definitions
CREATE TABLE IF NOT EXISTS dms.Profile (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  ProfileName VARCHAR(256) NOT NULL UNIQUE,
  Description TEXT NULL,
  ProfileDefinition XML NOT NULL,
  CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
  UpdatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
  PRIMARY KEY (Id)
);

-- Create index on ProfileName for faster lookups
CREATE INDEX IF NOT EXISTS IX_Profile_ProfileName ON dms.Profile (ProfileName);

-- Create index on UpdatedAt for cache invalidation queries
CREATE INDEX IF NOT EXISTS IX_Profile_UpdatedAt ON dms.Profile (UpdatedAt);

-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.ApiClient (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    ApplicationId BIGINT NOT NULL,
    Name VARCHAR(50) NOT NULL,
    IsApproved BOOLEAN NOT NULL DEFAULT false,
    ClientId VARCHAR(36) NOT NULL,
    ClientUuid UUID NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT fk_apiclient_application FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(Id) ON DELETE CASCADE
);

COMMENT ON COLUMN dmscs.ApiClient.Id IS 'ApiClient id';
COMMENT ON COLUMN dmscs.ApiClient.ApplicationId IS 'Application Id';
COMMENT ON COLUMN dmscs.ApiClient.ClientUuid IS 'Unique identifier of ApiClient';
COMMENT ON COLUMN dmscs.ApiClient.Name IS 'Name of the API client';
COMMENT ON COLUMN dmscs.ApiClient.IsApproved IS 'Indicates whether the API client is approved';
COMMENT ON COLUMN dmscs.ApiClient.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.ApiClient.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.ApiClient.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.ApiClient.ModifiedBy IS 'User or client ID who last modified the record';

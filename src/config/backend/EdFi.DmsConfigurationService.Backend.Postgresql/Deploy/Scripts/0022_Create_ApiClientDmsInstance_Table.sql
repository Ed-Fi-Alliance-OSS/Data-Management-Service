-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.ApiClientDmsInstance (
    ApiClientId BIGINT NOT NULL,
    DmsInstanceId BIGINT NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT pk_apiClientDmsInstance PRIMARY KEY (ApiClientId, DmsInstanceId),
    CONSTRAINT fk_apiclient FOREIGN KEY (ApiClientId) REFERENCES dmscs.ApiClient(Id) ON DELETE CASCADE,
    CONSTRAINT fk_dmsinstance FOREIGN KEY (DmsInstanceId) REFERENCES dmscs.DmsInstance(Id) ON DELETE CASCADE
);

COMMENT ON TABLE dmscs.ApiClientDmsInstance IS 'Relationship of API clients with DMS instances';
COMMENT ON COLUMN dmscs.ApiClientDmsInstance.ApiClientId IS 'API client id';
COMMENT ON COLUMN dmscs.ApiClientDmsInstance.DmsInstanceId IS 'DMS instance id';
COMMENT ON COLUMN dmscs.ApiClientDmsInstance.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.ApiClientDmsInstance.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.ApiClientDmsInstance.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.ApiClientDmsInstance.ModifiedBy IS 'User or client ID who last modified the record';

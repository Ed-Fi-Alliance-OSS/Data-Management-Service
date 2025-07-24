-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.ApiClient (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    ApplicationId BIGINT NOT NULL,
    ClientId VARCHAR(36) NOT NULL,
    ClientUuid UUID NOT NULL,
    CONSTRAINT fk_apiclient_application FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(Id) ON DELETE CASCADE
);

COMMENT ON COLUMN dmscs.ApiClient.Id IS 'ApiClient id';
COMMENT ON COLUMN dmscs.ApiClient.ApplicationId IS 'Application Id';
COMMENT ON COLUMN dmscs.ApiClient.ClientUuid IS 'Unique identifier of ApiClient';

-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.ApiClientDataStore', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.ApiClientDataStore (
        ApiClientId BIGINT NOT NULL,
        DataStoreId BIGINT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT pk_apiclientdatastore PRIMARY KEY (ApiClientId, DataStoreId),
        CONSTRAINT fk_apiclient FOREIGN KEY (ApiClientId) REFERENCES dmscs.ApiClient(Id) ON DELETE CASCADE,
        CONSTRAINT fk_datastore FOREIGN KEY (DataStoreId) REFERENCES dmscs.DataStore(Id) ON DELETE CASCADE
    );
END;

-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.ApiClient', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.ApiClient (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        ApplicationId BIGINT NOT NULL,
        Name NVARCHAR(50) NOT NULL,
        IsApproved BIT NOT NULL DEFAULT 0,
        ClientId NVARCHAR(36) NOT NULL,
        ClientUuid UNIQUEIDENTIFIER NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT fk_apiclient_application FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(Id) ON DELETE CASCADE
    );
END;

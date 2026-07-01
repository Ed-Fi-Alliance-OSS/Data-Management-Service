-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.OpenIddictApplication', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OpenIddictApplication (
        Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        ClientId NVARCHAR(100) NOT NULL CONSTRAINT uq_openiddictapplication_clientid UNIQUE,
        ClientSecret NVARCHAR(256),
        DisplayName NVARCHAR(200),
        RedirectUris NVARCHAR(MAX),
        PostLogoutRedirectUris NVARCHAR(MAX),
        Permissions NVARCHAR(MAX),
        Requirements NVARCHAR(MAX),
        Type NVARCHAR(50),
        CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        ProtocolMappers NVARCHAR(MAX)
    );
END;

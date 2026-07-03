-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.OpenIddictScope', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OpenIddictScope (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OpenIddictScope PRIMARY KEY,
        Name NVARCHAR(100) NOT NULL CONSTRAINT UX_OpenIddictScope_Name UNIQUE,
        Description NVARCHAR(200),
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

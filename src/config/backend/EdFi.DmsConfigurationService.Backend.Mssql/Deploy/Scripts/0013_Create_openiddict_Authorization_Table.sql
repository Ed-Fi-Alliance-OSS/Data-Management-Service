-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.OpenIddictAuthorization', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OpenIddictAuthorization (
        Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        ApplicationId UNIQUEIDENTIFIER NOT NULL,
        Subject NVARCHAR(100) NOT NULL,
        Status NVARCHAR(50) NOT NULL,
        Type NVARCHAR(50) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

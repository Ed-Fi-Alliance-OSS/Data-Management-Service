-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.ClaimsHierarchy', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.ClaimsHierarchy (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        Hierarchy NVARCHAR(MAX) NOT NULL,
        LastModifiedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

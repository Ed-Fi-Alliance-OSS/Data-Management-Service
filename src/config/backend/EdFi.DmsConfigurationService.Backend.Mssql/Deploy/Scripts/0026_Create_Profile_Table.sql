-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'dmscs')
    EXEC('CREATE SCHEMA dmscs');
GO

IF OBJECT_ID('dmscs.Profile', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.Profile (
        Id BIGINT IDENTITY(1,1) CONSTRAINT PK_Profile PRIMARY KEY,
        ProfileName NVARCHAR(500) NOT NULL,
        Definition NVARCHAR(MAX) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT UX_Profile_ProfileName UNIQUE (ProfileName)
    );
END;

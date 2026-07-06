-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'dmscs')
    EXEC('CREATE SCHEMA dmscs');
GO

IF OBJECT_ID('dmscs.ApplicationProfile', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.ApplicationProfile (
        ApplicationId BIGINT NOT NULL,
        ProfileId BIGINT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        CONSTRAINT PK_ApplicationProfile PRIMARY KEY (ApplicationId, ProfileId),
        CONSTRAINT FK_ApplicationProfile_Application FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(Id) ON DELETE CASCADE,
        CONSTRAINT FK_ApplicationProfile_Profile FOREIGN KEY (ProfileId) REFERENCES dmscs.Profile(Id)
    );
END;

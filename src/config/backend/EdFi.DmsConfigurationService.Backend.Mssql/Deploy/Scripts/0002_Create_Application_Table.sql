-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.Application', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.Application (
        Id BIGINT IDENTITY(1,1) CONSTRAINT PK_Application PRIMARY KEY,
        ApplicationName NVARCHAR(256) NOT NULL,
        VendorId BIGINT NOT NULL,
        ClaimSetName NVARCHAR(256) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT FK_Application_Vendor FOREIGN KEY (VendorId) REFERENCES dmscs.Vendor(Id) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'UX_Application_VendorId_ApplicationName' AND parent_object_id = OBJECT_ID('dmscs.Application'))
    ALTER TABLE dmscs.Application ADD CONSTRAINT UX_Application_VendorId_ApplicationName UNIQUE (VendorId, ApplicationName);

IF OBJECT_ID('dmscs.ApplicationEducationOrganization', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.ApplicationEducationOrganization (
        ApplicationId BIGINT NOT NULL,
        EducationOrganizationId BIGINT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT PK_ApplicationEducationOrganization PRIMARY KEY (ApplicationId, EducationOrganizationId),
        CONSTRAINT FK_ApplicationEducationOrganization_Application FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(Id) ON DELETE CASCADE
    );
END;

-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.Application', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.Application (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        ApplicationName NVARCHAR(256) NOT NULL,
        VendorId BIGINT NOT NULL,
        ClaimSetName NVARCHAR(256) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT fk_vendor FOREIGN KEY (VendorId) REFERENCES dmscs.Vendor(Id) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_vendor_applicationname' AND object_id = OBJECT_ID('dmscs.Application'))
    CREATE UNIQUE INDEX idx_vendor_applicationname ON dmscs.Application (VendorId, ApplicationName);

IF OBJECT_ID('dmscs.ApplicationEducationOrganization', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.ApplicationEducationOrganization (
        ApplicationId BIGINT NOT NULL,
        EducationOrganizationId BIGINT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT pk_applicationeducationorganization PRIMARY KEY (ApplicationId, EducationOrganizationId),
        CONSTRAINT fk_application_educationorganization FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(Id) ON DELETE CASCADE
    );
END;

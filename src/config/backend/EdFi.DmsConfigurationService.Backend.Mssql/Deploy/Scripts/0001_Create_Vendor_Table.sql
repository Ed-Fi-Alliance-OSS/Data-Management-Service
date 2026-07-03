-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.Vendor', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.Vendor (
        Id BIGINT IDENTITY(1,1) CONSTRAINT PK_Vendor PRIMARY KEY,
        Company NVARCHAR(256) NOT NULL,
        ContactName NVARCHAR(128),
        ContactEmailAddress NVARCHAR(320),
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'UX_Vendor_Company' AND parent_object_id = OBJECT_ID('dmscs.Vendor'))
    ALTER TABLE dmscs.Vendor ADD CONSTRAINT UX_Vendor_Company UNIQUE (Company);

IF OBJECT_ID('dmscs.VendorNamespacePrefix', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.VendorNamespacePrefix (
        VendorId BIGINT NOT NULL,
        NamespacePrefix NVARCHAR(128) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT PK_VendorNamespacePrefix PRIMARY KEY (VendorId, NamespacePrefix),
        CONSTRAINT FK_VendorNamespacePrefix_Vendor FOREIGN KEY (VendorId) REFERENCES dmscs.Vendor(Id) ON DELETE CASCADE
    );
END;

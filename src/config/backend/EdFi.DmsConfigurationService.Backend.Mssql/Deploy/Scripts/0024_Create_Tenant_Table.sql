-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.Tenant', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.Tenant (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        Name NVARCHAR(256) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'uq_tenant_name' AND parent_object_id = OBJECT_ID('dmscs.Tenant'))
    ALTER TABLE dmscs.Tenant ADD CONSTRAINT uq_tenant_name UNIQUE (Name);

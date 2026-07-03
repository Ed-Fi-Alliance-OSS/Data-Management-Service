-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.ResourceClaim', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.ResourceClaim (
        Id BIGINT IDENTITY(1,1) CONSTRAINT PK_ResourceClaim PRIMARY KEY,
        ResourceName NVARCHAR(255) NOT NULL,
        ClaimName NVARCHAR(255) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'UX_ResourceClaim_ClaimName' AND parent_object_id = OBJECT_ID('dmscs.ResourceClaim'))
    ALTER TABLE dmscs.ResourceClaim ADD CONSTRAINT UX_ResourceClaim_ClaimName UNIQUE (ClaimName);

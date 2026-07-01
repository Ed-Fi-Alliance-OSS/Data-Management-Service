-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.ClaimSet', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.ClaimSet (
        Id BIGINT IDENTITY(1,1) NOT NULL,
        ClaimSetName NVARCHAR(256) NOT NULL,
        IsSystemReserved BIT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT claimset_pkey PRIMARY KEY (Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_claimsetname' AND object_id = OBJECT_ID('dmscs.ClaimSet'))
    CREATE UNIQUE INDEX idx_claimsetname ON dmscs.ClaimSet (ClaimSetName);

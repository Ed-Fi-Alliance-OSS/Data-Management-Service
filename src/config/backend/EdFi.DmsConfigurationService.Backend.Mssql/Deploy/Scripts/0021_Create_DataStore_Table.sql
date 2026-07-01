-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.DataStore', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.DataStore (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        DataStoreType NVARCHAR(50) NOT NULL,
        Name NVARCHAR(256) NOT NULL,
        ConnectionString VARBINARY(MAX),
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

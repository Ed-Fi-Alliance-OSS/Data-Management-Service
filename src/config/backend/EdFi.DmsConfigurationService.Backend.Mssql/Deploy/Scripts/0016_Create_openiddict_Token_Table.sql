-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.OpenIddictToken', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OpenIddictToken (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OpenIddictToken PRIMARY KEY,
        ApplicationId UNIQUEIDENTIFIER,
        AuthorizationId UNIQUEIDENTIFIER,
        CreationDate DATETIME2 DEFAULT SYSUTCDATETIME(),
        Payload NVARCHAR(MAX),
        Properties NVARCHAR(200),
        RedemptionDate DATETIME2,
        Subject NVARCHAR(100),
        Type NVARCHAR(50),
        ReferenceId NVARCHAR(100),
        ExpirationDate DATETIME2,
        Status NVARCHAR(50) DEFAULT 'valid',
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OpenIddictToken_ApplicationId' AND object_id = OBJECT_ID('dmscs.OpenIddictToken'))
    CREATE INDEX IX_OpenIddictToken_ApplicationId ON dmscs.OpenIddictToken (ApplicationId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OpenIddictToken_Subject' AND object_id = OBJECT_ID('dmscs.OpenIddictToken'))
    CREATE INDEX IX_OpenIddictToken_Subject ON dmscs.OpenIddictToken (Subject);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OpenIddictToken_ReferenceId' AND object_id = OBJECT_ID('dmscs.OpenIddictToken'))
    CREATE INDEX IX_OpenIddictToken_ReferenceId ON dmscs.OpenIddictToken (ReferenceId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OpenIddictToken_ExpirationDate' AND object_id = OBJECT_ID('dmscs.OpenIddictToken'))
    CREATE INDEX IX_OpenIddictToken_ExpirationDate ON dmscs.OpenIddictToken (ExpirationDate);

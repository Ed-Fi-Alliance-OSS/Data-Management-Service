-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.OpenIddictToken', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OpenIddictToken (
        Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_openiddicttoken_applicationid' AND object_id = OBJECT_ID('dmscs.OpenIddictToken'))
    CREATE INDEX idx_openiddicttoken_applicationid ON dmscs.OpenIddictToken (ApplicationId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_openiddicttoken_subject' AND object_id = OBJECT_ID('dmscs.OpenIddictToken'))
    CREATE INDEX idx_openiddicttoken_subject ON dmscs.OpenIddictToken (Subject);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_openiddicttoken_referenceid' AND object_id = OBJECT_ID('dmscs.OpenIddictToken'))
    CREATE INDEX idx_openiddicttoken_referenceid ON dmscs.OpenIddictToken (ReferenceId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_openiddicttoken_expirationdate' AND object_id = OBJECT_ID('dmscs.OpenIddictToken'))
    CREATE INDEX idx_openiddicttoken_expirationdate ON dmscs.OpenIddictToken (ExpirationDate);

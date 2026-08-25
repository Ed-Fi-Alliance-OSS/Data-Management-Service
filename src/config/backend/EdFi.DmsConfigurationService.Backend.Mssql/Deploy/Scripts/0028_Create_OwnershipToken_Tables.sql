-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.OwnershipToken', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OwnershipToken (
        Id SMALLINT IDENTITY(1,1) NOT NULL,
        Description NVARCHAR(50) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        TenantId BIGINT NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_OwnershipToken' AND parent_object_id = OBJECT_ID('dmscs.OwnershipToken'))
BEGIN
    ALTER TABLE dmscs.OwnershipToken ADD CONSTRAINT PK_OwnershipToken PRIMARY KEY (Id);
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_OwnershipToken_Description_NotBlank' AND parent_object_id = OBJECT_ID('dmscs.OwnershipToken'))
BEGIN
    ALTER TABLE dmscs.OwnershipToken ADD CONSTRAINT CK_OwnershipToken_Description_NotBlank CHECK (
        LEN(
            REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(Description, N' ', N''), NCHAR(9), N''), NCHAR(10), N''), NCHAR(11), N''), NCHAR(12), N''), NCHAR(13), N'')
        ) > 0
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_OwnershipToken_Tenant')
BEGIN
    ALTER TABLE dmscs.OwnershipToken ADD CONSTRAINT FK_OwnershipToken_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF COL_LENGTH('dmscs.ApiClient', 'CreatorOwnershipTokenId') IS NULL
BEGIN
    ALTER TABLE dmscs.ApiClient ADD CreatorOwnershipTokenId SMALLINT NULL;
END;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ApiClient_CreatorOwnershipToken')
BEGIN
    ALTER TABLE dmscs.ApiClient ADD CONSTRAINT FK_ApiClient_CreatorOwnershipToken FOREIGN KEY (CreatorOwnershipTokenId) REFERENCES dmscs.OwnershipToken(Id) ON DELETE NO ACTION;
END;

IF OBJECT_ID('dmscs.ApiClientOwnershipToken', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.ApiClientOwnershipToken (
        ApiClientId INT NOT NULL,
        OwnershipTokenId SMALLINT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_ApiClientOwnershipToken' AND parent_object_id = OBJECT_ID('dmscs.ApiClientOwnershipToken'))
BEGIN
    ALTER TABLE dmscs.ApiClientOwnershipToken ADD CONSTRAINT PK_ApiClientOwnershipToken PRIMARY KEY (ApiClientId, OwnershipTokenId);
END;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ApiClientOwnershipToken_ApiClient')
BEGIN
    ALTER TABLE dmscs.ApiClientOwnershipToken ADD CONSTRAINT FK_ApiClientOwnershipToken_ApiClient FOREIGN KEY (ApiClientId) REFERENCES dmscs.ApiClient(Id) ON DELETE CASCADE;
END;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ApiClientOwnershipToken_OwnershipToken')
BEGIN
    ALTER TABLE dmscs.ApiClientOwnershipToken ADD CONSTRAINT FK_ApiClientOwnershipToken_OwnershipToken FOREIGN KEY (OwnershipTokenId) REFERENCES dmscs.OwnershipToken(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OwnershipToken_TenantId' AND object_id = OBJECT_ID('dmscs.OwnershipToken'))
    CREATE INDEX IX_OwnershipToken_TenantId ON dmscs.OwnershipToken (TenantId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ApiClient_CreatorOwnershipTokenId' AND object_id = OBJECT_ID('dmscs.ApiClient'))
    CREATE INDEX IX_ApiClient_CreatorOwnershipTokenId ON dmscs.ApiClient (CreatorOwnershipTokenId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ApiClientOwnershipToken_OwnershipTokenId' AND object_id = OBJECT_ID('dmscs.ApiClientOwnershipToken'))
    CREATE INDEX IX_ApiClientOwnershipToken_OwnershipTokenId ON dmscs.ApiClientOwnershipToken (OwnershipTokenId);

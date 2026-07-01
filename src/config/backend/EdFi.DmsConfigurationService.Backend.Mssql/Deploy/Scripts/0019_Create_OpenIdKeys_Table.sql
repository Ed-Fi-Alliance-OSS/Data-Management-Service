-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.OpenIddictKey', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OpenIddictKey (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        KeyId NVARCHAR(64) NOT NULL,
        PublicKey VARBINARY(MAX) NOT NULL,
        PrivateKey VARBINARY(MAX) NOT NULL, -- ENCRYPTBYPASSPHRASE ciphertext
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        ExpiresAt DATETIME2,
        IsActive BIT NOT NULL DEFAULT 1
    );
END;

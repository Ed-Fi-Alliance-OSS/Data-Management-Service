-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.OpenIddictClientRole', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OpenIddictClientRole (
        ClientId UNIQUEIDENTIFIER NOT NULL,
        RoleId UNIQUEIDENTIFIER NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        PRIMARY KEY (ClientId, RoleId),
        CONSTRAINT fk_openiddictclientrole_client FOREIGN KEY (ClientId) REFERENCES dmscs.OpenIddictApplication(Id) ON DELETE CASCADE,
        CONSTRAINT fk_openiddictclientrole_role FOREIGN KEY (RoleId) REFERENCES dmscs.OpenIddictRole(Id) ON DELETE CASCADE
    );
END;

-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.OpenIddictApplicationScope', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OpenIddictApplicationScope (
        ApplicationId UNIQUEIDENTIFIER NOT NULL,
        ScopeId UNIQUEIDENTIFIER NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT PK_OpenIddictApplicationScope PRIMARY KEY (ApplicationId, ScopeId),
        CONSTRAINT FK_OpenIddictApplicationScope_OpenIddictApplication FOREIGN KEY (ApplicationId) REFERENCES dmscs.OpenIddictApplication(Id) ON DELETE CASCADE,
        CONSTRAINT FK_OpenIddictApplicationScope_OpenIddictScope FOREIGN KEY (ScopeId) REFERENCES dmscs.OpenIddictScope(Id) ON DELETE CASCADE
    );
END;

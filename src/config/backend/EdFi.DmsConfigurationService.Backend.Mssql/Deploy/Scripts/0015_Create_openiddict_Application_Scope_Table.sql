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
        CONSTRAINT openiddictapplicationscope_pkey PRIMARY KEY (ApplicationId, ScopeId),
        CONSTRAINT fk_application FOREIGN KEY (ApplicationId) REFERENCES dmscs.OpenIddictApplication(Id) ON DELETE CASCADE,
        CONSTRAINT fk_scope FOREIGN KEY (ScopeId) REFERENCES dmscs.OpenIddictScope(Id) ON DELETE CASCADE
    );
END;

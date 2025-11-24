-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.OpenIddictScope (
    Id uuid NOT NULL PRIMARY KEY,
    Name varchar(100) NOT NULL  UNIQUE,
    Description varchar(200),
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256)
);

COMMENT ON TABLE dmscs.OpenIddictScope IS 'OpenIddict scopes storage.';

COMMENT ON COLUMN dmscs.OpenIddictScope.Id IS 'Scope unique identifier.';

COMMENT ON COLUMN dmscs.OpenIddictScope.Name IS 'Scope name.';

COMMENT ON COLUMN dmscs.OpenIddictScope.Description IS 'Scope description.';

COMMENT ON COLUMN dmscs.OpenIddictScope.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.OpenIddictScope.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.OpenIddictScope.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.OpenIddictScope.ModifiedBy IS 'User or client ID who last modified the record';

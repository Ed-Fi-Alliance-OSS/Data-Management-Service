-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.OpenIddictScope (
    Id uuid NOT NULL PRIMARY KEY,
    Name varchar(100) NOT NULL,
    Description varchar(200)
);

COMMENT ON TABLE dmscs.OpenIddictScope IS 'OpenIddict scopes storage.';

COMMENT ON COLUMN dmscs.OpenIddictScope.Id IS 'Scope unique identifier.';

COMMENT ON COLUMN dmscs.OpenIddictScope.Name IS 'Scope name.';

COMMENT ON COLUMN dmscs.OpenIddictScope.Description IS 'Scope description.';

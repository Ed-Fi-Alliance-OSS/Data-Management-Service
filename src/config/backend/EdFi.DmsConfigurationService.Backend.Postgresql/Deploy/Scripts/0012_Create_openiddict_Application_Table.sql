-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.


CREATE TABLE IF NOT EXISTS dmscs.OpenIddictApplication (
    Id uuid NOT NULL PRIMARY KEY,
    ClientId varchar(100) NOT NULL UNIQUE,
    ClientSecret varchar(256),
    DisplayName varchar(200),
    RedirectUris varchar(200)[],
    PostLogoutRedirectUris varchar(200)[],
    Permissions varchar(100)[],
    Requirements varchar(100)[],
    Type varchar(50),
    CreatedAt timestamp without time zone DEFAULT CURRENT_TIMESTAMP,
    ProtocolMappers jsonb
);


COMMENT ON TABLE dmscs.OpenIddictApplication IS 'OpenIddict applications (clients) storage.';


COMMENT ON COLUMN dmscs.OpenIddictApplication.Id IS 'Application unique identifier.';


COMMENT ON COLUMN dmscs.OpenIddictApplication.ClientId IS 'Client identifier.';


COMMENT ON COLUMN dmscs.OpenIddictApplication.ClientSecret IS 'Client secret.';


COMMENT ON COLUMN dmscs.OpenIddictApplication.DisplayName IS 'Display name for the application.';


COMMENT ON COLUMN dmscs.OpenIddictApplication.RedirectUris IS 'Allowed redirect URIs.';


COMMENT ON COLUMN dmscs.OpenIddictApplication.PostLogoutRedirectUris IS 'Allowed post-logout redirect URIs.';


COMMENT ON COLUMN dmscs.OpenIddictApplication.Permissions IS 'Permissions granted to the application.';


COMMENT ON COLUMN dmscs.OpenIddictApplication.Requirements IS 'Requirements for the application.';


COMMENT ON COLUMN dmscs.OpenIddictApplication.Type IS 'Application type (public/confidential).';


COMMENT ON COLUMN dmscs.OpenIddictApplication.CreatedAt IS 'Creation timestamp.';
COMMENT ON COLUMN dmscs.OpenIddictApplication.ProtocolMappers IS 'Protocol mappers for the client, stored as JSON.';

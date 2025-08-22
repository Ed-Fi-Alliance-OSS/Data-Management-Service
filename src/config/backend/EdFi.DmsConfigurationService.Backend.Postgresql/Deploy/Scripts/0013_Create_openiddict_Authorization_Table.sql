-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
CREATE TABLE IF NOT EXISTS dmscs.OpenIddictAuthorization (
    Id uuid NOT NULL PRIMARY KEY,
    ApplicationId uuid NOT NULL,
    Subject varchar(100) NOT NULL,
    Status varchar(50) NOT NULL,
    Type varchar(50) NOT NULL
);

COMMENT ON TABLE dmscs.OpenIddictAuthorization IS 'OpenIddict authorizations storage.';

COMMENT ON COLUMN dmscs.OpenIddictAuthorization.Id IS 'Authorization unique identifier.';

COMMENT ON COLUMN dmscs.OpenIddictAuthorization.ApplicationId IS 'Associated application id.';

COMMENT ON COLUMN dmscs.OpenIddictAuthorization.Subject IS 'Subject (user or client id).';

COMMENT ON COLUMN dmscs.OpenIddictAuthorization.Status IS 'Authorization status.';

COMMENT ON COLUMN dmscs.OpenIddictAuthorization.Type IS 'Authorization type.';

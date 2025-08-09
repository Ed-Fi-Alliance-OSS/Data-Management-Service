-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE if not exists dmscs.openiddict_application (

id uuid NOT NULL,
    client_id text NOT NULL,
    client_secret text,
    display_name text,
    redirect_uris text[],
    post_logout_redirect_uris text[],
    permissions text[],
    requirements text[],
    type text,
    created_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP,
    protocolmappers jsonb,
    CONSTRAINT openiddict_application_pkey PRIMARY KEY (id)
);

comment on Table dmscs.openiddict_application is 'OpenIddict applications (clients) storage.';

comment on Column dmscs.openiddict_application.id is 'Application unique identifier.';

comment on Column dmscs.openiddict_application.client_id is 'Client identifier.';

comment on Column dmscs.openiddict_application.client_secret is 'Client secret.';

comment on Column dmscs.openiddict_application.display_name is 'Display name for the application.';

comment on Column dmscs.openiddict_application.redirect_uris is 'Allowed redirect URIs.';

comment on Column dmscs.openiddict_application.post_logout_redirect_uris is 'Allowed post-logout redirect URIs.';

comment on Column dmscs.openiddict_application.permissions is 'Permissions granted to the application.';

comment on Column dmscs.openiddict_application.requirements is 'Requirements for the application.';

comment on Column dmscs.openiddict_application.type is 'Application type (public/confidential).';

comment on Column dmscs.openiddict_application.created_at is 'Creation timestamp.';
comment on Column dmscs.openiddict_application.protocolmappers is 'Protocol mappers for the client, stored as JSON.';

-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
CREATE TABLE if not exists dmscs.openiddict_authorization (
    id uuid NOT NULL,
    application_id uuid NOT NULL,
    subject text NOT NULL,
    status text NOT NULL,
    type text NOT NULL,
    CONSTRAINT openiddict_authorization_pkey PRIMARY KEY (id)
);

comment on Table dmscs.openiddict_authorization is 'OpenIddict authorizations storage.';

comment on Column dmscs.openiddict_authorization.id is 'Authorization unique identifier.';

comment on Column dmscs.openiddict_authorization.application_id is 'Associated application id.';

comment on Column dmscs.openiddict_authorization.subject is 'Subject (user or client id).';

comment on Column dmscs.openiddict_authorization.status is 'Authorization status.';

comment on Column dmscs.openiddict_authorization.type is 'Authorization type.';

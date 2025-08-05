-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE if not exists dmscs.openiddict_token (
id uuid NOT NULL,
application_id uuid,
subject text,
type text,
reference_id text,
expiration_date timestamp without time zone,
CONSTRAINT openiddict_token_pkey PRIMARY KEY (id)
)
USING heap;

comment on Table dmscs.openiddict_token is 'OpenIddict tokens storage.';

comment on Column dmscs.openiddict_token.id is 'Token unique identifier.';

comment on Column dmscs.openiddict_token.application_id is 'Associated application id.';

comment on Column dmscs.openiddict_token.subject is 'Token subject (user or client id).';

comment on Column dmscs.openiddict_token.type is 'Token type (access, refresh, etc).';

comment on Column dmscs.openiddict_token.reference_id is 'Reference id for the token.';

comment on Column dmscs.openiddict_token.expiration_date is 'Token expiration date.';

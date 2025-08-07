-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE if not exists dmscs.openiddict_scope (
    id uuid NOT NULL,
    name text NOT NULL,
    description text,
    CONSTRAINT openiddict_scope_pkey PRIMARY KEY (id)
);

comment on Table dmscs.openiddict_scope is 'OpenIddict scopes storage.';

comment on Column dmscs.openiddict_scope.id is 'Scope unique identifier.';

comment on Column dmscs.openiddict_scope.name is 'Scope name.';

comment on Column dmscs.openiddict_scope.description is 'Scope description.';

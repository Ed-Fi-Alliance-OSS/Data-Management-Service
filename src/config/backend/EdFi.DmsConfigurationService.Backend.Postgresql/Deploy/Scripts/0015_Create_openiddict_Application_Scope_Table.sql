-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.openiddict_application_scope (
    application_id uuid NOT NULL,
    scope_id uuid NOT NULL,
    CONSTRAINT openiddict_application_scope_pkey PRIMARY KEY (application_id, scope_id),
    CONSTRAINT fk_application FOREIGN KEY (application_id)
        REFERENCES dmscs.openiddict_application(id) ON DELETE CASCADE,
    CONSTRAINT fk_scope FOREIGN KEY (scope_id)
        REFERENCES dmscs.openiddict_scope(id) ON DELETE CASCADE
);

COMMENT ON TABLE dmscs.openiddict_application_scope IS 'Join table for OpenIddict applications and scopes.';
COMMENT ON COLUMN dmscs.openiddict_application_scope.application_id IS 'Application unique identifier.';
COMMENT ON COLUMN dmscs.openiddict_application_scope.scope_id IS 'Scope unique identifier.';

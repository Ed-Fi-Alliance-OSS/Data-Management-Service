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

-- Add missing columns to openiddict_token table for full OpenIddict compatibility
ALTER TABLE dmscs.openiddict_token
ADD COLUMN IF NOT EXISTS authorization_id uuid,
ADD COLUMN IF NOT EXISTS creation_date timestamp without time zone DEFAULT CURRENT_TIMESTAMP,
ADD COLUMN IF NOT EXISTS payload text,
ADD COLUMN IF NOT EXISTS properties text,
ADD COLUMN IF NOT EXISTS redemption_date timestamp without time zone,
ADD COLUMN IF NOT EXISTS status text DEFAULT 'valid';

-- Add comments for new columns
COMMENT ON COLUMN dmscs.openiddict_token.authorization_id IS 'Associated authorization identifier.';
COMMENT ON COLUMN dmscs.openiddict_token.creation_date IS 'Token creation timestamp.';
COMMENT ON COLUMN dmscs.openiddict_token.payload IS 'Token payload (JWT content).';
COMMENT ON COLUMN dmscs.openiddict_token.properties IS 'Additional token properties (JSON).';
COMMENT ON COLUMN dmscs.openiddict_token.redemption_date IS 'Token redemption timestamp.';
COMMENT ON COLUMN dmscs.openiddict_token.status IS 'Token status (valid, revoked, expired).';

-- Add index for better performance
CREATE INDEX IF NOT EXISTS idx_openiddict_token_application_id ON dmscs.openiddict_token(application_id);
CREATE INDEX IF NOT EXISTS idx_openiddict_token_subject ON dmscs.openiddict_token(subject);
CREATE INDEX IF NOT EXISTS idx_openiddict_token_reference_id ON dmscs.openiddict_token(reference_id);
CREATE INDEX IF NOT EXISTS idx_openiddict_token_expiration ON dmscs.openiddict_token(expiration_date);

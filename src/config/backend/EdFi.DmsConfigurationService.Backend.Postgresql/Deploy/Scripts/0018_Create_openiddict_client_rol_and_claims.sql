-- Create oidc_client_rol join table
CREATE TABLE IF NOT EXISTS dmscs.openiddict_client_rol (
    client_id UUID NOT NULL REFERENCES dmscs.openiddict_application(id) ON DELETE CASCADE,
    rol_id UUID NOT NULL REFERENCES dmscs.openiddict_rol(id) ON DELETE CASCADE,
    PRIMARY KEY (client_id, rol_id)
);

-- Add namespace_prefixes and education_organization_ids columns to openiddict_application
ALTER TABLE dmscs.openiddict_application
    ADD COLUMN IF NOT EXISTS namespace_prefixes TEXT,
    ADD COLUMN IF NOT EXISTS education_organization_ids TEXT;

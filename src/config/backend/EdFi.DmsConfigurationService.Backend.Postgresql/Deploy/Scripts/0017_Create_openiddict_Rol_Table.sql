-- Create oidc_rol table
CREATE TABLE IF NOT EXISTS dmscs.oidc_rol (
    id UUID PRIMARY KEY,
    name TEXT NOT NULL UNIQUE
);

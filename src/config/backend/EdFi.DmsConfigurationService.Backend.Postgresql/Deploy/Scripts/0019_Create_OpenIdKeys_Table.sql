-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
-- Create table for OpenID keys
CREATE TABLE IF NOT EXISTS dmscs.OpenIddictKey (
    Id SERIAL PRIMARY KEY,
    KeyId VARCHAR(64) NOT NULL,
    PublicKey BYTEA NOT NULL, -- binary format for public key
    PrivateKey TEXT NOT NULL, -- encrypted with pgcrypto
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    ExpiresAt TIMESTAMP,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE
);

COMMENT ON TABLE dmscs.OpenIddictKey IS 'OpenIddict cryptographic keys storage.';
COMMENT ON COLUMN dmscs.OpenIddictKey.Id IS 'Key unique identifier.';
COMMENT ON COLUMN dmscs.OpenIddictKey.KeyId IS 'Key identifier string.';
COMMENT ON COLUMN dmscs.OpenIddictKey.PublicKey IS 'Public key binary data.';
COMMENT ON COLUMN dmscs.OpenIddictKey.PrivateKey IS 'Encrypted private key.';
COMMENT ON COLUMN dmscs.OpenIddictKey.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.OpenIddictKey.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.OpenIddictKey.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.OpenIddictKey.ModifiedBy IS 'User or client ID who last modified the record';
COMMENT ON COLUMN dmscs.OpenIddictKey.ExpiresAt IS 'Key expiration timestamp.';
COMMENT ON COLUMN dmscs.OpenIddictKey.IsActive IS 'Whether the key is currently active.';

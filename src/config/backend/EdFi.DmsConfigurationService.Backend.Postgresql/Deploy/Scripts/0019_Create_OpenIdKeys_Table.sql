-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
-- Create table for OpenID keys
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE IF NOT EXISTS dmscs.OpenIddictKey (
    Id SERIAL PRIMARY KEY,
    KeyId VARCHAR(64) NOT NULL,
    PublicKey BYTEA NOT NULL, -- binary format for public key
    PrivateKey TEXT NOT NULL, -- encrypted with pgcrypto
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    ExpiresAt TIMESTAMP,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE
);

-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
-- ============================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'dmscs') THEN
        EXECUTE 'CREATE SCHEMA dmscs';
    END IF;
END$$;

CREATE TABLE IF NOT EXISTS dmscs.Profile (
    Id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ProfileName VARCHAR(500) NOT NULL,
    Definition TEXT NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT uq_profile_name UNIQUE (ProfileName)
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relname = 'ix_profile_name' AND n.nspname = 'dmscs'
    ) THEN
        EXECUTE 'CREATE INDEX ix_profile_name ON dmscs.Profile (ProfileName)';
    END IF;
END$$;


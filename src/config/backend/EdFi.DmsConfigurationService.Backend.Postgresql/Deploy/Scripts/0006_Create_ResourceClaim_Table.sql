-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.ResourceClaim (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    ResourceName VARCHAR(255) NOT NULL,
    ClaimName VARCHAR(255) NOT NULL
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'uq_claimname' AND table_schema = 'dmscs' AND table_name = 'resourceclaim'
    ) THEN
        ALTER TABLE dmscs.ResourceClaim ADD CONSTRAINT uq_ClaimName UNIQUE (ClaimName);
    END IF;
END$$;

COMMENT ON COLUMN dmscs.ResourceClaim.Id IS 'Resource Claim Identifier';
COMMENT ON COLUMN dmscs.ResourceClaim.ResourceName IS 'Resource Name';
COMMENT ON COLUMN dmscs.ResourceClaim.ClaimName IS 'Claim Name';

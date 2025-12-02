-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.Tenant (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    Name VARCHAR(256) NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256)
);

COMMENT ON TABLE dmscs.Tenant IS 'Tenants for multi-tenancy support';
COMMENT ON COLUMN dmscs.Tenant.Id IS 'Tenant id';
COMMENT ON COLUMN dmscs.Tenant.Name IS 'Tenant name (unique)';
COMMENT ON COLUMN dmscs.Tenant.CreatedAt IS 'Date and time tenant was created';
COMMENT ON COLUMN dmscs.Tenant.CreatedBy IS 'User or client that created the tenant';
COMMENT ON COLUMN dmscs.Tenant.LastModifiedAt IS 'Date and time tenant was last modified';
COMMENT ON COLUMN dmscs.Tenant.ModifiedBy IS 'User or client that last modified the tenant';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'uq_tenant_name' AND table_schema = 'dmscs' AND table_name = 'tenant'
    ) THEN
        ALTER TABLE dmscs.Tenant ADD CONSTRAINT uq_tenant_name UNIQUE (Name);
    END IF;
END$$;

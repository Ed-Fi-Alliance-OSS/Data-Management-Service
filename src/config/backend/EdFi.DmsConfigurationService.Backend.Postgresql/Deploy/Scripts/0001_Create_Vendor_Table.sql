-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.Vendor (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    Company VARCHAR(256) NOT NULL,
    ContactName VARCHAR(128),
    ContactEmailAddress VARCHAR(320),
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256)
);

COMMENT ON COLUMN dmscs.Vendor.Id IS 'Vendor or company id';
COMMENT ON COLUMN dmscs.Vendor.Company IS 'Vendor or company name';
COMMENT ON COLUMN dmscs.Vendor.ContactName IS 'Vendor contact name';
COMMENT ON COLUMN dmscs.Vendor.ContactEmailAddress IS 'Vendor contact email id';
COMMENT ON COLUMN dmscs.Vendor.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.Vendor.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.Vendor.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.Vendor.ModifiedBy IS 'User or client ID who last modified the record';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'uq_company' AND table_schema = 'dmscs' AND table_name = 'vendor'
    ) THEN
        ALTER TABLE dmscs.Vendor ADD CONSTRAINT uq_Company UNIQUE (Company);
    END IF;
END$$;

CREATE UNIQUE INDEX IF NOT EXISTS idx_Company ON dmscs.Vendor (Company);

CREATE TABLE IF NOT EXISTS dmscs.VendorNamespacePrefix (
    VendorId BIGINT NOT NULL,
    NamespacePrefix VARCHAR(128) NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT pk_VendorNamespacePrefix PRIMARY KEY (VendorId, NamespacePrefix),
    CONSTRAINT fk_vendor_NamespacePrefix FOREIGN KEY (VendorId) REFERENCES dmscs.Vendor(id) ON DELETE CASCADE
);

COMMENT ON TABLE dmscs.VendorNamespacePrefix IS 'Relationship of vendors with Namespace prefix';
COMMENT ON COLUMN dmscs.VendorNamespacePrefix.VendorId IS 'Vendor or company id';
COMMENT ON COLUMN dmscs.VendorNamespacePrefix.NamespacePrefix IS 'Namespace prefix for the vendor';
COMMENT ON COLUMN dmscs.VendorNamespacePrefix.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.VendorNamespacePrefix.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.VendorNamespacePrefix.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.VendorNamespacePrefix.ModifiedBy IS 'User or client ID who last modified the record';

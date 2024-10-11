-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dmscs.Vendor (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    Company VARCHAR(256) NOT NULL,    
    ContactName VARCHAR(128),
    ContactEmailAddress VARCHAR(320)
);

COMMENT ON COLUMN dmscs.Vendor.Id IS 'Vendor or company id';
COMMENT ON COLUMN dmscs.Vendor.Company IS 'Vendor or company name';
COMMENT ON COLUMN dmscs.Vendor.ContactName IS 'Vendor contact name';
COMMENT ON COLUMN dmscs.Vendor.ContactEmailAddress IS 'Vendor contact email id';


CREATE TABLE dmscs.VendorNamespacePrefix (
    VendorId BIGINT NOT NULL,
    NamespacePrefixes VARCHAR(128) NOT NULL,
    CONSTRAINT pk_VendorNamespacePrefix PRIMARY KEY (VendorId, NamespacePrefixes),
    CONSTRAINT fk_vendor_NamespacePrefixes FOREIGN KEY (VendorId) REFERENCES dmscs.Vendor(id) ON DELETE CASCADE
);

COMMENT ON TABLE dmscs.VendorNamespacePrefix IS 'Relationship of vendors with Namespace prefix';
COMMENT ON COLUMN dmscs.VendorNamespacePrefix.VendorId IS 'Vendor or company id';
COMMENT ON COLUMN dmscs.VendorNamespacePrefix.NamespacePrefixes IS 'Namespace prefix for the vendor';

-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.Application (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    ApplicationName VARCHAR(256) NOT NULL,
    VendorId BIGINT NOT NULL,
    ClaimSetName VARCHAR(256) NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT fk_vendor FOREIGN KEY (VendorId) REFERENCES dmscs.Vendor(Id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_vendor_applicationname ON dmscs.Application (VendorId, ApplicationName);

COMMENT ON COLUMN dmscs.Application.Id IS 'Application id';
COMMENT ON COLUMN dmscs.Application.ApplicationName IS 'Application name';
COMMENT ON COLUMN dmscs.Application.VendorId IS 'Vendor or company id';
COMMENT ON COLUMN dmscs.Application.ClaimSetName IS 'Claim set name';
COMMENT ON COLUMN dmscs.Application.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.Application.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.Application.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.Application.ModifiedBy IS 'User or client ID who last modified the record';

CREATE TABLE IF NOT EXISTS dmscs.ApplicationEducationOrganization (
    ApplicationId BIGINT NOT NULL,
    EducationOrganizationId BIGINT NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT pk_applicationEducationOrganization PRIMARY KEY (ApplicationId, EducationOrganizationId),
    CONSTRAINT fk_application_educationOrganization FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(id) ON DELETE CASCADE
);

COMMENT ON TABLE dmscs.ApplicationEducationOrganization IS 'Relationship of applications with educational organizations';
COMMENT ON COLUMN dmscs.ApplicationEducationOrganization.ApplicationId IS 'Application id';
COMMENT ON COLUMN dmscs.ApplicationEducationOrganization.EducationOrganizationId IS 'Education organization id';
COMMENT ON COLUMN dmscs.ApplicationEducationOrganization.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.ApplicationEducationOrganization.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.ApplicationEducationOrganization.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.ApplicationEducationOrganization.ModifiedBy IS 'User or client ID who last modified the record';


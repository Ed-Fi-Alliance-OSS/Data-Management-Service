-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.Application (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    ApplicationName VARCHAR(256) NOT NULL,
    VendorId BIGINT NOT NULL,
    ClaimSetName VARCHAR(256) NOT NULL,
    CONSTRAINT fk_vendor FOREIGN KEY (VendorId) REFERENCES dmscs.Vendor(Id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_vendor_applicationname ON dmscs.Application (VendorId, ApplicationName);

COMMENT ON COLUMN dmscs.Application.Id IS 'Application id';
COMMENT ON COLUMN dmscs.Application.ApplicationName IS 'Application name';
COMMENT ON COLUMN dmscs.Application.VendorId IS 'Vendor or company id';
COMMENT ON COLUMN dmscs.Application.ClaimSetName IS 'Claim set name';

CREATE TABLE IF NOT EXISTS dmscs.ApplicationEducationOrganization (
    ApplicationId BIGINT NOT NULL,
    EducationOrganizationId BIGINT NOT NULL,
    CONSTRAINT pk_applicationEducationOrganization PRIMARY KEY (ApplicationId, EducationOrganizationId),
    CONSTRAINT fk_application_educationOrganization FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(id) ON DELETE CASCADE
);

COMMENT ON TABLE dmscs.ApplicationEducationOrganization IS 'Relationship of applications with educational organizations';
COMMENT ON COLUMN dmscs.ApplicationEducationOrganization.ApplicationId IS 'Application id';
COMMENT ON COLUMN dmscs.ApplicationEducationOrganization.EducationOrganizationId IS 'Education organization id';


-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Scopes vendor company names and profile names by tenant, and gives Profile a tenant column.
--
-- Uniqueness becomes (TenantId, Company) and (TenantId, ProfileName). SQL Server unique constraints
-- treat NULLs as equal, so single-tenant deployments (TenantId IS NULL) keep rejecting duplicates
-- exactly as before.
--
-- What happens to profiles that already exist. Nothing recorded which tenant created a profile, so
-- each existing profile is assigned by the applications that use it, through their vendor's tenant:
--   * A profile used only by applications of one tenant's vendors is assigned to that tenant. Its id
--     does not change.
--   * A profile used by several tenants keeps its original row, which stays unassigned if an
--     unassigned (NULL-tenant) vendor's application uses it, and otherwise goes to the lowest tenant
--     id that uses it. Every other tenant that uses it gets a copy with the same name, definition and
--     CreatedBy, and that tenant's application assignments are moved to the copy. Copies get new ids.
--   * A profile that no application uses stays unassigned. In multi-tenant mode no tenant lists it;
--     in single-tenant mode it is visible as before.
--   * Single-tenant databases, where every vendor is unassigned, are unchanged.
-- Only recorded application assignments are preserved. A client that selects a profile through the
-- profile header without an assignment loses access to it until the profile is re-created in that
-- client's tenant. See "Upgrading an Existing Multi-Tenant Deployment" in
-- docs/MULTI-TENANCY-GETTING-STARTED.md for the inventory and coordinated upgrade steps.
--
-- The new constraint names do not contain the old ones, because the repositories match
-- unique-violation messages by substring.

-- Vendor: tenant-scoped company uniqueness. The new constraint is added before the old one is
-- dropped, so uniqueness is never absent.
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UX_Vendor_TenantId_Company' AND parent_object_id = OBJECT_ID('dmscs.Vendor'))
    ALTER TABLE dmscs.Vendor ADD CONSTRAINT UX_Vendor_TenantId_Company UNIQUE (TenantId, Company);

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UX_Vendor_Company' AND parent_object_id = OBJECT_ID('dmscs.Vendor'))
    ALTER TABLE dmscs.Vendor DROP CONSTRAINT UX_Vendor_Company;
GO

-- Profile: tenant column, matching 0025_Add_TenantId_To_Tables.
IF COL_LENGTH('dmscs.Profile', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dmscs.Profile ADD TenantId BIGINT NULL;
END;
GO
-- ON DELETE NO ACTION: SQL Server disallows the converging cascade paths from Tenant,
-- and tenant deletion is not exposed by the service.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Profile_Tenant')
BEGIN
    ALTER TABLE dmscs.Profile ADD CONSTRAINT FK_Profile_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Profile_TenantId' AND object_id = OBJECT_ID('dmscs.Profile'))
    CREATE INDEX IX_Profile_TenantId ON dmscs.Profile (TenantId);
GO

-- Profile: tenant-scoped name uniqueness. This must precede the assignment below, because a copy
-- shares its original's name.
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UX_Profile_TenantId_ProfileName' AND parent_object_id = OBJECT_ID('dmscs.Profile'))
    ALTER TABLE dmscs.Profile ADD CONSTRAINT UX_Profile_TenantId_ProfileName UNIQUE (TenantId, ProfileName);

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UX_Profile_ProfileName' AND parent_object_id = OBJECT_ID('dmscs.Profile'))
    ALTER TABLE dmscs.Profile DROP CONSTRAINT UX_Profile_ProfileName;
GO

-- Existing profiles: assign by usage and copy when shared, as the header describes. The usage is
-- captured before any row changes, and the whole batch runs in one transaction with XACT_ABORT, so
-- a failure part-way rolls back every assignment and copy instead of leaving some tenants copied
-- and the original already assigned. Replaying it is a no-op: no unassigned profile is still used
-- by a tenant vendor's application, except an original kept for an unassigned vendor, whose only
-- usage rank is that vendor and therefore does no work. Ascending ORDER BY sorts NULL first.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @ProfileUsage TABLE (
    ProfileId INT NOT NULL,
    TenantId BIGINT NULL,
    UsageRank BIGINT NOT NULL
);

INSERT INTO @ProfileUsage (ProfileId, TenantId, UsageRank)
SELECT DistinctUsage.ProfileId,
       DistinctUsage.TenantId,
       ROW_NUMBER() OVER (PARTITION BY DistinctUsage.ProfileId ORDER BY DistinctUsage.TenantId)
FROM (
    SELECT DISTINCT ApplicationProfile.ProfileId, Vendor.TenantId
    FROM dmscs.ApplicationProfile ApplicationProfile
    JOIN dmscs.Profile Profile
        ON Profile.Id = ApplicationProfile.ProfileId
    JOIN dmscs.Application Application
        ON Application.Id = ApplicationProfile.ApplicationId
    JOIN dmscs.Vendor Vendor
        ON Vendor.Id = Application.VendorId
    WHERE Profile.TenantId IS NULL
) DistinctUsage;

DECLARE @ProfileId INT;
DECLARE @TenantId BIGINT;
DECLARE @UsageRank BIGINT;
DECLARE @CopyId INT;

DECLARE ProfileUsageCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT ProfileId, TenantId, UsageRank
    FROM @ProfileUsage
    ORDER BY ProfileId, UsageRank;

OPEN ProfileUsageCursor;
FETCH NEXT FROM ProfileUsageCursor INTO @ProfileId, @TenantId, @UsageRank;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF @UsageRank = 1
    BEGIN
        IF @TenantId IS NOT NULL
            UPDATE dmscs.Profile
            SET TenantId = @TenantId
            WHERE Id = @ProfileId;
    END
    ELSE
    BEGIN
        INSERT INTO dmscs.Profile (ProfileName, Definition, CreatedBy, TenantId)
        SELECT Original.ProfileName, Original.Definition, Original.CreatedBy, @TenantId
        FROM dmscs.Profile Original
        WHERE Original.Id = @ProfileId;

        SET @CopyId = CAST(SCOPE_IDENTITY() AS INT);

        UPDATE ApplicationProfile
        SET ProfileId = @CopyId
        FROM dmscs.ApplicationProfile ApplicationProfile
        JOIN dmscs.Application Application
            ON Application.Id = ApplicationProfile.ApplicationId
        JOIN dmscs.Vendor Vendor
            ON Vendor.Id = Application.VendorId
        WHERE ApplicationProfile.ProfileId = @ProfileId
          AND Vendor.TenantId = @TenantId;
    END;

    FETCH NEXT FROM ProfileUsageCursor INTO @ProfileId, @TenantId, @UsageRank;
END;

CLOSE ProfileUsageCursor;
DEALLOCATE ProfileUsageCursor;

COMMIT TRANSACTION;
SET XACT_ABORT OFF;
GO

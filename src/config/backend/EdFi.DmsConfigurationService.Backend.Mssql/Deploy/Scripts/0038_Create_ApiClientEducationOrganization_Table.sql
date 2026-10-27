-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1639: stores each API client's education organizations, so a client can be limited to some
-- of its Application's education organizations.
--
-- Each client row references the Application's ApplicationEducationOrganization row with
-- ON DELETE CASCADE, so removing an education organization from an Application removes it from
-- every client. A second key, (ApiClientId, ApplicationId) to the new unique ApiClient
-- (Id, ApplicationId), ties every row to the client's current Application. It must be NO ACTION:
-- SQL Server rejects a second cascade path into the table (Application -> ApiClient alongside
-- Application -> ApplicationEducationOrganization). Deleting a client or moving it to another
-- Application is therefore rejected until the code deletes the client's rows first. Deleting an
-- Application, directly or through its Vendor, still removes every client row through the
-- education organization cascade. The index leads with the education organization and serves
-- that cascade.
--
-- InheritsApplicationEducationOrganizations is stored rather than derived: a client narrowed to
-- every current education organization must not pick up ones added later.
-- IdentityProviderSyncPendingSince (UTC) marks a client whose identity provider claims still need
-- an Application change applied.
--
-- Upgrade: every existing client inherits (the column default fills existing rows) and its rows
-- are copied from its Application's current list. Nothing reads the identity providers, so the
-- script can't tell whether a client's provider claims match that list: before 8.1 an Application
-- update refreshed only its first client. Every existing client is therefore marked sync-pending
-- when the column is added, and the Application's next PUT, even an unchanged one, sends every
-- client its claims.
--
-- Everything runs in one explicit transaction. The schema changes take their table locks before
-- the backfill reads and hold them until COMMIT, so no client or Application education
-- organization can change between the backfill and the commit. XACT_ABORT dooms the transaction on
-- any statement error and the CATCH block rolls it back and rethrows, so a failed run leaves
-- nothing behind. The existence guards let a rerun after a lost journal row succeed. The mark and
-- the backfill run only when this run added the sync-pending column, so a rerun neither re-marks
-- clients the application has since synchronized nor widens clients it has since narrowed. The
-- whole script is a single batch with no GO, which can't compile a statement naming a column the
-- batch adds, so the sync-pending mark runs through sp_executesql.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @SyncPendingColumnAdded BIT = 0;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints
        WHERE name = 'UX_ApiClient_Id_ApplicationId'
          AND parent_object_id = OBJECT_ID('dmscs.ApiClient')
    )
        ALTER TABLE dmscs.ApiClient ADD CONSTRAINT UX_ApiClient_Id_ApplicationId UNIQUE (Id, ApplicationId);

    IF OBJECT_ID('dmscs.ApiClientEducationOrganization', 'U') IS NULL
    BEGIN
        CREATE TABLE dmscs.ApiClientEducationOrganization (
            ApiClientId INT NOT NULL,
            ApplicationId INT NOT NULL,
            EducationOrganizationId BIGINT NOT NULL,
            CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
            CreatedBy NVARCHAR(256),
            LastModifiedAt DATETIME2,
            ModifiedBy NVARCHAR(256),
            CONSTRAINT PK_ApiClientEducationOrganization PRIMARY KEY (ApiClientId, EducationOrganizationId),
            CONSTRAINT FK_ApiClientEducationOrganization_ApplicationEducationOrg FOREIGN KEY (ApplicationId, EducationOrganizationId) REFERENCES dmscs.ApplicationEducationOrganization(ApplicationId, EducationOrganizationId) ON DELETE CASCADE,
            CONSTRAINT FK_ApiClientEducationOrganization_ApiClient FOREIGN KEY (ApiClientId, ApplicationId) REFERENCES dmscs.ApiClient(Id, ApplicationId) ON DELETE NO ACTION
        );
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE name = 'IX_ApiClientEducationOrganization_EducationOrganizationId'
          AND object_id = OBJECT_ID('dmscs.ApiClientEducationOrganization')
    )
        CREATE INDEX IX_ApiClientEducationOrganization_EducationOrganizationId
            ON dmscs.ApiClientEducationOrganization (EducationOrganizationId, ApplicationId);

    IF COL_LENGTH('dmscs.ApiClient', 'InheritsApplicationEducationOrganizations') IS NULL
        ALTER TABLE dmscs.ApiClient ADD InheritsApplicationEducationOrganizations BIT NOT NULL
            CONSTRAINT DF_ApiClient_InheritsApplicationEducationOrganizations DEFAULT 1;

    IF COL_LENGTH('dmscs.ApiClient', 'IdentityProviderSyncPendingSince') IS NULL
    BEGIN
        ALTER TABLE dmscs.ApiClient ADD IdentityProviderSyncPendingSince DATETIME2 NULL;
        SET @SyncPendingColumnAdded = 1;
    END;

    IF @SyncPendingColumnAdded = 1
    BEGIN
        EXEC sp_executesql N'UPDATE dmscs.ApiClient SET IdentityProviderSyncPendingSince = SYSUTCDATETIME();';

        INSERT INTO dmscs.ApiClientEducationOrganization (ApiClientId, ApplicationId, EducationOrganizationId)
        SELECT client.Id, client.ApplicationId, applicationEdOrg.EducationOrganizationId
        FROM dmscs.ApiClient client
        JOIN dmscs.ApplicationEducationOrganization applicationEdOrg
            ON applicationEdOrg.ApplicationId = client.ApplicationId
        WHERE NOT EXISTS (
            SELECT 1
            FROM dmscs.ApiClientEducationOrganization existing
            WHERE existing.ApiClientId = client.Id
              AND existing.EducationOrganizationId = applicationEdOrg.EducationOrganizationId
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
SET XACT_ABORT OFF;

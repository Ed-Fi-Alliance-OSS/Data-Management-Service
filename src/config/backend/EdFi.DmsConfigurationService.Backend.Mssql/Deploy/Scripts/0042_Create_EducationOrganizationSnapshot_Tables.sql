-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Education organizations read from DMS for each ordinary data store.
--
-- EducationOrganizationSnapshotTarget has one row per ordinary data store that has been refreshed. It
-- holds the generation counter, the published (active) generation and the refresh lease. Each refresh
-- stages its organizations under a new generation, and publishing moves ActiveGeneration to it, so
-- readers see one complete generation at a time. A target row references DataStore with ON DELETE
-- CASCADE, and organization rows reference their target row with ON DELETE CASCADE, so deleting an
-- ordinary data store removes its snapshot in the same statement, on every delete path. DataStore ->
-- target -> organizations must be the only cascade path into either table: SQL Server rejects a
-- second one, so the target's TenantId foreign key is NO ACTION and organization rows copy TenantId
-- from their target without a foreign key. LeaseOwner uses a binary collation so that ownership
-- matches exactly, as on PostgreSQL. All times are UTC.
--
-- Everything runs in one explicit transaction. XACT_ABORT dooms the transaction on any statement
-- error and the CATCH block rolls it back and rethrows, so a failed run leaves nothing behind. The
-- existence guards let a rerun after a lost journal row succeed.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID('dmscs.EducationOrganizationSnapshotTarget', 'U') IS NULL
    BEGIN
        CREATE TABLE dmscs.EducationOrganizationSnapshotTarget (
            DataStoreId INT NOT NULL,
            TenantId BIGINT NULL,
            LastGeneration BIGINT NOT NULL
                CONSTRAINT DF_EducationOrganizationSnapshotTarget_LastGeneration DEFAULT 0,
            ActiveGeneration BIGINT NULL,
            RefreshedAt DATETIME2 NULL,
            SourceContractVersion NVARCHAR(100) NULL,
            LeaseOwner NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
            LeaseGeneration BIGINT NULL,
            LeaseExpiresAt DATETIME2 NULL,
            CreatedAt DATETIME2 NOT NULL
                CONSTRAINT DF_EducationOrganizationSnapshotTarget_CreatedAt DEFAULT SYSUTCDATETIME(),
            LastModifiedAt DATETIME2 NULL,
            CONSTRAINT PK_EducationOrganizationSnapshotTarget PRIMARY KEY (DataStoreId),
            CONSTRAINT FK_EducationOrganizationSnapshotTarget_DataStore FOREIGN KEY (DataStoreId)
                REFERENCES dmscs.DataStore(Id) ON DELETE CASCADE,
            CONSTRAINT FK_EducationOrganizationSnapshotTarget_Tenant FOREIGN KEY (TenantId)
                REFERENCES dmscs.Tenant(Id) ON DELETE NO ACTION,
            CONSTRAINT CK_EducationOrganizationSnapshotTarget_Generations CHECK (
                LastGeneration >= 0
                AND (ActiveGeneration IS NULL OR ActiveGeneration BETWEEN 1 AND LastGeneration)
            ),
            CONSTRAINT CK_EducationOrganizationSnapshotTarget_Lease CHECK (
                (LeaseOwner IS NULL AND LeaseGeneration IS NULL AND LeaseExpiresAt IS NULL)
                OR (
                    LeaseOwner IS NOT NULL
                    AND LeaseGeneration IS NOT NULL
                    AND LeaseExpiresAt IS NOT NULL
                    AND LeaseGeneration BETWEEN 1 AND LastGeneration
                )
            )
        );
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE name = 'IX_EducationOrganizationSnapshotTarget_TenantId'
          AND object_id = OBJECT_ID('dmscs.EducationOrganizationSnapshotTarget')
    )
        CREATE INDEX IX_EducationOrganizationSnapshotTarget_TenantId
            ON dmscs.EducationOrganizationSnapshotTarget (TenantId);

    IF OBJECT_ID('dmscs.EducationOrganizationSnapshot', 'U') IS NULL
    BEGIN
        CREATE TABLE dmscs.EducationOrganizationSnapshot (
            DataStoreId INT NOT NULL,
            Generation BIGINT NOT NULL,
            TenantId BIGINT NULL,
            EducationOrganizationId BIGINT NOT NULL,
            NameOfInstitution NVARCHAR(256) NOT NULL,
            ShortNameOfInstitution NVARCHAR(256) NULL,
            Discriminator NVARCHAR(128) NOT NULL,
            ParentId BIGINT NULL,
            RefreshedAt DATETIME2 NOT NULL,
            SourceContractVersion NVARCHAR(100) NOT NULL,
            CONSTRAINT PK_EducationOrganizationSnapshot PRIMARY KEY (DataStoreId, Generation, EducationOrganizationId),
            CONSTRAINT FK_EducationOrganizationSnapshot_Target FOREIGN KEY (DataStoreId)
                REFERENCES dmscs.EducationOrganizationSnapshotTarget(DataStoreId) ON DELETE CASCADE,
            CONSTRAINT CK_EducationOrganizationSnapshot_Generation CHECK (Generation >= 1)
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

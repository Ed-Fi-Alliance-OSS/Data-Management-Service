-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1439 step 1.3d: storage only; no managed records or handlers are activated.
-- The exact canonical endpoint JSON is written by the repository serializer. Its digest is
-- a bounded, conservative reservation key, never identity proof: admission must compare every
-- descriptor field against the operation-bound snapshot. No endpoint field is truncated.
-- Pin immutability, legal transitions and same-tenant link verification belong to the model
-- and fenced repository transactions. Job IDs deliberately have no FK: retention removes jobs.

-- Pin string bounds derive from existing validators and the 16 MiB manifest envelope;
-- see catalog-name-uniqueness-rollout.md. Large Unicode values are bounded by UTF-8 bytes.
-- Replay builds an empty sibling from this one canonical DDL definition. It never repairs
-- an existing table or index by name; incompatible objects require explicit operator repair.

SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @Definition nvarchar(max) = N'CREATE TABLE dmscs.DataStoreManage (
    Id INT IDENTITY(1,1) NOT NULL,
    TenantId BIGINT NULL,
    Name NVARCHAR(46) NOT NULL,
    NameComparisonKey AS dmscs.DataStoreNameComparisonKey(Name) PERSISTED,
    Template VARCHAR(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Status VARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    DataStoreId INT NULL,
    HistoricalDataStoreId INT NULL,
    HistoricalDataStoreName NVARCHAR(46) NULL,
    TargetDatabaseName VARCHAR(63) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TargetDatabaseNameKey AS TRANSLATE(TargetDatabaseName, ''ABCDEFGHIJKLMNOPQRSTUVWXYZ'', ''abcdefghijklmnopqrstuvwxyz'') PERSISTED,
    ExpectedSourceIdentity UNIQUEIDENTIFIER NOT NULL,
    EndpointDescriptor NVARCHAR(MAX) NOT NULL,
    EndpointDigest AS CONVERT(binary(32), HASHBYTES(''SHA2_256'', EndpointDescriptor)) PERSISTED,
    CreateJobId NVARCHAR(150) NOT NULL,
    DeleteJobId NVARCHAR(150) NULL,
    LastAttempt INT NOT NULL DEFAULT 0,
    LastFailureCategory VARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
    LastFailureProvisioningCode VARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
    LastFailureJobCode VARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
    CreatedAt DATETIME2 NOT NULL,
    UpdatedAt DATETIME2 NOT NULL,
    DeletedAt DATETIME2 NULL,
    LastRefreshed DATETIME2 NOT NULL,
    LastModifiedDate DATETIME2 NOT NULL,
    PinnedContractVersion INT NULL,
    PinnedProvider NVARCHAR(10) NULL,
    PinnedPackageId NVARCHAR(100) NULL,
    PinnedPackageVersion NVARCHAR(MAX) NULL,
    PinnedPackageSha256 NVARCHAR(64) NULL,
    PinnedArtifactSha256 NVARCHAR(64) NULL,
    PinnedTemplateKind NVARCHAR(9) NULL,
    PinnedDataStandardVersion NVARCHAR(100) NULL,
    PinnedEffectiveSchemaHash NVARCHAR(64) NULL,
    PinnedInventorySha256 NVARCHAR(64) NULL,
    PinnedApiSchemaFormatVersion NVARCHAR(MAX) NULL,
    PinnedRelationalMappingVersion NVARCHAR(MAX) NULL,
    CONSTRAINT PK_DataStoreManage PRIMARY KEY (Id),
    CONSTRAINT FK_DataStoreManage_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant (Id) ON DELETE NO ACTION,
    CONSTRAINT FK_DataStoreManage_DataStore FOREIGN KEY (DataStoreId) REFERENCES dmscs.DataStore (Id) ON DELETE NO ACTION,
    CONSTRAINT CK_DataStoreManage_Name CHECK (DATALENGTH(NameComparisonKey) > 4),
    CONSTRAINT CK_DataStoreManage_Template CHECK (Template IN (''Minimal'', ''Sample'') AND DATALENGTH(Template) = LEN(Template)),
    CONSTRAINT CK_DataStoreManage_Status CHECK (Status IN (''PendingCreate'', ''CreateInProgress'', ''CreateFailed'', ''CreateError'', ''Created'',
        ''PendingDelete'', ''DeleteInProgress'', ''DeleteFailed'', ''DeleteError'', ''Deleted'') AND DATALENGTH(Status) = LEN(Status)),
    CONSTRAINT CK_DataStoreManage_TargetDatabaseName CHECK (TargetDatabaseName LIKE ''[A-Za-z]%'' AND TargetDatabaseName NOT LIKE ''%[^A-Za-z0-9_]%'' AND DATALENGTH(TargetDatabaseName) = LEN(TargetDatabaseName)),
    CONSTRAINT CK_DataStoreManage_ExpectedSourceIdentity CHECK (ExpectedSourceIdentity <> ''00000000-0000-0000-0000-000000000000''),
    CONSTRAINT CK_DataStoreManage_EndpointDescriptor CHECK (CASE WHEN ISJSON(EndpointDescriptor) = 1 THEN
        CASE WHEN JSON_VALUE(EndpointDescriptor, ''$[0]'') IS NOT NULL
            AND JSON_VALUE(EndpointDescriptor, ''$[0]'') = N''1''
            AND DATALENGTH(JSON_VALUE(EndpointDescriptor, ''$[0]'')) = 2
            AND JSON_VALUE(EndpointDescriptor, ''$[1]'') IS NOT NULL
            AND JSON_VALUE(EndpointDescriptor, ''$[1]'') COLLATE Latin1_General_100_BIN2 IN (N''postgresql'', N''mssql'')
            AND DATALENGTH(JSON_VALUE(EndpointDescriptor, ''$[1]'')) = 2 * LEN(JSON_VALUE(EndpointDescriptor, ''$[1]''))
        THEN 1 ELSE 0 END ELSE 0 END = 1),
    CONSTRAINT CK_DataStoreManage_LastAttempt CHECK (LastAttempt >= 0),
    CONSTRAINT CK_DataStoreManage_LastFailure CHECK (
        (LastFailureCategory IS NULL AND LastFailureProvisioningCode IS NULL AND LastFailureJobCode IS NULL)
        OR (LastFailureCategory IS NOT NULL AND LastFailureCategory IN (''InvalidRequest'', ''NotReady'', ''Transient'', ''Permanent'', ''OperatorInspectionRequired'') AND DATALENGTH(LastFailureCategory) = LEN(LastFailureCategory)
            AND ((LastFailureProvisioningCode IS NOT NULL AND LEN(LastFailureProvisioningCode) > 0 AND LastFailureJobCode IS NULL)
                OR (LastFailureProvisioningCode IS NULL AND LastFailureJobCode IS NOT NULL AND LEN(LastFailureJobCode) > 0)))),
    CONSTRAINT CK_DataStoreManage_JobIds CHECK (LEN(CreateJobId) > 0 AND (DeleteJobId IS NULL OR LEN(DeleteJobId) > 0)),
    CONSTRAINT CK_DataStoreManage_ArtifactPinBounds CHECK (
        (PinnedPackageVersion IS NULL OR DATALENGTH(CONVERT(varchar(max), PinnedPackageVersion COLLATE Latin1_General_100_BIN2_UTF8)) <= 16777216)
        AND (PinnedApiSchemaFormatVersion IS NULL OR DATALENGTH(CONVERT(varchar(max), PinnedApiSchemaFormatVersion COLLATE Latin1_General_100_BIN2_UTF8)) <= 16777216)
        AND (PinnedRelationalMappingVersion IS NULL OR DATALENGTH(CONVERT(varchar(max), PinnedRelationalMappingVersion COLLATE Latin1_General_100_BIN2_UTF8)) <= 16777216)),
    CONSTRAINT CK_DataStoreManage_ArtifactPin CHECK (
        (PinnedContractVersion IS NULL AND PinnedProvider IS NULL
            AND PinnedPackageId IS NULL AND PinnedPackageVersion IS NULL
            AND PinnedPackageSha256 IS NULL AND PinnedArtifactSha256 IS NULL
            AND PinnedTemplateKind IS NULL AND PinnedDataStandardVersion IS NULL
            AND PinnedEffectiveSchemaHash IS NULL AND PinnedInventorySha256 IS NULL
            AND PinnedApiSchemaFormatVersion IS NULL AND PinnedRelationalMappingVersion IS NULL)
        OR (PinnedContractVersion IS NOT NULL AND PinnedProvider IS NOT NULL
            AND PinnedPackageId IS NOT NULL AND PinnedPackageVersion IS NOT NULL
            AND PinnedPackageSha256 IS NOT NULL AND PinnedArtifactSha256 IS NOT NULL
            AND PinnedTemplateKind IS NOT NULL AND PinnedDataStandardVersion IS NOT NULL
            AND PinnedEffectiveSchemaHash IS NOT NULL AND PinnedInventorySha256 IS NOT NULL
            AND PinnedApiSchemaFormatVersion IS NOT NULL AND PinnedRelationalMappingVersion IS NOT NULL))
);

CREATE UNIQUE INDEX UX_DataStoreManage_TenantId_Name ON dmscs.DataStoreManage (TenantId, NameComparisonKey) WHERE Status <> ''Deleted'';
CREATE UNIQUE INDEX UX_DataStoreManage_ActiveTarget ON dmscs.DataStoreManage (EndpointDigest, TargetDatabaseNameKey) WHERE Status <> ''Deleted'';
CREATE UNIQUE INDEX UX_DataStoreManage_DataStoreId ON dmscs.DataStoreManage (DataStoreId) WHERE DataStoreId IS NOT NULL;
CREATE INDEX IX_DataStoreManage_TenantId ON dmscs.DataStoreManage (TenantId);';
    DECLARE @ActualId int = OBJECT_ID(N'dmscs.DataStoreManage');
    DECLARE @ExpectedId int;

    IF OBJECT_ID(N'dmscs.DataStoreManage_0041_Expected') IS NOT NULL
        THROW 50041, 'DataStoreManage replay is incompatible with reserved DataStoreManage_0041_Expected object; inspect and repair explicitly before retrying.', 1;

    IF @ActualId IS NULL
        EXEC sys.sp_executesql @Definition;
    ELSE
    BEGIN
        IF OBJECT_ID(N'dmscs.DataStoreManage', N'U') IS NULL
            THROW 50041, 'DataStoreManage object is incompatible; inspect and repair explicitly before retrying.', 1;

        -- Take an exclusive write boundary even when the table is empty.
        DECLARE @RowCount bigint;
        EXEC sys.sp_executesql N'SELECT @Count = COUNT_BIG(*) FROM dmscs.DataStoreManage WITH (TABLOCKX, HOLDLOCK);',
            N'@Count bigint OUTPUT', @Count = @RowCount OUTPUT;
        DECLARE @ExpectedDefinition nvarchar(max) = REPLACE(@Definition, N'DataStoreManage', N'DataStoreManage_0041_Expected');
        EXEC sys.sp_executesql @ExpectedDefinition;
        SET @ExpectedId = OBJECT_ID(N'dmscs.DataStoreManage_0041_Expected');

        -- Both sides come from server-parsed canonical DDL, not duplicated field/type lists.
        -- JSON preserves boundaries and literal values. Only expected object identifiers are
        -- normalized; expression text is compared under BIN2 with an explicit byte-length check.
        DECLARE @Shapes TABLE (
            ObjectId int NOT NULL, Kind varchar(16) NOT NULL,
            Name nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Definition nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL
        );
        INSERT INTO @Shapes
        SELECT c.object_id, 'column', c.name,
            (SELECT c.column_id, c.user_type_id, c.max_length, c.precision, c.scale,
                c.collation_name, c.is_nullable, c.is_ansi_padded, c.is_identity, c.is_computed,
                c.is_rowguidcol, c.is_sparse, c.is_column_set, c.generated_always_type, c.is_hidden,
                cc.is_persisted, cc.definition AS ComputedDefinition, dc.definition AS DefaultDefinition,
                CONVERT(nvarchar(100), ic.seed_value) AS IdentitySeed,
                CONVERT(nvarchar(100), ic.increment_value) AS IdentityIncrement, ic.is_not_for_replication
             FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
        FROM sys.columns c
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE c.object_id IN (@ActualId, @ExpectedId);

        INSERT INTO @Shapes
        SELECT t.object_id, 'table', 'configuration',
            (SELECT t.temporal_type, t.is_memory_optimized, t.is_filetable
             FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
        FROM sys.tables t WHERE t.object_id IN (@ActualId, @ExpectedId);

        INSERT INTO @Shapes
        SELECT c.parent_object_id, 'check',
            CASE WHEN c.parent_object_id = @ExpectedId THEN REPLACE(c.name, N'DataStoreManage_0041_Expected', N'DataStoreManage') ELSE c.name END,
            (SELECT c.parent_column_id, c.definition, c.is_disabled, c.is_not_trusted, c.is_not_for_replication
             FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
        FROM sys.check_constraints c WHERE c.parent_object_id IN (@ActualId, @ExpectedId);

        INSERT INTO @Shapes
        SELECT f.parent_object_id, 'foreign key',
            CASE WHEN f.parent_object_id = @ExpectedId THEN REPLACE(f.name, N'DataStoreManage_0041_Expected', N'DataStoreManage') ELSE f.name END,
            (SELECT f.referenced_object_id, f.delete_referential_action, f.update_referential_action,
                f.is_disabled, f.is_not_trusted, f.is_not_for_replication,
                (SELECT k.parent_column_id, k.referenced_column_id FROM sys.foreign_key_columns k
                 WHERE k.constraint_object_id = f.object_id ORDER BY k.constraint_column_id FOR JSON PATH) AS Keys
             FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
        FROM sys.foreign_keys f WHERE f.parent_object_id IN (@ActualId, @ExpectedId);

        INSERT INTO @Shapes
        SELECT k.parent_object_id, 'key',
            CASE WHEN k.parent_object_id = @ExpectedId THEN REPLACE(k.name, N'DataStoreManage_0041_Expected', N'DataStoreManage') ELSE k.name END,
            (SELECT k.type FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
        FROM sys.key_constraints k WHERE k.parent_object_id IN (@ActualId, @ExpectedId);

        INSERT INTO @Shapes
        SELECT i.object_id, 'index',
            CASE WHEN i.object_id = @ExpectedId THEN REPLACE(i.name, N'DataStoreManage_0041_Expected', N'DataStoreManage') ELSE i.name END,
            (SELECT i.type, i.is_unique, i.is_disabled, i.is_hypothetical, i.is_primary_key,
                i.is_unique_constraint, i.ignore_dup_key, i.has_filter, i.filter_definition,
                (SELECT k.column_id, k.key_ordinal, k.is_descending_key, k.is_included_column
                 FROM sys.index_columns k WHERE k.object_id = i.object_id AND k.index_id = i.index_id
                 ORDER BY k.index_column_id FOR JSON PATH) AS Keys
             FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
        FROM sys.indexes i WHERE i.object_id IN (@ActualId, @ExpectedId) AND i.name IS NOT NULL;

        IF (SELECT COUNT(*) FROM @Shapes WHERE ObjectId = @ActualId AND Kind = 'column')
                <> (SELECT COUNT(*) FROM @Shapes WHERE ObjectId = @ExpectedId AND Kind = 'column')
            OR EXISTS (
                SELECT 1 FROM @Shapes e
                LEFT JOIN @Shapes a ON a.ObjectId = @ActualId AND a.Kind = e.Kind AND a.Name = e.Name
                WHERE e.ObjectId = @ExpectedId AND (a.Name IS NULL
                    OR DATALENGTH(a.Definition) <> DATALENGTH(e.Definition) OR a.Definition <> e.Definition)
            )
            THROW 50041, 'DataStoreManage table or approved constraint/index is missing or incompatible; inspect and repair explicitly before retrying.', 1;

        DROP TABLE dmscs.DataStoreManage_0041_Expected;
    END;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

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

    -- A committed migration whose journal write was lost can be replayed.
    IF OBJECT_ID(N'dmscs.DataStoreManage', N'U') IS NULL
    BEGIN
        CREATE TABLE dmscs.DataStoreManage (
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
            TargetDatabaseNameKey AS TRANSLATE(TargetDatabaseName, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') PERSISTED,
            ExpectedSourceIdentity UNIQUEIDENTIFIER NOT NULL,
            EndpointDescriptor NVARCHAR(MAX) NOT NULL,
            EndpointDigest AS CONVERT(binary(32), HASHBYTES('SHA2_256', EndpointDescriptor)) PERSISTED,
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
            PinnedProvider NVARCHAR(MAX) NULL,
            PinnedPackageId NVARCHAR(MAX) NULL,
            PinnedPackageVersion NVARCHAR(MAX) NULL,
            PinnedPackageSha256 NVARCHAR(MAX) NULL,
            PinnedArtifactSha256 NVARCHAR(MAX) NULL,
            PinnedTemplateKind NVARCHAR(MAX) NULL,
            PinnedDataStandardVersion NVARCHAR(MAX) NULL,
            PinnedEffectiveSchemaHash NVARCHAR(MAX) NULL,
            PinnedInventorySha256 NVARCHAR(MAX) NULL,
            PinnedApiSchemaFormatVersion NVARCHAR(MAX) NULL,
            PinnedRelationalMappingVersion NVARCHAR(MAX) NULL,
            CONSTRAINT PK_DataStoreManage PRIMARY KEY (Id),
            CONSTRAINT FK_DataStoreManage_Tenant FOREIGN KEY (TenantId) REFERENCES dmscs.Tenant (Id) ON DELETE NO ACTION,
            CONSTRAINT FK_DataStoreManage_DataStore FOREIGN KEY (DataStoreId) REFERENCES dmscs.DataStore (Id) ON DELETE NO ACTION,
            CONSTRAINT CK_DataStoreManage_Name CHECK (DATALENGTH(NameComparisonKey) > 4),
            CONSTRAINT CK_DataStoreManage_Template CHECK (Template IN ('Minimal', 'Sample') AND DATALENGTH(Template) = LEN(Template)),
            CONSTRAINT CK_DataStoreManage_Status CHECK (Status IN ('PendingCreate', 'CreateInProgress', 'CreateFailed', 'CreateError', 'Created',
                'PendingDelete', 'DeleteInProgress', 'DeleteFailed', 'DeleteError', 'Deleted') AND DATALENGTH(Status) = LEN(Status)),
            CONSTRAINT CK_DataStoreManage_TargetDatabaseName CHECK (TargetDatabaseName LIKE '[A-Za-z]%' AND TargetDatabaseName NOT LIKE '%[^A-Za-z0-9_]%' AND DATALENGTH(TargetDatabaseName) = LEN(TargetDatabaseName)),
            CONSTRAINT CK_DataStoreManage_ExpectedSourceIdentity CHECK (ExpectedSourceIdentity <> '00000000-0000-0000-0000-000000000000'),
            CONSTRAINT CK_DataStoreManage_EndpointDescriptor CHECK (CASE WHEN ISJSON(EndpointDescriptor) = 1 THEN
                CASE WHEN JSON_VALUE(EndpointDescriptor, '$[0]') IS NOT NULL
                    AND JSON_VALUE(EndpointDescriptor, '$[0]') = N'1'
                    AND DATALENGTH(JSON_VALUE(EndpointDescriptor, '$[0]')) = 2
                    AND JSON_VALUE(EndpointDescriptor, '$[1]') IS NOT NULL
                    AND JSON_VALUE(EndpointDescriptor, '$[1]') COLLATE Latin1_General_100_BIN2 IN (N'postgresql', N'mssql')
                    AND DATALENGTH(JSON_VALUE(EndpointDescriptor, '$[1]')) = 2 * LEN(JSON_VALUE(EndpointDescriptor, '$[1]'))
                THEN 1 ELSE 0 END ELSE 0 END = 1),
            CONSTRAINT CK_DataStoreManage_LastAttempt CHECK (LastAttempt >= 0),
            CONSTRAINT CK_DataStoreManage_LastFailure CHECK (
                (LastFailureCategory IS NULL AND LastFailureProvisioningCode IS NULL AND LastFailureJobCode IS NULL)
                OR (LastFailureCategory IS NOT NULL AND LastFailureCategory IN ('InvalidRequest', 'NotReady', 'Transient', 'Permanent', 'OperatorInspectionRequired') AND DATALENGTH(LastFailureCategory) = LEN(LastFailureCategory)
                    AND ((LastFailureProvisioningCode IS NOT NULL AND LEN(LastFailureProvisioningCode) > 0 AND LastFailureJobCode IS NULL)
                        OR (LastFailureProvisioningCode IS NULL AND LastFailureJobCode IS NOT NULL AND LEN(LastFailureJobCode) > 0)))),
            CONSTRAINT CK_DataStoreManage_JobIds CHECK (LEN(CreateJobId) > 0 AND (DeleteJobId IS NULL OR LEN(DeleteJobId) > 0)),
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
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dmscs.DataStoreManage') AND name = N'UX_DataStoreManage_TenantId_Name')
        CREATE UNIQUE INDEX UX_DataStoreManage_TenantId_Name ON dmscs.DataStoreManage (TenantId, NameComparisonKey) WHERE Status <> 'Deleted';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dmscs.DataStoreManage') AND name = N'UX_DataStoreManage_ActiveTarget')
        CREATE UNIQUE INDEX UX_DataStoreManage_ActiveTarget ON dmscs.DataStoreManage (EndpointDigest, TargetDatabaseNameKey) WHERE Status <> 'Deleted';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dmscs.DataStoreManage') AND name = N'UX_DataStoreManage_DataStoreId')
        CREATE UNIQUE INDEX UX_DataStoreManage_DataStoreId ON dmscs.DataStoreManage (DataStoreId) WHERE DataStoreId IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dmscs.DataStoreManage') AND name = N'IX_DataStoreManage_TenantId')
        CREATE INDEX IX_DataStoreManage_TenantId ON dmscs.DataStoreManage (TenantId);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

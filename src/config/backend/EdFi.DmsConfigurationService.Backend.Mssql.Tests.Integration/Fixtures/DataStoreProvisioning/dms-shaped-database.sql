-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- A hand-built DMS-shaped SQL Server database for the DMS-1438 Step 4.1 integration tests: the dms singleton
-- tables with the generated DDL's column shapes, one resource project (edfi) with its tracked_changes
-- companion, and one object of each inventory category the producer's query reports here (sequence, table,
-- view, scalar function, trigger, table type, named and system-named constraints, standalone and unique
-- indexes). It emits no GRANT, DENY, user, role, or membership, so the database keeps exactly the built-in
-- security state of a new database, as the generated DDL does. Batches are separated by lines holding only
-- GO. The committed canonical inventory and hash beside this file were produced from it by the delivered
-- PowerShell module (see README.md).

CREATE SCHEMA [dms];
GO
CREATE SCHEMA [edfi];
GO
CREATE SCHEMA [tracked_changes_edfi];
GO

CREATE SEQUENCE [dms].[ChangeVersionSequence] START WITH 1;

CREATE TABLE [dms].[DataStoreIdentity]
(
    [DataStoreIdentitySingletonId] smallint NOT NULL,
    [SourceIdentity] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_DataStoreIdentity] PRIMARY KEY CLUSTERED ([DataStoreIdentitySingletonId]),
    CONSTRAINT [CK_DataStoreIdentity_Singleton] CHECK ([DataStoreIdentitySingletonId] = 1)
);

CREATE TABLE [dms].[EffectiveSchema]
(
    [EffectiveSchemaSingletonId] smallint NOT NULL,
    [ApiSchemaFormatVersion] nvarchar(64) NOT NULL,
    [EffectiveSchemaHash] nvarchar(64) NOT NULL,
    [ResourceKeyCount] smallint NOT NULL,
    [ResourceKeySeedHash] binary(32) NOT NULL,
    [AppliedAt] datetime2(7) NOT NULL CONSTRAINT [DF_EffectiveSchema_AppliedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_EffectiveSchema] PRIMARY KEY CLUSTERED ([EffectiveSchemaSingletonId]),
    CONSTRAINT [CK_EffectiveSchema_Singleton] CHECK ([EffectiveSchemaSingletonId] = 1),
    CONSTRAINT [UX_EffectiveSchema_EffectiveSchemaHash] UNIQUE ([EffectiveSchemaHash])
);

CREATE TABLE [dms].[ResourceKey]
(
    [ResourceKeyId] smallint NOT NULL,
    [ProjectName] nvarchar(256) NOT NULL,
    [ResourceName] nvarchar(256) NOT NULL,
    [ResourceVersion] nvarchar(32) NOT NULL,
    CONSTRAINT [PK_ResourceKey] PRIMARY KEY CLUSTERED ([ResourceKeyId])
);

CREATE TABLE [dms].[Document]
(
    [DocumentId] bigint IDENTITY(1,1) NOT NULL,
    [DocumentUuid] uniqueidentifier NOT NULL,
    [ResourceKeyId] smallint NOT NULL,
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_Document_ContentVersion] DEFAULT (NEXT VALUE FOR [dms].[ChangeVersionSequence]),
    CONSTRAINT [PK_Document] PRIMARY KEY CLUSTERED ([DocumentId]),
    CONSTRAINT [FK_Document_ResourceKey] FOREIGN KEY ([ResourceKeyId]) REFERENCES [dms].[ResourceKey] ([ResourceKeyId])
);

CREATE INDEX [IX_Document_ResourceKeyId] ON [dms].[Document] ([ResourceKeyId]);

-- The ComputedAt default is deliberately unnamed: SQL Server generates a different name for it in every
-- database, so the inventory keys it by its column.
CREATE TABLE [dms].[DocumentCache]
(
    [DocumentId] bigint NOT NULL,
    [DocumentUuid] uniqueidentifier NOT NULL,
    [DocumentJson] nvarchar(max) NOT NULL,
    [ComputedAt] datetime2(7) NOT NULL DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_DocumentCache] PRIMARY KEY CLUSTERED ([DocumentId])
);

CREATE TABLE [dms].[DocumentCacheState]
(
    [StateId] smallint NOT NULL,
    [ProjectionLifecycleState] varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [CacheAheadRecoveryRequired] bit NOT NULL,
    CONSTRAINT [PK_DocumentCacheState] PRIMARY KEY CLUSTERED ([StateId]),
    CONSTRAINT [CK_DocumentCacheState_Singleton] CHECK ([StateId] = 1)
);

CREATE TABLE [dms].[DocumentProjectionWork]
(
    [DocumentId] bigint NOT NULL,
    [RequiredContentVersion] bigint NOT NULL,
    [FirstEnqueuedAt] datetime2(7) NOT NULL,
    [LastEnqueuedAt] datetime2(7) NOT NULL,
    CONSTRAINT [PK_DocumentProjectionWork] PRIMARY KEY CLUSTERED ([DocumentId])
);
GO

CREATE FUNCTION [dms].[GetMaxChangeVersion]()
RETURNS bigint
AS
BEGIN
    RETURN (SELECT CONVERT(bigint, s.[current_value]) FROM sys.sequences s WHERE s.[object_id] = OBJECT_ID(N'[dms].[ChangeVersionSequence]'));
END;
GO

CREATE TRIGGER [dms].[TR_Document_EnqueueProjectionInsert]
ON [dms].[Document]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
END;
GO

CREATE TYPE [edfi].[SchoolIdList] AS TABLE ([SchoolId] bigint NOT NULL);
GO

CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [SchoolId] bigint NOT NULL,
    [NameOfInstitution] nvarchar(75) NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY CLUSTERED ([DocumentId]),
    CONSTRAINT [UX_School_SchoolId] UNIQUE ([SchoolId])
);

CREATE UNIQUE INDEX [UX_School_NameOfInstitution] ON [edfi].[School] ([NameOfInstitution]);

CREATE TABLE [tracked_changes_edfi].[School]
(
    [ChangeVersion] bigint NOT NULL,
    [SchoolId] bigint NOT NULL,
    CONSTRAINT [PK_tracked_changes_edfi_School] PRIMARY KEY CLUSTERED ([ChangeVersion])
);
GO

CREATE VIEW [edfi].[SchoolSummary]
AS
    SELECT [SchoolId], [NameOfInstitution] FROM [edfi].[School];
GO

INSERT INTO [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId], [SourceIdentity])
VALUES (1, '6f1c9a52-2d3e-4b7a-9c8d-1e2f3a4b5c6d');

INSERT INTO [dms].[EffectiveSchema] ([EffectiveSchemaSingletonId], [ApiSchemaFormatVersion], [EffectiveSchemaHash], [ResourceKeyCount], [ResourceKeySeedHash])
VALUES (1, N'1.0.0', N'abababababababababababababababababababababababababababababababab', 42, 0x0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20);

INSERT INTO [dms].[DocumentCacheState] ([StateId], [ProjectionLifecycleState], [CacheAheadRecoveryRequired])
VALUES (1, 'Tracking', 0);

INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
VALUES (1, N'Ed-Fi', N'School', N'5.2.0'), (2, N'Ed-Fi', N'GradeLevelDescriptor', N'5.2.0'), (3, N'Ed-Fi', N'SchoolYearType', N'5.2.0');

-- Only descriptor and school-year documents: the Populated predicate is false until a test adds a School document.
INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
VALUES ('0f3b2c1d-4e5f-4a6b-8c7d-9e0f1a2b3c4d', 2), ('1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d', 3);
GO

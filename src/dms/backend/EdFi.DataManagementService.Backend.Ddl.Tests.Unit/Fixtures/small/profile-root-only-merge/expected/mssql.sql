-- ==========================================================
-- Phase 0: Bounded Provisioning Guards
-- ==========================================================

-- Preflight: validate EffectiveSchema hash compatibility
DECLARE @preflight_stored_hash nvarchar(200);

IF OBJECT_ID(N'dms.EffectiveSchema', N'U') IS NOT NULL
BEGIN
    SELECT @preflight_stored_hash = [EffectiveSchemaHash] FROM [dms].[EffectiveSchema]
    WHERE [EffectiveSchemaSingletonId] = 1;
    IF @preflight_stored_hash IS NOT NULL AND @preflight_stored_hash <> N'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0'
    BEGIN
        DECLARE @preflight_msg nvarchar(500) = CONCAT(N'EffectiveSchemaHash mismatch: database has ''', @preflight_stored_hash, N''' but expected ''', N'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0', N'''');
        THROW 50000, @preflight_msg, 1;
    END
END

-- Preflight: protect completed DocumentCache mutable singleton state before mutation
DECLARE @preflight_completed_hash nvarchar(200);

IF OBJECT_ID(N'dms.EffectiveSchema', N'U') IS NOT NULL
BEGIN
    SELECT @preflight_completed_hash = [EffectiveSchemaHash] FROM [dms].[EffectiveSchema]
    WHERE [EffectiveSchemaSingletonId] = 1;
    IF @preflight_completed_hash = N'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0'
    BEGIN
        IF OBJECT_ID(N'dms.DataStoreIdentity', N'U') IS NULL
        BEGIN
            THROW 50000, N'Completed dms.EffectiveSchema hash matches this DDL, but dms.DataStoreIdentity is missing. Drop and recreate the database before re-provisioning.', 1;
        END

        DECLARE @preflight_source_identity uniqueidentifier;
        EXEC sys.sp_executesql
            N'SELECT @source_identity = [SourceIdentity] FROM [dms].[DataStoreIdentity] WHERE [DataStoreIdentitySingletonId] = 1;',
            N'@source_identity uniqueidentifier OUTPUT',
            @source_identity = @preflight_source_identity OUTPUT;
        IF @preflight_source_identity IS NULL
        BEGIN
            THROW 50000, N'Completed dms.EffectiveSchema hash matches this DDL, but dms.DataStoreIdentity singleton row is missing. Drop and recreate the database before re-provisioning.', 1;
        END
        IF @preflight_source_identity = CAST('00000000-0000-0000-0000-000000000000' AS uniqueidentifier)
        BEGIN
            THROW 50000, N'dms.DataStoreIdentity.SourceIdentity must not be the zero UUID. Drop and recreate the database before re-provisioning.', 1;
        END

        IF OBJECT_ID(N'dms.DocumentCacheState', N'U') IS NULL
        BEGIN
            THROW 50000, N'Completed dms.EffectiveSchema hash matches this DDL, but dms.DocumentCacheState is missing. Drop and recreate the database before re-provisioning.', 1;
        END

        DECLARE @preflight_lifecycle_state varchar(16);
        DECLARE @preflight_cache_ahead_recovery_required bit;
        EXEC sys.sp_executesql
            N'SELECT @lifecycle_state = [ProjectionLifecycleState], @cache_ahead_recovery_required = [CacheAheadRecoveryRequired] FROM [dms].[DocumentCacheState] WHERE [StateId] = 1;',
            N'@lifecycle_state varchar(16) OUTPUT, @cache_ahead_recovery_required bit OUTPUT',
            @lifecycle_state = @preflight_lifecycle_state OUTPUT,
            @cache_ahead_recovery_required = @preflight_cache_ahead_recovery_required OUTPUT;
        IF @preflight_lifecycle_state IS NULL
        BEGIN
            THROW 50000, N'Completed dms.EffectiveSchema hash matches this DDL, but dms.DocumentCacheState singleton row is missing. Drop and recreate the database before re-provisioning.', 1;
        END
        IF NOT (
            (@preflight_lifecycle_state COLLATE Latin1_General_100_BIN2 = 'Disabled' AND DATALENGTH(@preflight_lifecycle_state) = 8)
            OR (@preflight_lifecycle_state COLLATE Latin1_General_100_BIN2 = 'Resetting' AND DATALENGTH(@preflight_lifecycle_state) = 9)
            OR (@preflight_lifecycle_state COLLATE Latin1_General_100_BIN2 = 'Rebuilding' AND DATALENGTH(@preflight_lifecycle_state) = 10)
            OR (@preflight_lifecycle_state COLLATE Latin1_General_100_BIN2 = 'Tracking' AND DATALENGTH(@preflight_lifecycle_state) = 8)
        )
        BEGIN
            THROW 50000, N'dms.DocumentCacheState.ProjectionLifecycleState has unsupported value during provisioning preflight.', 1;
        END
        IF @preflight_cache_ahead_recovery_required IS NULL
        BEGIN
            THROW 50000, N'dms.DocumentCacheState.CacheAheadRecoveryRequired must not be null during provisioning preflight.', 1;
        END
    END
END

-- Preflight: reject known legacy DocumentCache artifacts before mutation
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dms.DocumentCache', N'U') AND name = N'Etag')
BEGIN
    THROW 50000, N'Known legacy artifact dms.DocumentCache.Etag was found. Drop and recreate the database before provisioning E18 DocumentCache schema.', 1;
END

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dms.DocumentCache', N'U') AND name = N'UX_DocumentCache_DocumentUuid')
OR EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dms.DocumentCache', N'U') AND name = N'UX_DocumentCache_DocumentUuid')
BEGIN
    THROW 50000, N'Known legacy artifact UX_DocumentCache_DocumentUuid was found. Drop and recreate the database before provisioning E18 DocumentCache schema.', 1;
END

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dms.DocumentCache', N'U') AND name = N'IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt')
BEGIN
    THROW 50000, N'Known legacy artifact IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt was found. Drop and recreate the database before provisioning E18 DocumentCache schema.', 1;
END

-- ==========================================================
-- Phase 1: Schemas
-- ==========================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dms')
    EXEC('CREATE SCHEMA [dms]');

-- ==========================================================
-- Phase 3: Sequences
-- ==========================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.sequences s
    JOIN sys.schemas sch ON s.schema_id = sch.schema_id
    WHERE sch.name = N'dms' AND s.name = N'ChangeVersionSequence'
)
CREATE SEQUENCE [dms].[ChangeVersionSequence] START WITH 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.sequences s
    JOIN sys.schemas sch ON s.schema_id = sch.schema_id
    WHERE sch.name = N'dms' AND s.name = N'CollectionItemIdSequence'
)
CREATE SEQUENCE [dms].[CollectionItemIdSequence] START WITH 1;

-- ==========================================================
-- Phase 4: Functions and Types
-- ==========================================================

GO
CREATE OR ALTER FUNCTION [dms].[GetMaxChangeVersion]()
RETURNS bigint
AS
BEGIN
    DECLARE @Result bigint;
    SELECT @Result = CONVERT(bigint, seq.current_value) FROM sys.sequences seq
    INNER JOIN sys.schemas sch
    ON seq.schema_id = sch.schema_id
    WHERE seq.name = 'ChangeVersionSequence' AND sch.name = 'dms';
    RETURN @Result;
END;
GO
CREATE OR ALTER FUNCTION [dms].[uuidv5](@namespace_uuid uniqueidentifier, @name_text nvarchar(max))
RETURNS uniqueidentifier
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @ns_bytes varbinary(16) = CAST(@namespace_uuid AS varbinary(16));

    -- Convert SQL Server mixed-endian to RFC 4122 big-endian for hashing
    DECLARE @ns_be varbinary(16) =
        SUBSTRING(@ns_bytes, 4, 1) + SUBSTRING(@ns_bytes, 3, 1)
        + SUBSTRING(@ns_bytes, 2, 1) + SUBSTRING(@ns_bytes, 1, 1)
        + SUBSTRING(@ns_bytes, 6, 1) + SUBSTRING(@ns_bytes, 5, 1)
        + SUBSTRING(@ns_bytes, 8, 1) + SUBSTRING(@ns_bytes, 7, 1)
        + SUBSTRING(@ns_bytes, 9, 8);

    -- Apply UTF-8 collation to the nvarchar source before casting to varchar so the
    -- conversion uses code page 65001 directly, matching Core (Encoding.UTF8) and
    -- PostgreSQL (convert_to ... 'UTF8') for non-ASCII characters.
    DECLARE @name_bytes varbinary(max) = CAST(CAST(@name_text COLLATE Latin1_General_100_CI_AS_SC_UTF8 AS varchar(max)) AS varbinary(max));

    DECLARE @hash varbinary(20) = HASHBYTES('SHA1', @ns_be + @name_bytes);

    -- Take first 16 bytes and set version/variant bits
    DECLARE @result varbinary(16) = SUBSTRING(@hash, 1, 16);

    DECLARE @byte6 int = CAST(SUBSTRING(@result, 7, 1) AS int);
    SET @result = SUBSTRING(@result, 1, 6)
        + CAST((@byte6 & 0x0F) | 0x50 AS binary(1))
        + SUBSTRING(@result, 8, 9);

    DECLARE @byte8 int = CAST(SUBSTRING(@result, 9, 1) AS int);
    SET @result = SUBSTRING(@result, 1, 8)
        + CAST((@byte8 & 0x3F) | 0x80 AS binary(1))
        + SUBSTRING(@result, 10, 7);

    -- Convert big-endian result back to SQL Server mixed-endian
    RETURN CAST(
        SUBSTRING(@result, 4, 1) + SUBSTRING(@result, 3, 1)
        + SUBSTRING(@result, 2, 1) + SUBSTRING(@result, 1, 1)
        + SUBSTRING(@result, 6, 1) + SUBSTRING(@result, 5, 1)
        + SUBSTRING(@result, 8, 1) + SUBSTRING(@result, 7, 1)
        + SUBSTRING(@result, 9, 8)
        AS uniqueidentifier);
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.types t
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms'
      AND t.name = N'BigIntTable'
      AND t.is_table_type = 1
)
CREATE TYPE [dms].[BigIntTable] AS TABLE(
    [Id] bigint NOT NULL
);

IF NOT EXISTS (
    SELECT 1 FROM sys.types t
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms'
      AND t.name = N'UniqueIdentifierTable'
      AND t.is_table_type = 1
)
CREATE TYPE [dms].[UniqueIdentifierTable] AS TABLE(
    [Id] uniqueidentifier NOT NULL
);

-- ==========================================================
-- Phase 5: Tables (PK/UNIQUE/CHECK only, no cross-table FKs)
-- ==========================================================

IF OBJECT_ID(N'dms.DataStoreIdentity', N'U') IS NULL
CREATE TABLE [dms].[DataStoreIdentity]
(
    [DataStoreIdentitySingletonId] smallint NOT NULL,
    [SourceIdentity] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_DataStoreIdentity] PRIMARY KEY CLUSTERED ([DataStoreIdentitySingletonId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_DataStoreIdentity_Singleton' AND parent_object_id = OBJECT_ID(N'dms.DataStoreIdentity')
)
ALTER TABLE [dms].[DataStoreIdentity]
ADD CONSTRAINT [CK_DataStoreIdentity_Singleton] CHECK ([DataStoreIdentitySingletonId] = 1);

IF OBJECT_ID(N'dms.Descriptor', N'U') IS NULL
CREATE TABLE [dms].[Descriptor]
(
    [DocumentId] bigint NOT NULL,
    [ResourceKeyId] smallint NOT NULL,
    [Namespace] nvarchar(255) NOT NULL,
    [CodeValue] nvarchar(50) NOT NULL,
    [ShortDescription] nvarchar(75) NOT NULL,
    [Description] nvarchar(1024) NULL,
    [EffectiveBeginDate] date NULL,
    [EffectiveEndDate] date NULL,
    [Discriminator] nvarchar(128) NOT NULL,
    [Uri] nvarchar(306) NOT NULL,
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_Descriptor_ContentVersion] DEFAULT 0,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Descriptor_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_Descriptor] PRIMARY KEY CLUSTERED ([DocumentId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE name = N'UX_Descriptor_Uri_Discriminator' AND type = 'UQ' AND parent_object_id = OBJECT_ID(N'dms.Descriptor')
)
ALTER TABLE [dms].[Descriptor]
ADD CONSTRAINT [UX_Descriptor_Uri_Discriminator] UNIQUE ([Uri], [Discriminator]);

IF OBJECT_ID(N'dms.Document', N'U') IS NULL
CREATE TABLE [dms].[Document]
(
    [DocumentId] bigint IDENTITY(1,1) NOT NULL,
    [DocumentUuid] uniqueidentifier NOT NULL,
    [ResourceKeyId] smallint NOT NULL,
    [CreatedByOwnershipTokenId] smallint NULL,
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_Document_ContentVersion] DEFAULT (NEXT VALUE FOR [dms].[ChangeVersionSequence]),
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Document_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Document_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_Document] PRIMARY KEY CLUSTERED ([DocumentId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE name = N'UX_Document_DocumentUuid' AND type = 'UQ' AND parent_object_id = OBJECT_ID(N'dms.Document')
)
ALTER TABLE [dms].[Document]
ADD CONSTRAINT [UX_Document_DocumentUuid] UNIQUE ([DocumentUuid]);

IF OBJECT_ID(N'dms.DocumentCache', N'U') IS NULL
CREATE TABLE [dms].[DocumentCache]
(
    [DocumentId] bigint NOT NULL,
    [DocumentUuid] uniqueidentifier NOT NULL,
    [ProjectName] nvarchar(256) NOT NULL,
    [ResourceName] nvarchar(256) NOT NULL,
    [ResourceVersion] nvarchar(32) NOT NULL,
    [ContentVersion] bigint NOT NULL,
    [StreamEtag] varchar(64) NOT NULL,
    [LastModifiedAt] datetime2(7) NOT NULL,
    [DocumentJson] nvarchar(max) NOT NULL,
    [ComputedAt] datetime2(7) NOT NULL CONSTRAINT [DF_DocumentCache_ComputedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_DocumentCache] PRIMARY KEY CLUSTERED ([DocumentId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_DocumentCache_IsJsonObject' AND parent_object_id = OBJECT_ID(N'dms.DocumentCache')
)
ALTER TABLE [dms].[DocumentCache]
ADD CONSTRAINT [CK_DocumentCache_IsJsonObject] CHECK (ISJSON([DocumentJson]) = 1 AND LEFT(LTRIM([DocumentJson]), 1) = '{');

IF OBJECT_ID(N'dms.DocumentCacheState', N'U') IS NULL
CREATE TABLE [dms].[DocumentCacheState]
(
    [StateId] smallint NOT NULL,
    [ProjectionLifecycleState] varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [CacheAheadRecoveryRequired] bit NOT NULL,
    CONSTRAINT [PK_DocumentCacheState] PRIMARY KEY CLUSTERED ([StateId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_DocumentCacheState_Singleton' AND parent_object_id = OBJECT_ID(N'dms.DocumentCacheState')
)
ALTER TABLE [dms].[DocumentCacheState]
ADD CONSTRAINT [CK_DocumentCacheState_Singleton] CHECK ([StateId] = 1);

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_DocumentCacheState_Lifecycle' AND parent_object_id = OBJECT_ID(N'dms.DocumentCacheState')
)
ALTER TABLE [dms].[DocumentCacheState]
ADD CONSTRAINT [CK_DocumentCacheState_Lifecycle] CHECK (([ProjectionLifecycleState] = 'Disabled' AND DATALENGTH([ProjectionLifecycleState]) = 8) OR ([ProjectionLifecycleState] = 'Resetting' AND DATALENGTH([ProjectionLifecycleState]) = 9) OR ([ProjectionLifecycleState] = 'Rebuilding' AND DATALENGTH([ProjectionLifecycleState]) = 10) OR ([ProjectionLifecycleState] = 'Tracking' AND DATALENGTH([ProjectionLifecycleState]) = 8));

IF OBJECT_ID(N'dms.DocumentProjectionWork', N'U') IS NULL
CREATE TABLE [dms].[DocumentProjectionWork]
(
    [DocumentId] bigint NOT NULL,
    [RequiredContentVersion] bigint NOT NULL,
    [FirstEnqueuedAt] datetime2(7) NOT NULL,
    [LastEnqueuedAt] datetime2(7) NOT NULL,
    CONSTRAINT [PK_DocumentProjectionWork] PRIMARY KEY CLUSTERED ([DocumentId])
);

IF OBJECT_ID(N'dms.EffectiveSchema', N'U') IS NULL
CREATE TABLE [dms].[EffectiveSchema]
(
    [EffectiveSchemaSingletonId] smallint NOT NULL,
    [ApiSchemaFormatVersion] nvarchar(64) NOT NULL,
    [EffectiveSchemaHash] nvarchar(64) NOT NULL,
    [ResourceKeyCount] smallint NOT NULL,
    [ResourceKeySeedHash] binary(32) NOT NULL,
    [AppliedAt] datetime2(7) NOT NULL CONSTRAINT [DF_EffectiveSchema_AppliedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_EffectiveSchema] PRIMARY KEY CLUSTERED ([EffectiveSchemaSingletonId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_EffectiveSchema_Singleton' AND parent_object_id = OBJECT_ID(N'dms.EffectiveSchema')
)
ALTER TABLE [dms].[EffectiveSchema]
ADD CONSTRAINT [CK_EffectiveSchema_Singleton] CHECK ([EffectiveSchemaSingletonId] = 1);

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_EffectiveSchema_ApiSchemaFormatVersion_NotBlank' AND parent_object_id = OBJECT_ID(N'dms.EffectiveSchema')
)
ALTER TABLE [dms].[EffectiveSchema]
ADD CONSTRAINT [CK_EffectiveSchema_ApiSchemaFormatVersion_NotBlank] CHECK (LEN(LTRIM(RTRIM([ApiSchemaFormatVersion]))) > 0);

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE name = N'UX_EffectiveSchema_EffectiveSchemaHash' AND type = 'UQ' AND parent_object_id = OBJECT_ID(N'dms.EffectiveSchema')
)
ALTER TABLE [dms].[EffectiveSchema]
ADD CONSTRAINT [UX_EffectiveSchema_EffectiveSchemaHash] UNIQUE ([EffectiveSchemaHash]);

IF OBJECT_ID(N'dms.ReferentialIdentity', N'U') IS NULL
CREATE TABLE [dms].[ReferentialIdentity]
(
    [ReferentialId] uniqueidentifier NOT NULL,
    [DocumentId] bigint NOT NULL,
    [ResourceKeyId] smallint NOT NULL,
    CONSTRAINT [PK_ReferentialIdentity] PRIMARY KEY NONCLUSTERED ([ReferentialId]),
    CONSTRAINT [UX_ReferentialIdentity_DocumentId_ResourceKeyId] UNIQUE CLUSTERED ([DocumentId], [ResourceKeyId])
);

IF OBJECT_ID(N'dms.ResourceKey', N'U') IS NULL
CREATE TABLE [dms].[ResourceKey]
(
    [ResourceKeyId] smallint NOT NULL,
    [ProjectName] nvarchar(256) NOT NULL,
    [ResourceName] nvarchar(256) NOT NULL,
    [ResourceVersion] nvarchar(32) NOT NULL,
    CONSTRAINT [PK_ResourceKey] PRIMARY KEY CLUSTERED ([ResourceKeyId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE name = N'UX_ResourceKey_ProjectName_ResourceName' AND type = 'UQ' AND parent_object_id = OBJECT_ID(N'dms.ResourceKey')
)
ALTER TABLE [dms].[ResourceKey]
ADD CONSTRAINT [UX_ResourceKey_ProjectName_ResourceName] UNIQUE ([ProjectName], [ResourceName]);

IF OBJECT_ID(N'dms.SchemaComponent', N'U') IS NULL
CREATE TABLE [dms].[SchemaComponent]
(
    [EffectiveSchemaHash] nvarchar(64) NOT NULL,
    [ProjectEndpointName] nvarchar(128) NOT NULL,
    [ProjectName] nvarchar(256) NOT NULL,
    [ProjectVersion] nvarchar(32) NOT NULL,
    [IsExtensionProject] bit NOT NULL,
    CONSTRAINT [PK_SchemaComponent] PRIMARY KEY CLUSTERED ([EffectiveSchemaHash], [ProjectEndpointName])
);

-- ==========================================================
-- Phase 6: Foreign Keys
-- ==========================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_Descriptor_Document' AND parent_object_id = OBJECT_ID(N'dms.Descriptor')
)
ALTER TABLE [dms].[Descriptor]
ADD CONSTRAINT [FK_Descriptor_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_Descriptor_ResourceKey' AND parent_object_id = OBJECT_ID(N'dms.Descriptor')
)
ALTER TABLE [dms].[Descriptor]
ADD CONSTRAINT [FK_Descriptor_ResourceKey]
FOREIGN KEY ([ResourceKeyId])
REFERENCES [dms].[ResourceKey] ([ResourceKeyId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_Document_ResourceKey' AND parent_object_id = OBJECT_ID(N'dms.Document')
)
ALTER TABLE [dms].[Document]
ADD CONSTRAINT [FK_Document_ResourceKey]
FOREIGN KEY ([ResourceKeyId])
REFERENCES [dms].[ResourceKey] ([ResourceKeyId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_DocumentCache_Document' AND parent_object_id = OBJECT_ID(N'dms.DocumentCache')
)
ALTER TABLE [dms].[DocumentCache]
ADD CONSTRAINT [FK_DocumentCache_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_DocumentProjectionWork_Document' AND parent_object_id = OBJECT_ID(N'dms.DocumentProjectionWork')
)
ALTER TABLE [dms].[DocumentProjectionWork]
ADD CONSTRAINT [FK_DocumentProjectionWork_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_ReferentialIdentity_Document' AND parent_object_id = OBJECT_ID(N'dms.ReferentialIdentity')
)
ALTER TABLE [dms].[ReferentialIdentity]
ADD CONSTRAINT [FK_ReferentialIdentity_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_ReferentialIdentity_ResourceKey' AND parent_object_id = OBJECT_ID(N'dms.ReferentialIdentity')
)
ALTER TABLE [dms].[ReferentialIdentity]
ADD CONSTRAINT [FK_ReferentialIdentity_ResourceKey]
FOREIGN KEY ([ResourceKeyId])
REFERENCES [dms].[ResourceKey] ([ResourceKeyId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_SchemaComponent_EffectiveSchemaHash' AND parent_object_id = OBJECT_ID(N'dms.SchemaComponent')
)
ALTER TABLE [dms].[SchemaComponent]
ADD CONSTRAINT [FK_SchemaComponent_EffectiveSchemaHash]
FOREIGN KEY ([EffectiveSchemaHash])
REFERENCES [dms].[EffectiveSchema] ([EffectiveSchemaHash])
ON DELETE CASCADE
ON UPDATE NO ACTION;

-- ==========================================================
-- Phase 7: Indexes
-- ==========================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms' AND t.name = N'Descriptor' AND i.name = N'IX_Descriptor_ResourceKeyId_DocumentId'
)
CREATE INDEX [IX_Descriptor_ResourceKeyId_DocumentId] ON [dms].[Descriptor] ([ResourceKeyId], [DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms' AND t.name = N'Document' AND i.name = N'IX_Document_CreatedByOwnershipTokenId'
)
CREATE INDEX [IX_Document_CreatedByOwnershipTokenId] ON [dms].[Document] ([CreatedByOwnershipTokenId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms' AND t.name = N'DocumentProjectionWork' AND i.name = N'IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId'
)
CREATE INDEX [IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId] ON [dms].[DocumentProjectionWork] ([FirstEnqueuedAt], [DocumentId]);

-- ==========================================================
-- Phase 8: Triggers
-- ==========================================================

GO
CREATE OR ALTER TRIGGER [dms].[TR_Descriptor_Stamp_Document]
ON [dms].[Descriptor]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        INNER JOIN [dms].[Document] d ON d.[DocumentId] = i.[DocumentId]
        WHERE i.[ResourceKeyId] <> d.[ResourceKeyId]
    )
    BEGIN
        THROW 50000, N'dms.Descriptor.ResourceKeyId diverges from the owning dms.Document row.', 1;
    END
    DECLARE @stamped TABLE (
        [DocumentId] bigint NOT NULL PRIMARY KEY,
        [ContentVersion] bigint NOT NULL,
        [ContentLastModifiedAt] datetime2(7) NOT NULL
    );
    INSERT INTO @stamped ([DocumentId], [ContentVersion], [ContentLastModifiedAt])
    SELECT d.[DocumentId], d.[ContentVersion], d.[ContentLastModifiedAt]
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
    LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
    WHERE del.[DocumentId] IS NULL;
    IF EXISTS (SELECT 1 FROM deleted) AND (NOT EXISTS (SELECT 1 FROM inserted) OR UPDATE([Namespace]) OR UPDATE([CodeValue]) OR UPDATE([ShortDescription]) OR UPDATE([Description]) OR UPDATE([EffectiveBeginDate]) OR UPDATE([EffectiveEndDate]) OR UPDATE([Discriminator]) OR UPDATE([Uri]))
    BEGIN
        ;WITH affectedDocs AS (
            SELECT i.[DocumentId]
            FROM inserted i
            LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
            WHERE del.[DocumentId] IS NOT NULL AND ((CAST(i.[Namespace] AS varbinary(max)) <> CAST(del.[Namespace] AS varbinary(max)) OR (i.[Namespace] IS NULL AND del.[Namespace] IS NOT NULL) OR (i.[Namespace] IS NOT NULL AND del.[Namespace] IS NULL)) OR (CAST(i.[CodeValue] AS varbinary(max)) <> CAST(del.[CodeValue] AS varbinary(max)) OR (i.[CodeValue] IS NULL AND del.[CodeValue] IS NOT NULL) OR (i.[CodeValue] IS NOT NULL AND del.[CodeValue] IS NULL)) OR (CAST(i.[ShortDescription] AS varbinary(max)) <> CAST(del.[ShortDescription] AS varbinary(max)) OR (i.[ShortDescription] IS NULL AND del.[ShortDescription] IS NOT NULL) OR (i.[ShortDescription] IS NOT NULL AND del.[ShortDescription] IS NULL)) OR (CAST(i.[Description] AS varbinary(max)) <> CAST(del.[Description] AS varbinary(max)) OR (i.[Description] IS NULL AND del.[Description] IS NOT NULL) OR (i.[Description] IS NOT NULL AND del.[Description] IS NULL)) OR (i.[EffectiveBeginDate] <> del.[EffectiveBeginDate] OR (i.[EffectiveBeginDate] IS NULL AND del.[EffectiveBeginDate] IS NOT NULL) OR (i.[EffectiveBeginDate] IS NOT NULL AND del.[EffectiveBeginDate] IS NULL)) OR (i.[EffectiveEndDate] <> del.[EffectiveEndDate] OR (i.[EffectiveEndDate] IS NULL AND del.[EffectiveEndDate] IS NOT NULL) OR (i.[EffectiveEndDate] IS NOT NULL AND del.[EffectiveEndDate] IS NULL)) OR (CAST(i.[Discriminator] AS varbinary(max)) <> CAST(del.[Discriminator] AS varbinary(max)) OR (i.[Discriminator] IS NULL AND del.[Discriminator] IS NOT NULL) OR (i.[Discriminator] IS NOT NULL AND del.[Discriminator] IS NULL)) OR (CAST(i.[Uri] AS varbinary(max)) <> CAST(del.[Uri] AS varbinary(max)) OR (i.[Uri] IS NULL AND del.[Uri] IS NOT NULL) OR (i.[Uri] IS NOT NULL AND del.[Uri] IS NULL)))
            UNION ALL
            SELECT del.[DocumentId]
            FROM deleted del
            LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
            WHERE i.[DocumentId] IS NULL
        )
        UPDATE d
        SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
        OUTPUT inserted.[DocumentId], inserted.[ContentVersion], inserted.[ContentLastModifiedAt] INTO @stamped
        FROM [dms].[Document] d
        INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [dms].[Descriptor] r WITH (FORCESEEK)
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[Descriptor] (
            [Discriminator],
            [OldNamespace],
            [OldCodeValue],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[Discriminator],
            del.[Namespace],
            del.[CodeValue],
            doc.[DocumentUuid],
            doc.[ContentVersion]
        FROM deleted del
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = del.[DocumentId];
    END
END;
GO

GO
CREATE OR ALTER TRIGGER [dms].[TR_Document_EnqueueProjectionWork]
ON [dms].[Document]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lifecycleState varchar(16);
    SELECT @lifecycleState = [ProjectionLifecycleState]
    FROM [dms].[DocumentCacheState]
    WHERE [StateId] = 1;

    IF @lifecycleState IS NULL
    BEGIN
        THROW 50000, N'dms.DocumentCacheState singleton row is missing or unreadable for projection enqueue.', 1;
    END

    IF NOT (
        (@lifecycleState COLLATE Latin1_General_100_BIN2 = 'Disabled' AND DATALENGTH(@lifecycleState) = 8)
        OR (@lifecycleState COLLATE Latin1_General_100_BIN2 = 'Resetting' AND DATALENGTH(@lifecycleState) = 9)
        OR (@lifecycleState COLLATE Latin1_General_100_BIN2 = 'Rebuilding' AND DATALENGTH(@lifecycleState) = 10)
        OR (@lifecycleState COLLATE Latin1_General_100_BIN2 = 'Tracking' AND DATALENGTH(@lifecycleState) = 8)
    )
    BEGIN
        THROW 50000, N'dms.DocumentCacheState.ProjectionLifecycleState has unsupported value for projection enqueue.', 1;
    END

    IF @lifecycleState COLLATE Latin1_General_100_BIN2 = 'Disabled' AND DATALENGTH(@lifecycleState) = 8
    BEGIN
        RETURN;
    END

    DECLARE @required TABLE (
        [DocumentId] bigint NOT NULL PRIMARY KEY,
        [RequiredContentVersion] bigint NOT NULL
    );

    INSERT INTO @required ([DocumentId], [RequiredContentVersion])
    SELECT i.[DocumentId], MAX(i.[ContentVersion])
    FROM inserted i
    LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
    WHERE del.[DocumentId] IS NULL OR i.[ContentVersion] <> del.[ContentVersion]
    GROUP BY i.[DocumentId];

    DECLARE @enqueuedAt datetime2(7) = SYSUTCDATETIME();

    UPDATE work
    SET work.[RequiredContentVersion] = req.[RequiredContentVersion],
        work.[LastEnqueuedAt] = @enqueuedAt
    FROM @required req
    INNER LOOP JOIN [dms].[DocumentProjectionWork] work WITH (UPDLOCK, ROWLOCK) ON work.[DocumentId] = req.[DocumentId]
    WHERE work.[RequiredContentVersion] < req.[RequiredContentVersion]
    OPTION (FORCE ORDER);

    INSERT INTO [dms].[DocumentProjectionWork] ([DocumentId], [RequiredContentVersion], [FirstEnqueuedAt], [LastEnqueuedAt])
    SELECT req.[DocumentId], req.[RequiredContentVersion], @enqueuedAt, @enqueuedAt
    FROM @required req
    WHERE NOT EXISTS (
        SELECT 1
        FROM [dms].[DocumentProjectionWork] work WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE work.[DocumentId] = req.[DocumentId]
    );
END;
GO

GO
CREATE OR ALTER TRIGGER [dms].[TR_DocumentCache_ValidateDocumentUuid]
ON [dms].[DocumentCache]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        INNER JOIN [dms].[Document] d ON d.[DocumentId] = i.[DocumentId]
        WHERE i.[DocumentUuid] <> d.[DocumentUuid]
    )
    BEGIN
        THROW 50000, N'dms.DocumentCache.DocumentUuid diverges from the owning dms.Document row.', 1;
    END
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'tracked_changes_edfi')
    EXEC('CREATE SCHEMA [tracked_changes_edfi]');

IF OBJECT_ID(N'edfi.ProfileRootOnlyMergeItem', N'U') IS NULL
CREATE TABLE [edfi].[ProfileRootOnlyMergeItem]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_ProfileRootOnlyMergeItem_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_ProfileRootOnlyMergeItem_ContentVersion] DEFAULT 0,
    [PrimarySchoolTypeDescriptor_DescriptorId_Present] bit NULL,
    [PrimarySchoolTypeDescriptor_Unified_DescriptorId] bigint NULL,
    [SecondarySchoolTypeDescriptor_DescriptorId_Present] bit NULL,
    [StudentReference_DocumentId] bigint NULL,
    [StudentReference_StudentUniqueId] nvarchar(32) NULL,
    [PrimarySchoolTypeDescriptor_DescriptorId] AS (CASE WHEN [PrimarySchoolTypeDescriptor_DescriptorId_Present] IS NULL THEN NULL ELSE [PrimarySchoolTypeDescriptor_Unified_DescriptorId] END) PERSISTED,
    [SecondarySchoolTypeDescriptor_DescriptorId] AS (CASE WHEN [SecondarySchoolTypeDescriptor_DescriptorId_Present] IS NULL THEN NULL ELSE [PrimarySchoolTypeDescriptor_Unified_DescriptorId] END) PERSISTED,
    [DisplayName] nvarchar(100) NULL,
    [ProfileRootOnlyMergeItemId] int NOT NULL,
    [ProfileScopeClearableText] nvarchar(100) NULL,
    [ProfileScopePreservedText] nvarchar(100) NULL,
    CONSTRAINT [PK_ProfileRootOnlyMergeItem] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_ProfileRootOnlyMergeItem_NK] UNIQUE ([ProfileRootOnlyMergeItemId]),
    CONSTRAINT [CK_ProfileRootOnlyMergeItem_StudentReference_AllNone] CHECK (([StudentReference_DocumentId] IS NULL AND [StudentReference_StudentUniqueId] IS NULL) OR ([StudentReference_DocumentId] IS NOT NULL AND [StudentReference_StudentUniqueId] IS NOT NULL)),
    CONSTRAINT [CK_ProfileRootOnlyMergeItem_PrimarySchoolTypeDescriptor_DescriptorId_Present_NullOrTrue] CHECK ([PrimarySchoolTypeDescriptor_DescriptorId_Present] IS NULL OR [PrimarySchoolTypeDescriptor_DescriptorId_Present] = 1),
    CONSTRAINT [CK_ProfileRootOnlyMergeItem_SecondarySchoolTypeDescriptor_DescriptorId_Present_NullOrTrue] CHECK ([SecondarySchoolTypeDescriptor_DescriptorId_Present] IS NULL OR [SecondarySchoolTypeDescriptor_DescriptorId_Present] = 1)
);

IF OBJECT_ID(N'edfi.Student', N'U') IS NULL
CREATE TABLE [edfi].[Student]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Student_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_Student_ContentVersion] DEFAULT 0,
    [FirstName] nvarchar(75) NOT NULL,
    [StudentUniqueId] nvarchar(32) NOT NULL,
    CONSTRAINT [PK_Student] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_Student_NK] UNIQUE ([StudentUniqueId]),
    CONSTRAINT [UX_Student_RefKey] UNIQUE ([StudentUniqueId], [DocumentId])
);

IF OBJECT_ID(N'tracked_changes_edfi.Descriptor', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[Descriptor]
(
    [OldNamespace] nvarchar(255) NOT NULL,
    [NewNamespace] nvarchar(255) NULL,
    [OldCodeValue] nvarchar(50) NOT NULL,
    [NewCodeValue] nvarchar(50) NULL,
    [Discriminator] nvarchar(128) NOT NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_Descriptor_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_Descriptor] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.ProfileRootOnlyMergeItem', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[ProfileRootOnlyMergeItem]
(
    [OldProfileRootOnlyMergeItemId] int NOT NULL,
    [NewProfileRootOnlyMergeItemId] int NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_ProfileRootOnlyMergeItem_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_ProfileRootOnlyMergeItem] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.Student', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[Student]
(
    [OldStudentUniqueId] nvarchar(32) NOT NULL,
    [NewStudentUniqueId] nvarchar(32) NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_Student_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_Student] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_ProfileRootOnlyMergeItem_Document' AND parent_object_id = OBJECT_ID(N'edfi.ProfileRootOnlyMergeItem')
)
ALTER TABLE [edfi].[ProfileRootOnlyMergeItem]
ADD CONSTRAINT [FK_ProfileRootOnlyMergeItem_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_ProfileRootOnlyMergeItem_PrimarySchoolTypeDescriptor_Unified' AND parent_object_id = OBJECT_ID(N'edfi.ProfileRootOnlyMergeItem')
)
ALTER TABLE [edfi].[ProfileRootOnlyMergeItem]
ADD CONSTRAINT [FK_ProfileRootOnlyMergeItem_PrimarySchoolTypeDescriptor_Unified]
FOREIGN KEY ([PrimarySchoolTypeDescriptor_Unified_DescriptorId])
REFERENCES [dms].[Descriptor] ([DocumentId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_ProfileRootOnlyMergeItem_StudentReference_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.ProfileRootOnlyMergeItem')
)
ALTER TABLE [edfi].[ProfileRootOnlyMergeItem]
ADD CONSTRAINT [FK_ProfileRootOnlyMergeItem_StudentReference_RefKey]
FOREIGN KEY ([StudentReference_StudentUniqueId], [StudentReference_DocumentId])
REFERENCES [edfi].[Student] ([StudentUniqueId], [DocumentId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_Student_Document' AND parent_object_id = OBJECT_ID(N'edfi.Student')
)
ALTER TABLE [edfi].[Student]
ADD CONSTRAINT [FK_Student_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms' AND t.name = N'Descriptor' AND i.name = N'IX_Descriptor_Discriminator_ContentVersion'
)
CREATE INDEX [IX_Descriptor_Discriminator_ContentVersion] ON [dms].[Descriptor] ([Discriminator], [ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms' AND t.name = N'Descriptor' AND i.name = N'IX_Descriptor_ResourceKeyId_ContentVersion_DocumentId'
)
CREATE INDEX [IX_Descriptor_ResourceKeyId_ContentVersion_DocumentId] ON [dms].[Descriptor] ([ResourceKeyId], [ContentVersion], [DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'ProfileRootOnlyMergeItem' AND i.name = N'IX_ProfileRootOnlyMergeItem_ContentVersion'
)
CREATE INDEX [IX_ProfileRootOnlyMergeItem_ContentVersion] ON [edfi].[ProfileRootOnlyMergeItem] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'ProfileRootOnlyMergeItem' AND i.name = N'IX_ProfileRootOnlyMergeItem_PrimarySchoolTypeDescriptor_Unified_DescriptorId'
)
CREATE INDEX [IX_ProfileRootOnlyMergeItem_PrimarySchoolTypeDescriptor_Unified_DescriptorId] ON [edfi].[ProfileRootOnlyMergeItem] ([PrimarySchoolTypeDescriptor_Unified_DescriptorId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'ProfileRootOnlyMergeItem' AND i.name = N'IX_ProfileRootOnlyMergeItem_StudentReference_StudentUniqueId_StudentReference_DocumentId'
)
CREATE INDEX [IX_ProfileRootOnlyMergeItem_StudentReference_StudentUniqueId_StudentReference_DocumentId] ON [edfi].[ProfileRootOnlyMergeItem] ([StudentReference_StudentUniqueId], [StudentReference_DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'Student' AND i.name = N'IX_Student_ContentVersion'
)
CREATE INDEX [IX_Student_ContentVersion] ON [edfi].[Student] ([ContentVersion]);

GO
CREATE OR ALTER TRIGGER [edfi].[TR_ProfileRootOnlyMergeItem_ReferentialIdentity]
ON [edfi].[ProfileRootOnlyMergeItem]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiProfileRootOnlyMergeItem' AS nvarchar(max)) + N'$.profileRootOnlyMergeItemId=' + CAST(i.[ProfileRootOnlyMergeItemId] AS nvarchar(max))), i.[DocumentId], 1
        FROM inserted i;
    END
    ELSE IF (UPDATE([ProfileRootOnlyMergeItemId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[ProfileRootOnlyMergeItemId] <> d.[ProfileRootOnlyMergeItemId] OR (i.[ProfileRootOnlyMergeItemId] IS NULL AND d.[ProfileRootOnlyMergeItemId] IS NOT NULL) OR (i.[ProfileRootOnlyMergeItemId] IS NOT NULL AND d.[ProfileRootOnlyMergeItemId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiProfileRootOnlyMergeItem' AS nvarchar(max)) + N'$.profileRootOnlyMergeItemId=' + CAST(i.[ProfileRootOnlyMergeItemId] AS nvarchar(max))), i.[DocumentId], 1
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_ProfileRootOnlyMergeItem_Stamp]
ON [edfi].[ProfileRootOnlyMergeItem]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @stamped TABLE (
        [DocumentId] bigint NOT NULL PRIMARY KEY,
        [ContentVersion] bigint NOT NULL,
        [ContentLastModifiedAt] datetime2(7) NOT NULL
    );
    INSERT INTO @stamped ([DocumentId], [ContentVersion], [ContentLastModifiedAt])
    SELECT d.[DocumentId], d.[ContentVersion], d.[ContentLastModifiedAt]
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
    LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
    WHERE del.[DocumentId] IS NULL;
    IF EXISTS (SELECT 1 FROM deleted) AND (NOT EXISTS (SELECT 1 FROM inserted) OR UPDATE([DocumentId]) OR UPDATE([PrimarySchoolTypeDescriptor_DescriptorId_Present]) OR UPDATE([PrimarySchoolTypeDescriptor_Unified_DescriptorId]) OR UPDATE([SecondarySchoolTypeDescriptor_DescriptorId_Present]) OR UPDATE([StudentReference_DocumentId]) OR UPDATE([StudentReference_StudentUniqueId]) OR UPDATE([DisplayName]) OR UPDATE([ProfileRootOnlyMergeItemId]) OR UPDATE([ProfileScopeClearableText]) OR UPDATE([ProfileScopePreservedText]))
    BEGIN
        ;WITH affectedDocs AS (
            SELECT i.[DocumentId]
            FROM inserted i
            LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
            WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[PrimarySchoolTypeDescriptor_DescriptorId_Present] <> del.[PrimarySchoolTypeDescriptor_DescriptorId_Present] OR (i.[PrimarySchoolTypeDescriptor_DescriptorId_Present] IS NULL AND del.[PrimarySchoolTypeDescriptor_DescriptorId_Present] IS NOT NULL) OR (i.[PrimarySchoolTypeDescriptor_DescriptorId_Present] IS NOT NULL AND del.[PrimarySchoolTypeDescriptor_DescriptorId_Present] IS NULL)) OR (i.[PrimarySchoolTypeDescriptor_Unified_DescriptorId] <> del.[PrimarySchoolTypeDescriptor_Unified_DescriptorId] OR (i.[PrimarySchoolTypeDescriptor_Unified_DescriptorId] IS NULL AND del.[PrimarySchoolTypeDescriptor_Unified_DescriptorId] IS NOT NULL) OR (i.[PrimarySchoolTypeDescriptor_Unified_DescriptorId] IS NOT NULL AND del.[PrimarySchoolTypeDescriptor_Unified_DescriptorId] IS NULL)) OR (i.[SecondarySchoolTypeDescriptor_DescriptorId_Present] <> del.[SecondarySchoolTypeDescriptor_DescriptorId_Present] OR (i.[SecondarySchoolTypeDescriptor_DescriptorId_Present] IS NULL AND del.[SecondarySchoolTypeDescriptor_DescriptorId_Present] IS NOT NULL) OR (i.[SecondarySchoolTypeDescriptor_DescriptorId_Present] IS NOT NULL AND del.[SecondarySchoolTypeDescriptor_DescriptorId_Present] IS NULL)) OR (i.[StudentReference_DocumentId] <> del.[StudentReference_DocumentId] OR (i.[StudentReference_DocumentId] IS NULL AND del.[StudentReference_DocumentId] IS NOT NULL) OR (i.[StudentReference_DocumentId] IS NOT NULL AND del.[StudentReference_DocumentId] IS NULL)) OR (CAST(i.[StudentReference_StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentReference_StudentUniqueId] AS varbinary(max)) OR (i.[StudentReference_StudentUniqueId] IS NULL AND del.[StudentReference_StudentUniqueId] IS NOT NULL) OR (i.[StudentReference_StudentUniqueId] IS NOT NULL AND del.[StudentReference_StudentUniqueId] IS NULL)) OR (CAST(i.[DisplayName] AS varbinary(max)) <> CAST(del.[DisplayName] AS varbinary(max)) OR (i.[DisplayName] IS NULL AND del.[DisplayName] IS NOT NULL) OR (i.[DisplayName] IS NOT NULL AND del.[DisplayName] IS NULL)) OR (i.[ProfileRootOnlyMergeItemId] <> del.[ProfileRootOnlyMergeItemId] OR (i.[ProfileRootOnlyMergeItemId] IS NULL AND del.[ProfileRootOnlyMergeItemId] IS NOT NULL) OR (i.[ProfileRootOnlyMergeItemId] IS NOT NULL AND del.[ProfileRootOnlyMergeItemId] IS NULL)) OR (CAST(i.[ProfileScopeClearableText] AS varbinary(max)) <> CAST(del.[ProfileScopeClearableText] AS varbinary(max)) OR (i.[ProfileScopeClearableText] IS NULL AND del.[ProfileScopeClearableText] IS NOT NULL) OR (i.[ProfileScopeClearableText] IS NOT NULL AND del.[ProfileScopeClearableText] IS NULL)) OR (CAST(i.[ProfileScopePreservedText] AS varbinary(max)) <> CAST(del.[ProfileScopePreservedText] AS varbinary(max)) OR (i.[ProfileScopePreservedText] IS NULL AND del.[ProfileScopePreservedText] IS NOT NULL) OR (i.[ProfileScopePreservedText] IS NOT NULL AND del.[ProfileScopePreservedText] IS NULL)))
            UNION ALL
            SELECT del.[DocumentId]
            FROM deleted del
            LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
            WHERE i.[DocumentId] IS NULL
        )
        UPDATE d
        SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
        OUTPUT inserted.[DocumentId], inserted.[ContentVersion], inserted.[ContentLastModifiedAt] INTO @stamped
        FROM [dms].[Document] d
        INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[ProfileRootOnlyMergeItem] r WITH (FORCESEEK)
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[ProfileRootOnlyMergeItem] (
            [OldProfileRootOnlyMergeItemId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[ProfileRootOnlyMergeItemId],
            doc.[DocumentUuid],
            doc.[ContentVersion]
        FROM deleted del
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = del.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND EXISTS (SELECT 1 FROM inserted)
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[ProfileRootOnlyMergeItemId] <> d.[ProfileRootOnlyMergeItemId] OR (i.[ProfileRootOnlyMergeItemId] IS NULL AND d.[ProfileRootOnlyMergeItemId] IS NOT NULL) OR (i.[ProfileRootOnlyMergeItemId] IS NOT NULL AND d.[ProfileRootOnlyMergeItemId] IS NULL));
        INSERT INTO [tracked_changes_edfi].[ProfileRootOnlyMergeItem] (
            [OldProfileRootOnlyMergeItemId],
            [NewProfileRootOnlyMergeItemId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[ProfileRootOnlyMergeItemId],
            i.[ProfileRootOnlyMergeItemId],
            doc.[DocumentUuid],
            doc.[ContentVersion]
        FROM @changedDocs cd
        INNER JOIN inserted i ON i.[DocumentId] = cd.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_Student_ReferentialIdentity]
ON [edfi].[Student]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 3;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStudent' AS nvarchar(max)) + N'$.studentUniqueId=' + i.[StudentUniqueId]), i.[DocumentId], 3
        FROM inserted i;
    END
    ELSE IF (UPDATE([StudentUniqueId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(d.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND d.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND d.[StudentUniqueId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 3;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStudent' AS nvarchar(max)) + N'$.studentUniqueId=' + i.[StudentUniqueId]), i.[DocumentId], 3
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_Student_Stamp]
ON [edfi].[Student]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @stamped TABLE (
        [DocumentId] bigint NOT NULL PRIMARY KEY,
        [ContentVersion] bigint NOT NULL,
        [ContentLastModifiedAt] datetime2(7) NOT NULL
    );
    INSERT INTO @stamped ([DocumentId], [ContentVersion], [ContentLastModifiedAt])
    SELECT d.[DocumentId], d.[ContentVersion], d.[ContentLastModifiedAt]
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
    LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
    WHERE del.[DocumentId] IS NULL;
    IF EXISTS (SELECT 1 FROM deleted) AND (NOT EXISTS (SELECT 1 FROM inserted) OR UPDATE([DocumentId]) OR UPDATE([FirstName]) OR UPDATE([StudentUniqueId]))
    BEGIN
        ;WITH affectedDocs AS (
            SELECT i.[DocumentId]
            FROM inserted i
            LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
            WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (CAST(i.[FirstName] AS varbinary(max)) <> CAST(del.[FirstName] AS varbinary(max)) OR (i.[FirstName] IS NULL AND del.[FirstName] IS NOT NULL) OR (i.[FirstName] IS NOT NULL AND del.[FirstName] IS NULL)) OR (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL)))
            UNION ALL
            SELECT del.[DocumentId]
            FROM deleted del
            LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
            WHERE i.[DocumentId] IS NULL
        )
        UPDATE d
        SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
        OUTPUT inserted.[DocumentId], inserted.[ContentVersion], inserted.[ContentLastModifiedAt] INTO @stamped
        FROM [dms].[Document] d
        INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[Student] r WITH (FORCESEEK)
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[Student] (
            [OldStudentUniqueId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[StudentUniqueId],
            doc.[DocumentUuid],
            doc.[ContentVersion]
        FROM deleted del
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = del.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND EXISTS (SELECT 1 FROM inserted)
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(d.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND d.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND d.[StudentUniqueId] IS NULL));
        INSERT INTO [tracked_changes_edfi].[Student] (
            [OldStudentUniqueId],
            [NewStudentUniqueId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[StudentUniqueId],
            i.[StudentUniqueId],
            doc.[DocumentUuid],
            doc.[ContentVersion]
        FROM @changedDocs cd
        INNER JOIN inserted i ON i.[DocumentId] = cd.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

-- ==========================================================
-- Phase 10: Seed Data (insert-if-missing + validation)
-- ==========================================================

-- DataStoreIdentity singleton insert-if-missing
IF NOT EXISTS (SELECT 1 FROM [dms].[DataStoreIdentity] WHERE [DataStoreIdentitySingletonId] = 1)
    INSERT INTO [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId], [SourceIdentity])
    VALUES (1, NEWID());

-- DocumentCacheState singleton insert-if-missing
IF NOT EXISTS (SELECT 1 FROM [dms].[DocumentCacheState] WHERE [StateId] = 1)
    INSERT INTO [dms].[DocumentCacheState] ([StateId], [ProjectionLifecycleState], [CacheAheadRecoveryRequired])
    VALUES (1, N'Disabled', 0);

-- ResourceKey seed inserts (insert-if-missing)
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 1)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (1, N'Ed-Fi', N'ProfileRootOnlyMergeItem', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 2)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (2, N'Ed-Fi', N'SchoolTypeDescriptor', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 3)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (3, N'Ed-Fi', N'Student', N'5.0.0');

-- ResourceKey full-table validation (count + content)
DECLARE @actual_count integer;
DECLARE @mismatched_count integer;
DECLARE @rk_mismatched_ids nvarchar(max);

SELECT @actual_count = COUNT(*) FROM [dms].[ResourceKey];
IF @actual_count <> 3
BEGIN
    DECLARE @rk_count_msg nvarchar(200) = CONCAT(N'dms.ResourceKey count mismatch: expected 3, found ', CAST(@actual_count AS nvarchar(10)));
    THROW 50000, @rk_count_msg, 1;
END

SELECT @mismatched_count = COUNT(*)
FROM [dms].[ResourceKey] rk
WHERE NOT EXISTS (
    SELECT 1 FROM (VALUES
        (1, N'Ed-Fi', N'ProfileRootOnlyMergeItem', N'5.0.0'),
        (2, N'Ed-Fi', N'SchoolTypeDescriptor', N'5.0.0'),
        (3, N'Ed-Fi', N'Student', N'5.0.0')
    ) AS expected([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    WHERE expected.[ResourceKeyId] = rk.[ResourceKeyId]
    AND expected.[ProjectName] = rk.[ProjectName]
    AND expected.[ResourceName] = rk.[ResourceName]
    AND expected.[ResourceVersion] = rk.[ResourceVersion]
);
IF @mismatched_count > 0
BEGIN
    SELECT @rk_mismatched_ids = STRING_AGG(sub.[ResourceKeyId], N', ') WITHIN GROUP (ORDER BY sub.[ResourceKeyIdNum])
    FROM (
        SELECT TOP 10 CAST(rk.[ResourceKeyId] AS nvarchar(10)) AS [ResourceKeyId], rk.[ResourceKeyId] AS [ResourceKeyIdNum]
        FROM [dms].[ResourceKey] rk
        WHERE NOT EXISTS (
            SELECT 1 FROM (VALUES
                (1, N'Ed-Fi', N'ProfileRootOnlyMergeItem', N'5.0.0'),
                (2, N'Ed-Fi', N'SchoolTypeDescriptor', N'5.0.0'),
                (3, N'Ed-Fi', N'Student', N'5.0.0')
            ) AS expected([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
            WHERE expected.[ResourceKeyId] = rk.[ResourceKeyId]
            AND expected.[ProjectName] = rk.[ProjectName]
            AND expected.[ResourceName] = rk.[ResourceName]
            AND expected.[ResourceVersion] = rk.[ResourceVersion]
        )
        ORDER BY rk.[ResourceKeyId]
    ) sub;
    DECLARE @rk_content_msg nvarchar(500) = CONCAT(N'dms.ResourceKey contents mismatch: ', CAST(@mismatched_count AS nvarchar(10)), N' unexpected or modified rows (ResourceKeyIds: ', @rk_mismatched_ids, N'). Run ddl provision for detailed row-level diff.');
    THROW 50000, @rk_content_msg, 1;
END

-- EffectiveSchema singleton insert-if-missing
IF NOT EXISTS (SELECT 1 FROM [dms].[EffectiveSchema] WHERE [EffectiveSchemaSingletonId] = 1)
    INSERT INTO [dms].[EffectiveSchema] ([EffectiveSchemaSingletonId], [ApiSchemaFormatVersion], [EffectiveSchemaHash], [ResourceKeyCount], [ResourceKeySeedHash])
    VALUES (1, N'1.0.0', N'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0', 3, 0xA72B6D85B70E1147001E9503787A2F7173C575DDBED1DFD867AAFECC5ACD13D9);

-- EffectiveSchema validation (ApiSchemaFormatVersion + ResourceKeyCount + ResourceKeySeedHash)
DECLARE @es_stored_api_schema_format_version nvarchar(255);
DECLARE @es_stored_count smallint;
DECLARE @es_stored_hash varbinary(32);

SELECT @es_stored_api_schema_format_version = [ApiSchemaFormatVersion], @es_stored_count = [ResourceKeyCount], @es_stored_hash = [ResourceKeySeedHash]
FROM [dms].[EffectiveSchema]
WHERE [EffectiveSchemaSingletonId] = 1;
IF @es_stored_count IS NOT NULL
BEGIN
    IF @es_stored_api_schema_format_version IS NULL OR LEN(LTRIM(RTRIM(@es_stored_api_schema_format_version))) = 0
    BEGIN
        THROW 50000, N'dms.EffectiveSchema.ApiSchemaFormatVersion must not be empty.', 1;
    END
    IF @es_stored_count <> 3
    BEGIN
        DECLARE @es_count_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeyCount mismatch: expected 3, found ', CAST(@es_stored_count AS nvarchar(10)));
        THROW 50000, @es_count_msg, 1;
    END
    IF @es_stored_hash <> 0xA72B6D85B70E1147001E9503787A2F7173C575DDBED1DFD867AAFECC5ACD13D9
    BEGIN
        DECLARE @es_hash_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored ', CONVERT(nvarchar(66), @es_stored_hash, 1), N' but expected ', CONVERT(nvarchar(66), 0xA72B6D85B70E1147001E9503787A2F7173C575DDBED1DFD867AAFECC5ACD13D9, 1));
        THROW 50000, @es_hash_msg, 1;
    END
END

-- SchemaComponent seed inserts (insert-if-missing)
IF NOT EXISTS (SELECT 1 FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = N'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0' AND [ProjectEndpointName] = N'ed-fi')
    INSERT INTO [dms].[SchemaComponent] ([EffectiveSchemaHash], [ProjectEndpointName], [ProjectName], [ProjectVersion], [IsExtensionProject])
    VALUES (N'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0', N'ed-fi', N'Ed-Fi', N'5.0.0', 0);

-- SchemaComponent exact-match validation (count + content)
DECLARE @sc_actual_count integer;
DECLARE @sc_mismatched_count integer;
DECLARE @sc_mismatched_names nvarchar(max);

SELECT @sc_actual_count = COUNT(*) FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = N'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0';
IF @sc_actual_count <> 1
BEGIN
    DECLARE @sc_count_msg nvarchar(200) = CONCAT(N'dms.SchemaComponent count mismatch: expected 1, found ', CAST(@sc_actual_count AS nvarchar(10)));
    THROW 50000, @sc_count_msg, 1;
END

SELECT @sc_mismatched_count = COUNT(*)
FROM [dms].[SchemaComponent] sc
WHERE sc.[EffectiveSchemaHash] = N'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0'
AND NOT EXISTS (
    SELECT 1 FROM (VALUES
        (N'ed-fi', N'Ed-Fi', N'5.0.0', 0)
    ) AS expected([ProjectEndpointName], [ProjectName], [ProjectVersion], [IsExtensionProject])
    WHERE expected.[ProjectEndpointName] = sc.[ProjectEndpointName]
    AND expected.[ProjectName] = sc.[ProjectName]
    AND expected.[ProjectVersion] = sc.[ProjectVersion]
    AND expected.[IsExtensionProject] = sc.[IsExtensionProject]
);
IF @sc_mismatched_count > 0
BEGIN
    SELECT @sc_mismatched_names = STRING_AGG(sub.[ProjectEndpointName], N', ') WITHIN GROUP (ORDER BY sub.[ProjectEndpointName])
    FROM (
        SELECT TOP 10 sc.[ProjectEndpointName]
        FROM [dms].[SchemaComponent] sc
        WHERE sc.[EffectiveSchemaHash] = N'c0c29ac6d172df544bdf4eeff052ed2767eb15c7961c6cf0e8d7e61f2d19bff0'
        AND NOT EXISTS (
            SELECT 1 FROM (VALUES
                (N'ed-fi', N'Ed-Fi', N'5.0.0', 0)
            ) AS expected([ProjectEndpointName], [ProjectName], [ProjectVersion], [IsExtensionProject])
            WHERE expected.[ProjectEndpointName] = sc.[ProjectEndpointName]
            AND expected.[ProjectName] = sc.[ProjectName]
            AND expected.[ProjectVersion] = sc.[ProjectVersion]
            AND expected.[IsExtensionProject] = sc.[IsExtensionProject]
        )
        ORDER BY sc.[ProjectEndpointName]
    ) sub;
    DECLARE @sc_content_msg nvarchar(500) = CONCAT(N'dms.SchemaComponent contents mismatch: ', CAST(@sc_mismatched_count AS nvarchar(10)), N' unexpected or modified rows (ProjectEndpointNames: ', @sc_mismatched_names, N'). Run ddl provision for detailed row-level diff.');
    THROW 50000, @sc_content_msg, 1;
END

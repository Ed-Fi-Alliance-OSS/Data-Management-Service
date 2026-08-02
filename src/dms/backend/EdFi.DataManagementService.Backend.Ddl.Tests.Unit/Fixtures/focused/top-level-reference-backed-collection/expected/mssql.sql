-- ==========================================================
-- Phase 0: Bounded Provisioning Guards
-- ==========================================================

-- Preflight: validate EffectiveSchema hash compatibility
DECLARE @preflight_stored_hash nvarchar(200);

IF OBJECT_ID(N'dms.EffectiveSchema', N'U') IS NOT NULL
BEGIN
    SELECT @preflight_stored_hash = [EffectiveSchemaHash] FROM [dms].[EffectiveSchema]
    WHERE [EffectiveSchemaSingletonId] = 1;
    IF @preflight_stored_hash IS NOT NULL AND @preflight_stored_hash <> N'ca6b657de7cfe191e2cd5b733292e516ccbd5cb81d05b663c4a39a04a5b8fd61'
    BEGIN
        DECLARE @preflight_msg nvarchar(500) = CONCAT(N'EffectiveSchemaHash mismatch: database has ''', @preflight_stored_hash, N''' but expected ''', N'ca6b657de7cfe191e2cd5b733292e516ccbd5cb81d05b663c4a39a04a5b8fd61', N'''');
        THROW 50000, @preflight_msg, 1;
    END
END

-- Preflight: protect completed DocumentCache mutable singleton state before mutation
DECLARE @preflight_completed_hash nvarchar(200);

IF OBJECT_ID(N'dms.EffectiveSchema', N'U') IS NOT NULL
BEGIN
    SELECT @preflight_completed_hash = [EffectiveSchemaHash] FROM [dms].[EffectiveSchema]
    WHERE [EffectiveSchemaSingletonId] = 1;
    IF @preflight_completed_hash = N'ca6b657de7cfe191e2cd5b733292e516ccbd5cb81d05b663c4a39a04a5b8fd61'
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
    [IdentityVersion] bigint NOT NULL CONSTRAINT [DF_Document_IdentityVersion] DEFAULT (NEXT VALUE FOR [dms].[ChangeVersionSequence]),
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Document_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [IdentityLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Document_IdentityLastModifiedAt] DEFAULT (sysutcdatetime()),
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
    IF EXISTS (SELECT 1 FROM deleted)
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
        FROM [dms].[Descriptor] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId]
        OPTION (RECOMPILE);
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
    FROM [dms].[DocumentProjectionWork] work
    INNER JOIN @required req ON req.[DocumentId] = work.[DocumentId]
    WHERE work.[RequiredContentVersion] < req.[RequiredContentVersion];

    INSERT INTO [dms].[DocumentProjectionWork] ([DocumentId], [RequiredContentVersion], [FirstEnqueuedAt], [LastEnqueuedAt])
    SELECT req.[DocumentId], req.[RequiredContentVersion], @enqueuedAt, @enqueuedAt
    FROM @required req
    LEFT JOIN [dms].[DocumentProjectionWork] work ON work.[DocumentId] = req.[DocumentId]
    WHERE work.[DocumentId] IS NULL;
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

IF OBJECT_ID(N'edfi.Program', N'U') IS NULL
CREATE TABLE [edfi].[Program]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Program_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_Program_ContentVersion] DEFAULT 0,
    [Description] nvarchar(1024) NULL,
    [ProgramId] int NOT NULL,
    [ProgramName] nvarchar(60) NOT NULL,
    CONSTRAINT [PK_Program] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_Program_NK] UNIQUE ([ProgramId], [ProgramName]),
    CONSTRAINT [UX_Program_RefKey] UNIQUE ([ProgramId], [ProgramName], [DocumentId])
);

IF OBJECT_ID(N'edfi.School', N'U') IS NULL
CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_School_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_School_ContentVersion] DEFAULT 0,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_School_NK] UNIQUE ([SchoolId])
);

IF OBJECT_ID(N'edfi.SchoolProgram', N'U') IS NULL
CREATE TABLE [edfi].[SchoolProgram]
(
    [CollectionItemId] bigint NOT NULL DEFAULT (NEXT VALUE FOR [dms].[CollectionItemIdSequence]),
    [Ordinal] int NOT NULL,
    [School_DocumentId] bigint NOT NULL,
    [ProgramReference_DocumentId] bigint NULL,
    [ProgramReference_ProgramId] int NULL,
    [ProgramReference_ProgramName] nvarchar(60) NULL,
    [PrimaryIndicator] bit NULL,
    CONSTRAINT [PK_SchoolProgram] PRIMARY KEY ([CollectionItemId]),
    CONSTRAINT [UX_SchoolProgram_Ordinal_School_DocumentId] UNIQUE ([School_DocumentId], [Ordinal]),
    CONSTRAINT [UX_SchoolProgram_ProgramReference_DocumentId_School_DocumentId] UNIQUE ([School_DocumentId], [ProgramReference_DocumentId]),
    CONSTRAINT [CK_SchoolProgram_ProgramReference_AllNone] CHECK (([ProgramReference_DocumentId] IS NULL AND [ProgramReference_ProgramId] IS NULL AND [ProgramReference_ProgramName] IS NULL) OR ([ProgramReference_DocumentId] IS NOT NULL AND [ProgramReference_ProgramId] IS NOT NULL AND [ProgramReference_ProgramName] IS NOT NULL))
);

IF OBJECT_ID(N'tracked_changes_edfi.Program', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[Program]
(
    [OldProgramId] int NOT NULL,
    [NewProgramId] int NULL,
    [OldProgramName] nvarchar(60) NOT NULL,
    [NewProgramName] nvarchar(60) NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_Program_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_Program] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.School', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[School]
(
    [OldSchoolId] int NOT NULL,
    [NewSchoolId] int NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_School_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_School] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_Program_Document' AND parent_object_id = OBJECT_ID(N'edfi.Program')
)
ALTER TABLE [edfi].[Program]
ADD CONSTRAINT [FK_Program_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_School_Document' AND parent_object_id = OBJECT_ID(N'edfi.School')
)
ALTER TABLE [edfi].[School]
ADD CONSTRAINT [FK_School_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_SchoolProgram_ProgramReference_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.SchoolProgram')
)
ALTER TABLE [edfi].[SchoolProgram]
ADD CONSTRAINT [FK_SchoolProgram_ProgramReference_RefKey]
FOREIGN KEY ([ProgramReference_ProgramId], [ProgramReference_ProgramName], [ProgramReference_DocumentId])
REFERENCES [edfi].[Program] ([ProgramId], [ProgramName], [DocumentId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_SchoolProgram_School' AND parent_object_id = OBJECT_ID(N'edfi.SchoolProgram')
)
ALTER TABLE [edfi].[SchoolProgram]
ADD CONSTRAINT [FK_SchoolProgram_School]
FOREIGN KEY ([School_DocumentId])
REFERENCES [edfi].[School] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'Program' AND i.name = N'IX_Program_ContentVersion'
)
CREATE INDEX [IX_Program_ContentVersion] ON [edfi].[Program] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'School' AND i.name = N'IX_School_ContentVersion'
)
CREATE INDEX [IX_School_ContentVersion] ON [edfi].[School] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'SchoolProgram' AND i.name = N'IX_SchoolProgram_ProgramReference_ProgramId_ProgramReference_ProgramName_ProgramReference_DocumentId'
)
CREATE INDEX [IX_SchoolProgram_ProgramReference_ProgramId_ProgramReference_ProgramName_ProgramReference_DocumentId] ON [edfi].[SchoolProgram] ([ProgramReference_ProgramId], [ProgramReference_ProgramName], [ProgramReference_DocumentId]);

GO
CREATE OR ALTER TRIGGER [edfi].[TR_Program_ReferentialIdentity]
ON [edfi].[Program]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiProgram' AS nvarchar(max)) + N'$.programId=' + CAST(i.[ProgramId] AS nvarchar(max)) + N'#' + N'$.programName=' + i.[ProgramName]), i.[DocumentId], 1
        FROM inserted i;
    END
    ELSE IF (UPDATE([ProgramId]) OR UPDATE([ProgramName]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[ProgramId] <> d.[ProgramId] OR (i.[ProgramId] IS NULL AND d.[ProgramId] IS NOT NULL) OR (i.[ProgramId] IS NOT NULL AND d.[ProgramId] IS NULL)) OR (CAST(i.[ProgramName] AS varbinary(max)) <> CAST(d.[ProgramName] AS varbinary(max)) OR (i.[ProgramName] IS NULL AND d.[ProgramName] IS NOT NULL) OR (i.[ProgramName] IS NOT NULL AND d.[ProgramName] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiProgram' AS nvarchar(max)) + N'$.programId=' + CAST(i.[ProgramId] AS nvarchar(max)) + N'#' + N'$.programName=' + i.[ProgramName]), i.[DocumentId], 1
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_Program_Stamp]
ON [edfi].[Program]
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
    IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        ;WITH affectedDocs AS (
            SELECT i.[DocumentId]
            FROM inserted i
            LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
            WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (CAST(i.[Description] AS varbinary(max)) <> CAST(del.[Description] AS varbinary(max)) OR (i.[Description] IS NULL AND del.[Description] IS NOT NULL) OR (i.[Description] IS NOT NULL AND del.[Description] IS NULL)) OR (i.[ProgramId] <> del.[ProgramId] OR (i.[ProgramId] IS NULL AND del.[ProgramId] IS NOT NULL) OR (i.[ProgramId] IS NOT NULL AND del.[ProgramId] IS NULL)) OR (CAST(i.[ProgramName] AS varbinary(max)) <> CAST(del.[ProgramName] AS varbinary(max)) OR (i.[ProgramName] IS NULL AND del.[ProgramName] IS NOT NULL) OR (i.[ProgramName] IS NOT NULL AND del.[ProgramName] IS NULL)))
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
        FROM [edfi].[Program] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId]
        OPTION (RECOMPILE);
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[Program] (
            [OldProgramId],
            [OldProgramName],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[ProgramId],
            del.[ProgramName],
            doc.[DocumentUuid],
            doc.[ContentVersion]
        FROM deleted del
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = del.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND EXISTS (SELECT 1 FROM inserted)
    BEGIN
        DECLARE @identityChangedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY, [ContentVersion] bigint NOT NULL);
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        OUTPUT inserted.[DocumentId], inserted.[ContentVersion] INTO @identityChangedDocs
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[ProgramId] <> del.[ProgramId] OR (i.[ProgramId] IS NULL AND del.[ProgramId] IS NOT NULL) OR (i.[ProgramId] IS NOT NULL AND del.[ProgramId] IS NULL)) OR (CAST(i.[ProgramName] AS varbinary(max)) <> CAST(del.[ProgramName] AS varbinary(max)) OR (i.[ProgramName] IS NULL AND del.[ProgramName] IS NOT NULL) OR (i.[ProgramName] IS NOT NULL AND del.[ProgramName] IS NULL));
        INSERT INTO [tracked_changes_edfi].[Program] (
            [OldProgramId],
            [OldProgramName],
            [NewProgramId],
            [NewProgramName],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[ProgramId],
            del.[ProgramName],
            i.[ProgramId],
            i.[ProgramName],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_School_ReferentialIdentity]
ON [edfi].[School]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 2;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiSchool' AS nvarchar(max)) + N'$.schoolId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 2
        FROM inserted i;
    END
    ELSE IF (UPDATE([SchoolId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[SchoolId] <> d.[SchoolId] OR (i.[SchoolId] IS NULL AND d.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND d.[SchoolId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 2;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiSchool' AS nvarchar(max)) + N'$.schoolId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 2
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_School_Stamp]
ON [edfi].[School]
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
    IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        ;WITH affectedDocs AS (
            SELECT i.[DocumentId]
            FROM inserted i
            LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
            WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL)))
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
        FROM [edfi].[School] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId]
        OPTION (RECOMPILE);
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[School] (
            [OldSchoolId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[SchoolId],
            doc.[DocumentUuid],
            doc.[ContentVersion]
        FROM deleted del
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = del.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND EXISTS (SELECT 1 FROM inserted)
    BEGIN
        DECLARE @identityChangedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY, [ContentVersion] bigint NOT NULL);
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        OUTPUT inserted.[DocumentId], inserted.[ContentVersion] INTO @identityChangedDocs
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL));
        INSERT INTO [tracked_changes_edfi].[School] (
            [OldSchoolId],
            [NewSchoolId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[SchoolId],
            i.[SchoolId],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_SchoolProgram_Stamp]
ON [edfi].[SchoolProgram]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @stamped TABLE (
        [DocumentId] bigint NOT NULL PRIMARY KEY,
        [ContentVersion] bigint NOT NULL,
        [ContentLastModifiedAt] datetime2(7) NOT NULL
    );
    ;WITH affectedDocs AS (
        SELECT i.[School_DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[CollectionItemId] = i.[CollectionItemId]
        WHERE del.[CollectionItemId] IS NULL OR (i.[CollectionItemId] <> del.[CollectionItemId] OR (i.[CollectionItemId] IS NULL AND del.[CollectionItemId] IS NOT NULL) OR (i.[CollectionItemId] IS NOT NULL AND del.[CollectionItemId] IS NULL)) OR (i.[Ordinal] <> del.[Ordinal] OR (i.[Ordinal] IS NULL AND del.[Ordinal] IS NOT NULL) OR (i.[Ordinal] IS NOT NULL AND del.[Ordinal] IS NULL)) OR (i.[School_DocumentId] <> del.[School_DocumentId] OR (i.[School_DocumentId] IS NULL AND del.[School_DocumentId] IS NOT NULL) OR (i.[School_DocumentId] IS NOT NULL AND del.[School_DocumentId] IS NULL)) OR (i.[ProgramReference_DocumentId] <> del.[ProgramReference_DocumentId] OR (i.[ProgramReference_DocumentId] IS NULL AND del.[ProgramReference_DocumentId] IS NOT NULL) OR (i.[ProgramReference_DocumentId] IS NOT NULL AND del.[ProgramReference_DocumentId] IS NULL)) OR (i.[ProgramReference_ProgramId] <> del.[ProgramReference_ProgramId] OR (i.[ProgramReference_ProgramId] IS NULL AND del.[ProgramReference_ProgramId] IS NOT NULL) OR (i.[ProgramReference_ProgramId] IS NOT NULL AND del.[ProgramReference_ProgramId] IS NULL)) OR (CAST(i.[ProgramReference_ProgramName] AS varbinary(max)) <> CAST(del.[ProgramReference_ProgramName] AS varbinary(max)) OR (i.[ProgramReference_ProgramName] IS NULL AND del.[ProgramReference_ProgramName] IS NOT NULL) OR (i.[ProgramReference_ProgramName] IS NOT NULL AND del.[ProgramReference_ProgramName] IS NULL)) OR (i.[PrimaryIndicator] <> del.[PrimaryIndicator] OR (i.[PrimaryIndicator] IS NULL AND del.[PrimaryIndicator] IS NOT NULL) OR (i.[PrimaryIndicator] IS NOT NULL AND del.[PrimaryIndicator] IS NULL))
        UNION
        SELECT del.[School_DocumentId]
        FROM deleted del
        LEFT JOIN inserted i ON i.[CollectionItemId] = del.[CollectionItemId]
        WHERE i.[CollectionItemId] IS NULL OR (i.[CollectionItemId] <> del.[CollectionItemId] OR (i.[CollectionItemId] IS NULL AND del.[CollectionItemId] IS NOT NULL) OR (i.[CollectionItemId] IS NOT NULL AND del.[CollectionItemId] IS NULL)) OR (i.[Ordinal] <> del.[Ordinal] OR (i.[Ordinal] IS NULL AND del.[Ordinal] IS NOT NULL) OR (i.[Ordinal] IS NOT NULL AND del.[Ordinal] IS NULL)) OR (i.[School_DocumentId] <> del.[School_DocumentId] OR (i.[School_DocumentId] IS NULL AND del.[School_DocumentId] IS NOT NULL) OR (i.[School_DocumentId] IS NOT NULL AND del.[School_DocumentId] IS NULL)) OR (i.[ProgramReference_DocumentId] <> del.[ProgramReference_DocumentId] OR (i.[ProgramReference_DocumentId] IS NULL AND del.[ProgramReference_DocumentId] IS NOT NULL) OR (i.[ProgramReference_DocumentId] IS NOT NULL AND del.[ProgramReference_DocumentId] IS NULL)) OR (i.[ProgramReference_ProgramId] <> del.[ProgramReference_ProgramId] OR (i.[ProgramReference_ProgramId] IS NULL AND del.[ProgramReference_ProgramId] IS NOT NULL) OR (i.[ProgramReference_ProgramId] IS NOT NULL AND del.[ProgramReference_ProgramId] IS NULL)) OR (CAST(i.[ProgramReference_ProgramName] AS varbinary(max)) <> CAST(del.[ProgramReference_ProgramName] AS varbinary(max)) OR (i.[ProgramReference_ProgramName] IS NULL AND del.[ProgramReference_ProgramName] IS NOT NULL) OR (i.[ProgramReference_ProgramName] IS NOT NULL AND del.[ProgramReference_ProgramName] IS NULL)) OR (i.[PrimaryIndicator] <> del.[PrimaryIndicator] OR (i.[PrimaryIndicator] IS NULL AND del.[PrimaryIndicator] IS NOT NULL) OR (i.[PrimaryIndicator] IS NOT NULL AND del.[PrimaryIndicator] IS NULL))
    )
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    OUTPUT inserted.[DocumentId], inserted.[ContentVersion], inserted.[ContentLastModifiedAt] INTO @stamped
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[School_DocumentId]
    INNER JOIN [edfi].[School] stampTarget ON stampTarget.[DocumentId] = a.[School_DocumentId];
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[School] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId]
        OPTION (RECOMPILE);
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
    VALUES (1, N'Ed-Fi', N'Program', N'1.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 2)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (2, N'Ed-Fi', N'School', N'1.0.0');

-- ResourceKey full-table validation (count + content)
DECLARE @actual_count integer;
DECLARE @mismatched_count integer;
DECLARE @rk_mismatched_ids nvarchar(max);

SELECT @actual_count = COUNT(*) FROM [dms].[ResourceKey];
IF @actual_count <> 2
BEGIN
    DECLARE @rk_count_msg nvarchar(200) = CONCAT(N'dms.ResourceKey count mismatch: expected 2, found ', CAST(@actual_count AS nvarchar(10)));
    THROW 50000, @rk_count_msg, 1;
END

SELECT @mismatched_count = COUNT(*)
FROM [dms].[ResourceKey] rk
WHERE NOT EXISTS (
    SELECT 1 FROM (VALUES
        (1, N'Ed-Fi', N'Program', N'1.0.0'),
        (2, N'Ed-Fi', N'School', N'1.0.0')
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
                (1, N'Ed-Fi', N'Program', N'1.0.0'),
                (2, N'Ed-Fi', N'School', N'1.0.0')
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
    VALUES (1, N'1.0.0', N'ca6b657de7cfe191e2cd5b733292e516ccbd5cb81d05b663c4a39a04a5b8fd61', 2, 0x3F4100845FD234C31689525E02A344E042EC3E11C81EF4405143942137997C00);

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
    IF @es_stored_count <> 2
    BEGIN
        DECLARE @es_count_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeyCount mismatch: expected 2, found ', CAST(@es_stored_count AS nvarchar(10)));
        THROW 50000, @es_count_msg, 1;
    END
    IF @es_stored_hash <> 0x3F4100845FD234C31689525E02A344E042EC3E11C81EF4405143942137997C00
    BEGIN
        DECLARE @es_hash_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored ', CONVERT(nvarchar(66), @es_stored_hash, 1), N' but expected ', CONVERT(nvarchar(66), 0x3F4100845FD234C31689525E02A344E042EC3E11C81EF4405143942137997C00, 1));
        THROW 50000, @es_hash_msg, 1;
    END
END

-- SchemaComponent seed inserts (insert-if-missing)
IF NOT EXISTS (SELECT 1 FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = N'ca6b657de7cfe191e2cd5b733292e516ccbd5cb81d05b663c4a39a04a5b8fd61' AND [ProjectEndpointName] = N'ed-fi')
    INSERT INTO [dms].[SchemaComponent] ([EffectiveSchemaHash], [ProjectEndpointName], [ProjectName], [ProjectVersion], [IsExtensionProject])
    VALUES (N'ca6b657de7cfe191e2cd5b733292e516ccbd5cb81d05b663c4a39a04a5b8fd61', N'ed-fi', N'Ed-Fi', N'1.0.0', 0);

-- SchemaComponent exact-match validation (count + content)
DECLARE @sc_actual_count integer;
DECLARE @sc_mismatched_count integer;
DECLARE @sc_mismatched_names nvarchar(max);

SELECT @sc_actual_count = COUNT(*) FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = N'ca6b657de7cfe191e2cd5b733292e516ccbd5cb81d05b663c4a39a04a5b8fd61';
IF @sc_actual_count <> 1
BEGIN
    DECLARE @sc_count_msg nvarchar(200) = CONCAT(N'dms.SchemaComponent count mismatch: expected 1, found ', CAST(@sc_actual_count AS nvarchar(10)));
    THROW 50000, @sc_count_msg, 1;
END

SELECT @sc_mismatched_count = COUNT(*)
FROM [dms].[SchemaComponent] sc
WHERE sc.[EffectiveSchemaHash] = N'ca6b657de7cfe191e2cd5b733292e516ccbd5cb81d05b663c4a39a04a5b8fd61'
AND NOT EXISTS (
    SELECT 1 FROM (VALUES
        (N'ed-fi', N'Ed-Fi', N'1.0.0', 0)
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
        WHERE sc.[EffectiveSchemaHash] = N'ca6b657de7cfe191e2cd5b733292e516ccbd5cb81d05b663c4a39a04a5b8fd61'
        AND NOT EXISTS (
            SELECT 1 FROM (VALUES
                (N'ed-fi', N'Ed-Fi', N'1.0.0', 0)
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

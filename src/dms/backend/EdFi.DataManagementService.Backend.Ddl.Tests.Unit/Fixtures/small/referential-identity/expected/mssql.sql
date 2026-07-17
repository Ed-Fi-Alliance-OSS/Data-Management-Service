-- ==========================================================
-- Phase 0: Preflight (fail fast on schema hash mismatch)
-- ==========================================================

-- Preflight: fail fast if database is provisioned for a different schema hash
DECLARE @preflight_stored_hash nvarchar(200);

IF OBJECT_ID(N'dms.EffectiveSchema', N'U') IS NOT NULL
BEGIN
    SELECT @preflight_stored_hash = [EffectiveSchemaHash] FROM [dms].[EffectiveSchema]
    WHERE [EffectiveSchemaSingletonId] = 1;
    IF @preflight_stored_hash IS NOT NULL AND @preflight_stored_hash <> N'136957ea965b4c23f513963a407ed08e9203a723da63135a3543a74e48e58136'
    BEGIN
        DECLARE @preflight_msg nvarchar(500) = CONCAT(N'EffectiveSchemaHash mismatch: database has ''', @preflight_stored_hash, N''' but expected ''', N'136957ea965b4c23f513963a407ed08e9203a723da63135a3543a74e48e58136', N'''');
        THROW 50000, @preflight_msg, 1;
    END
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

IF OBJECT_ID(N'dms.Descriptor', N'U') IS NULL
CREATE TABLE [dms].[Descriptor]
(
    [DocumentId] bigint NOT NULL,
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
    [Etag] nvarchar(64) NOT NULL,
    [ContentVersion] bigint NOT NULL,
    [LastModifiedAt] datetime2(7) NOT NULL,
    [DocumentJson] nvarchar(max) NOT NULL,
    [ComputedAt] datetime2(7) NOT NULL CONSTRAINT [DF_DocumentCache_ComputedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_DocumentCache] PRIMARY KEY CLUSTERED ([DocumentId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE name = N'UX_DocumentCache_DocumentUuid' AND type = 'UQ' AND parent_object_id = OBJECT_ID(N'dms.DocumentCache')
)
ALTER TABLE [dms].[DocumentCache]
ADD CONSTRAINT [UX_DocumentCache_DocumentUuid] UNIQUE ([DocumentUuid]);

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_DocumentCache_IsJsonObject' AND parent_object_id = OBJECT_ID(N'dms.DocumentCache')
)
ALTER TABLE [dms].[DocumentCache]
ADD CONSTRAINT [CK_DocumentCache_IsJsonObject] CHECK (ISJSON([DocumentJson]) = 1 AND LEFT(LTRIM([DocumentJson]), 1) = '{');

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
    WHERE s.name = N'dms' AND t.name = N'Descriptor' AND i.name = N'IX_Descriptor_Uri_Discriminator'
)
CREATE INDEX [IX_Descriptor_Uri_Discriminator] ON [dms].[Descriptor] ([Uri], [Discriminator]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms' AND t.name = N'Document' AND i.name = N'IX_Document_ResourceKeyId_DocumentId'
)
CREATE INDEX [IX_Document_ResourceKeyId_DocumentId] ON [dms].[Document] ([ResourceKeyId], [DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms' AND t.name = N'DocumentCache' AND i.name = N'IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt'
)
CREATE INDEX [IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt] ON [dms].[DocumentCache] ([ProjectName], [ResourceName], [LastModifiedAt], [DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms' AND t.name = N'ReferentialIdentity' AND i.name = N'IX_ReferentialIdentity_DocumentId'
)
CREATE INDEX [IX_ReferentialIdentity_DocumentId] ON [dms].[ReferentialIdentity] ([DocumentId]);

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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [dms].[Descriptor] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'auth')
    EXEC('CREATE SCHEMA [auth]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'tracked_changes_edfi')
    EXEC('CREATE SCHEMA [tracked_changes_edfi]');

IF OBJECT_ID(N'edfi.DateTimeKeyResource', N'U') IS NULL
CREATE TABLE [edfi].[DateTimeKeyResource]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_DateTimeKeyResource_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_DateTimeKeyResource_ContentVersion] DEFAULT 0,
    [EventTimestamp] datetime2(7) NOT NULL,
    CONSTRAINT [PK_DateTimeKeyResource] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_DateTimeKeyResource_NK] UNIQUE ([EventTimestamp])
);

IF OBJECT_ID(N'edfi.DecimalKeyResource', N'U') IS NULL
CREATE TABLE [edfi].[DecimalKeyResource]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_DecimalKeyResource_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_DecimalKeyResource_ContentVersion] DEFAULT 0,
    [DecimalKey] decimal(9,2) NOT NULL,
    CONSTRAINT [PK_DecimalKeyResource] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_DecimalKeyResource_NK] UNIQUE ([DecimalKey]),
    CONSTRAINT [UX_DecimalKeyResource_RefKey] UNIQUE ([DecimalKey], [DocumentId])
);

IF OBJECT_ID(N'edfi.DecimalRefResource', N'U') IS NULL
CREATE TABLE [edfi].[DecimalRefResource]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_DecimalRefResource_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_DecimalRefResource_ContentVersion] DEFAULT 0,
    [DecimalKeyReference_DocumentId] bigint NOT NULL,
    [DecimalKeyReference_DecimalKey] decimal(9,2) NOT NULL,
    [RefResourceId] nvarchar(64) NOT NULL,
    CONSTRAINT [PK_DecimalRefResource] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_DecimalRefResource_NK] UNIQUE ([RefResourceId], [DecimalKeyReference_DocumentId]),
    CONSTRAINT [CK_DecimalRefResource_DecimalKeyReference_AllNone] CHECK (([DecimalKeyReference_DocumentId] IS NULL AND [DecimalKeyReference_DecimalKey] IS NULL) OR ([DecimalKeyReference_DocumentId] IS NOT NULL AND [DecimalKeyReference_DecimalKey] IS NOT NULL))
);

IF OBJECT_ID(N'edfi.EdOrgDependentChildResource', N'U') IS NULL
CREATE TABLE [edfi].[EdOrgDependentChildResource]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_EdOrgDependentChildResource_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_EdOrgDependentChildResource_ContentVersion] DEFAULT 0,
    [EdOrgDependentResourceReference_DocumentId] bigint NOT NULL,
    [EdOrgDependentResourceReference_EdOrgDependentResourceId] nvarchar(64) NOT NULL,
    [EdOrgDependentResourceReference_EducationOrganizationId] int NOT NULL,
    [EdOrgDependentChildResourceId] nvarchar(64) NOT NULL,
    CONSTRAINT [PK_EdOrgDependentChildResource] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_EdOrgDependentChildResource_NK] UNIQUE ([EdOrgDependentChildResourceId], [EdOrgDependentResourceReference_DocumentId]),
    CONSTRAINT [CK_EdOrgDependentChildResource_EdOrgDependentResourceReference_AllNone] CHECK (([EdOrgDependentResourceReference_DocumentId] IS NULL AND [EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NULL AND [EdOrgDependentResourceReference_EducationOrganizationId] IS NULL) OR ([EdOrgDependentResourceReference_DocumentId] IS NOT NULL AND [EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NOT NULL AND [EdOrgDependentResourceReference_EducationOrganizationId] IS NOT NULL))
);

IF OBJECT_ID(N'edfi.EdOrgDependentResource', N'U') IS NULL
CREATE TABLE [edfi].[EdOrgDependentResource]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_EdOrgDependentResource_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_EdOrgDependentResource_ContentVersion] DEFAULT 0,
    [EducationOrganization_DocumentId] bigint NOT NULL,
    [EducationOrganization_EducationOrganizationId] int NOT NULL,
    [EdOrgDependentResourceId] nvarchar(64) NOT NULL,
    CONSTRAINT [PK_EdOrgDependentResource] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_EdOrgDependentResource_NK] UNIQUE ([EdOrgDependentResourceId], [EducationOrganization_DocumentId]),
    CONSTRAINT [UX_EdOrgDependentResource_RefKey] UNIQUE ([EdOrgDependentResourceId], [EducationOrganization_EducationOrganizationId], [DocumentId]),
    CONSTRAINT [CK_EdOrgDependentResource_EducationOrganization_AllNone] CHECK (([EducationOrganization_DocumentId] IS NULL AND [EducationOrganization_EducationOrganizationId] IS NULL) OR ([EducationOrganization_DocumentId] IS NOT NULL AND [EducationOrganization_EducationOrganizationId] IS NOT NULL))
);

IF OBJECT_ID(N'edfi.KeyUnifiedResource', N'U') IS NULL
CREATE TABLE [edfi].[KeyUnifiedResource]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_KeyUnifiedResource_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_KeyUnifiedResource_ContentVersion] DEFAULT 0,
    [StudentUniqueId_Unified] nvarchar(32) NOT NULL,
    [ResourceAReference_DocumentId] bigint NOT NULL,
    [ResourceAReference_ResourceAId] nvarchar(64) NOT NULL,
    [ResourceAReference_StudentUniqueId] AS (CASE WHEN [ResourceAReference_DocumentId] IS NULL THEN NULL ELSE [StudentUniqueId_Unified] END) PERSISTED,
    [ResourceBReference_DocumentId] bigint NOT NULL,
    [ResourceBReference_ResourceBId] nvarchar(64) NOT NULL,
    [ResourceBReference_StudentUniqueId] AS (CASE WHEN [ResourceBReference_DocumentId] IS NULL THEN NULL ELSE [StudentUniqueId_Unified] END) PERSISTED,
    [KeyUnifiedResourceId] nvarchar(64) NOT NULL,
    CONSTRAINT [PK_KeyUnifiedResource] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_KeyUnifiedResource_NK] UNIQUE ([KeyUnifiedResourceId], [ResourceAReference_DocumentId], [ResourceBReference_DocumentId]),
    CONSTRAINT [CK_KeyUnifiedResource_ResourceAReference_AllNone] CHECK (([ResourceAReference_DocumentId] IS NULL AND [ResourceAReference_ResourceAId] IS NULL AND [ResourceAReference_StudentUniqueId] IS NULL) OR ([ResourceAReference_DocumentId] IS NOT NULL AND [ResourceAReference_ResourceAId] IS NOT NULL AND [ResourceAReference_StudentUniqueId] IS NOT NULL)),
    CONSTRAINT [CK_KeyUnifiedResource_ResourceBReference_AllNone] CHECK (([ResourceBReference_DocumentId] IS NULL AND [ResourceBReference_ResourceBId] IS NULL AND [ResourceBReference_StudentUniqueId] IS NULL) OR ([ResourceBReference_DocumentId] IS NOT NULL AND [ResourceBReference_ResourceBId] IS NOT NULL AND [ResourceBReference_StudentUniqueId] IS NOT NULL))
);

IF OBJECT_ID(N'edfi.ResourceA', N'U') IS NULL
CREATE TABLE [edfi].[ResourceA]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_ResourceA_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_ResourceA_ContentVersion] DEFAULT 0,
    [StudentReference_DocumentId] bigint NOT NULL,
    [StudentReference_StudentUniqueId] nvarchar(32) NOT NULL,
    [ResourceAId] nvarchar(64) NOT NULL,
    CONSTRAINT [PK_ResourceA] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_ResourceA_NK] UNIQUE ([ResourceAId], [StudentReference_DocumentId]),
    CONSTRAINT [UX_ResourceA_RefKey] UNIQUE ([ResourceAId], [StudentReference_StudentUniqueId], [DocumentId]),
    CONSTRAINT [CK_ResourceA_StudentReference_AllNone] CHECK (([StudentReference_DocumentId] IS NULL AND [StudentReference_StudentUniqueId] IS NULL) OR ([StudentReference_DocumentId] IS NOT NULL AND [StudentReference_StudentUniqueId] IS NOT NULL))
);

IF OBJECT_ID(N'edfi.ResourceB', N'U') IS NULL
CREATE TABLE [edfi].[ResourceB]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_ResourceB_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_ResourceB_ContentVersion] DEFAULT 0,
    [StudentReference_DocumentId] bigint NOT NULL,
    [StudentReference_StudentUniqueId] nvarchar(32) NOT NULL,
    [ResourceBId] nvarchar(64) NOT NULL,
    CONSTRAINT [PK_ResourceB] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_ResourceB_NK] UNIQUE ([ResourceBId], [StudentReference_DocumentId]),
    CONSTRAINT [UX_ResourceB_RefKey] UNIQUE ([ResourceBId], [StudentReference_StudentUniqueId], [DocumentId]),
    CONSTRAINT [CK_ResourceB_StudentReference_AllNone] CHECK (([StudentReference_DocumentId] IS NULL AND [StudentReference_StudentUniqueId] IS NULL) OR ([StudentReference_DocumentId] IS NOT NULL AND [StudentReference_StudentUniqueId] IS NOT NULL))
);

IF OBJECT_ID(N'edfi.School', N'U') IS NULL
CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_School_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_School_ContentVersion] DEFAULT 0,
    [EducationOrganizationId] int NOT NULL,
    [NameOfInstitution] nvarchar(75) NULL,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_School_NK] UNIQUE ([SchoolId]),
    CONSTRAINT [UX_School_RefKey] UNIQUE ([SchoolId], [DocumentId])
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

IF OBJECT_ID(N'edfi.StudentSchoolAssociation', N'U') IS NULL
CREATE TABLE [edfi].[StudentSchoolAssociation]
(
    [DocumentId] bigint NOT NULL,
    [ContentLastModifiedAt] datetime2(7) NOT NULL CONSTRAINT [DF_StudentSchoolAssociation_ContentLastModifiedAt] DEFAULT (sysutcdatetime()),
    [ContentVersion] bigint NOT NULL CONSTRAINT [DF_StudentSchoolAssociation_ContentVersion] DEFAULT 0,
    [SchoolReference_DocumentId] bigint NOT NULL,
    [SchoolReference_SchoolId] int NOT NULL,
    [StudentUniqueId] nvarchar(32) NOT NULL,
    CONSTRAINT [PK_StudentSchoolAssociation] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_StudentSchoolAssociation_NK] UNIQUE ([StudentUniqueId], [SchoolReference_DocumentId]),
    CONSTRAINT [CK_StudentSchoolAssociation_SchoolReference_AllNone] CHECK (([SchoolReference_DocumentId] IS NULL AND [SchoolReference_SchoolId] IS NULL) OR ([SchoolReference_DocumentId] IS NOT NULL AND [SchoolReference_SchoolId] IS NOT NULL))
);

IF OBJECT_ID(N'auth.EducationOrganizationIdToEducationOrganizationId', N'U') IS NULL
CREATE TABLE [auth].[EducationOrganizationIdToEducationOrganizationId]
(
    [SourceEducationOrganizationId] bigint NOT NULL,
    [TargetEducationOrganizationId] bigint NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdToEducationOrganizationId] PRIMARY KEY CLUSTERED ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
);

IF OBJECT_ID(N'tracked_changes_edfi.DateTimeKeyResource', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[DateTimeKeyResource]
(
    [OldEventTimestamp] datetime2(7) NOT NULL,
    [NewEventTimestamp] datetime2(7) NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_DateTimeKeyResource_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_DateTimeKeyResource] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.DecimalKeyResource', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[DecimalKeyResource]
(
    [OldDecimalKey] decimal(9,2) NOT NULL,
    [NewDecimalKey] decimal(9,2) NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_DecimalKeyResource_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_DecimalKeyResource] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.DecimalRefResource', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[DecimalRefResource]
(
    [OldRefResourceId] nvarchar(64) NOT NULL,
    [NewRefResourceId] nvarchar(64) NULL,
    [OldDecimalKeyReference_DecimalKey] decimal(9,2) NOT NULL,
    [NewDecimalKeyReference_DecimalKey] decimal(9,2) NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_DecimalRefResource_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_DecimalRefResource] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.EdOrgDependentChildResource', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[EdOrgDependentChildResource]
(
    [OldEdOrgDependentChildResourceId] nvarchar(64) NOT NULL,
    [NewEdOrgDependentChildResourceId] nvarchar(64) NULL,
    [OldEdOrgDependentResourceReference_EdOrgDependentResourceId] nvarchar(64) NOT NULL,
    [NewEdOrgDependentResourceReference_EdOrgDependentResourceId] nvarchar(64) NULL,
    [OldEdOrgDependentResourceReference_EducationOrganizationId] int NOT NULL,
    [NewEdOrgDependentResourceReference_EducationOrganizationId] int NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_EdOrgDependentChildResource_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_EdOrgDependentChildResource] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.EdOrgDependentResource', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[EdOrgDependentResource]
(
    [OldEdOrgDependentResourceId] nvarchar(64) NOT NULL,
    [NewEdOrgDependentResourceId] nvarchar(64) NULL,
    [OldEducationOrganization_EducationOrganizationId] int NOT NULL,
    [NewEducationOrganization_EducationOrganizationId] int NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_EdOrgDependentResource_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_EdOrgDependentResource] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.KeyUnifiedResource', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[KeyUnifiedResource]
(
    [OldKeyUnifiedResourceId] nvarchar(64) NOT NULL,
    [NewKeyUnifiedResourceId] nvarchar(64) NULL,
    [OldResourceAReference_ResourceAId] nvarchar(64) NOT NULL,
    [NewResourceAReference_ResourceAId] nvarchar(64) NULL,
    [OldStudentUniqueId_Unified] nvarchar(32) NOT NULL,
    [NewStudentUniqueId_Unified] nvarchar(32) NULL,
    [OldResourceBReference_ResourceBId] nvarchar(64) NOT NULL,
    [NewResourceBReference_ResourceBId] nvarchar(64) NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_KeyUnifiedResource_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_KeyUnifiedResource] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.ResourceA', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[ResourceA]
(
    [OldResourceAId] nvarchar(64) NOT NULL,
    [NewResourceAId] nvarchar(64) NULL,
    [OldStudentReference_StudentUniqueId] nvarchar(32) NOT NULL,
    [NewStudentReference_StudentUniqueId] nvarchar(32) NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_ResourceA_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_ResourceA] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'tracked_changes_edfi.ResourceB', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[ResourceB]
(
    [OldResourceBId] nvarchar(64) NOT NULL,
    [NewResourceBId] nvarchar(64) NULL,
    [OldStudentReference_StudentUniqueId] nvarchar(32) NOT NULL,
    [NewStudentReference_StudentUniqueId] nvarchar(32) NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_ResourceB_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_ResourceB] PRIMARY KEY CLUSTERED ([ChangeVersion])
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

IF OBJECT_ID(N'tracked_changes_edfi.StudentSchoolAssociation', N'U') IS NULL
CREATE TABLE [tracked_changes_edfi].[StudentSchoolAssociation]
(
    [OldStudentUniqueId] nvarchar(32) NOT NULL,
    [NewStudentUniqueId] nvarchar(32) NULL,
    [OldSchoolReference_SchoolId] int NOT NULL,
    [NewSchoolReference_SchoolId] int NULL,
    [Id] uniqueidentifier NOT NULL,
    [ChangeVersion] bigint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_tracked_changes_edfi_StudentSchoolAssociation_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_tracked_changes_edfi_StudentSchoolAssociation] PRIMARY KEY CLUSTERED ([ChangeVersion])
);

IF OBJECT_ID(N'edfi.EducationOrganizationIdentity', N'U') IS NULL
CREATE TABLE [edfi].[EducationOrganizationIdentity]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    [Discriminator] nvarchar(256) NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdentity] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_EducationOrganizationIdentity_NK] UNIQUE ([EducationOrganizationId]),
    CONSTRAINT [UX_EducationOrganizationIdentity_RefKey] UNIQUE ([EducationOrganizationId], [DocumentId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_DateTimeKeyResource_Document' AND parent_object_id = OBJECT_ID(N'edfi.DateTimeKeyResource')
)
ALTER TABLE [edfi].[DateTimeKeyResource]
ADD CONSTRAINT [FK_DateTimeKeyResource_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_DecimalKeyResource_Document' AND parent_object_id = OBJECT_ID(N'edfi.DecimalKeyResource')
)
ALTER TABLE [edfi].[DecimalKeyResource]
ADD CONSTRAINT [FK_DecimalKeyResource_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_DecimalRefResource_DecimalKeyReference_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.DecimalRefResource')
)
ALTER TABLE [edfi].[DecimalRefResource]
ADD CONSTRAINT [FK_DecimalRefResource_DecimalKeyReference_RefKey]
FOREIGN KEY ([DecimalKeyReference_DecimalKey], [DecimalKeyReference_DocumentId])
REFERENCES [edfi].[DecimalKeyResource] ([DecimalKey], [DocumentId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_DecimalRefResource_Document' AND parent_object_id = OBJECT_ID(N'edfi.DecimalRefResource')
)
ALTER TABLE [edfi].[DecimalRefResource]
ADD CONSTRAINT [FK_DecimalRefResource_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_EdOrgDependentChildResource_Document' AND parent_object_id = OBJECT_ID(N'edfi.EdOrgDependentChildResource')
)
ALTER TABLE [edfi].[EdOrgDependentChildResource]
ADD CONSTRAINT [FK_EdOrgDependentChildResource_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_EdOrgDependentChildResource_EdOrgDependentResourceReference_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.EdOrgDependentChildResource')
)
ALTER TABLE [edfi].[EdOrgDependentChildResource]
ADD CONSTRAINT [FK_EdOrgDependentChildResource_EdOrgDependentResourceReference_RefKey]
FOREIGN KEY ([EdOrgDependentResourceReference_EdOrgDependentResourceId], [EdOrgDependentResourceReference_EducationOrganizationId], [EdOrgDependentResourceReference_DocumentId])
REFERENCES [edfi].[EdOrgDependentResource] ([EdOrgDependentResourceId], [EducationOrganization_EducationOrganizationId], [DocumentId])
ON DELETE NO ACTION
ON UPDATE CASCADE;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_EdOrgDependentResource_Document' AND parent_object_id = OBJECT_ID(N'edfi.EdOrgDependentResource')
)
ALTER TABLE [edfi].[EdOrgDependentResource]
ADD CONSTRAINT [FK_EdOrgDependentResource_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_EdOrgDependentResource_EducationOrganization_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.EdOrgDependentResource')
)
ALTER TABLE [edfi].[EdOrgDependentResource]
ADD CONSTRAINT [FK_EdOrgDependentResource_EducationOrganization_RefKey]
FOREIGN KEY ([EducationOrganization_EducationOrganizationId], [EducationOrganization_DocumentId])
REFERENCES [edfi].[EducationOrganizationIdentity] ([EducationOrganizationId], [DocumentId])
ON DELETE NO ACTION
ON UPDATE CASCADE;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_KeyUnifiedResource_Document' AND parent_object_id = OBJECT_ID(N'edfi.KeyUnifiedResource')
)
ALTER TABLE [edfi].[KeyUnifiedResource]
ADD CONSTRAINT [FK_KeyUnifiedResource_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_KeyUnifiedResource_ResourceAReference_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.KeyUnifiedResource')
)
ALTER TABLE [edfi].[KeyUnifiedResource]
ADD CONSTRAINT [FK_KeyUnifiedResource_ResourceAReference_RefKey]
FOREIGN KEY ([ResourceAReference_ResourceAId], [StudentUniqueId_Unified], [ResourceAReference_DocumentId])
REFERENCES [edfi].[ResourceA] ([ResourceAId], [StudentReference_StudentUniqueId], [DocumentId])
ON DELETE NO ACTION
ON UPDATE CASCADE;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_KeyUnifiedResource_ResourceBReference_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.KeyUnifiedResource')
)
ALTER TABLE [edfi].[KeyUnifiedResource]
ADD CONSTRAINT [FK_KeyUnifiedResource_ResourceBReference_RefKey]
FOREIGN KEY ([ResourceBReference_ResourceBId], [StudentUniqueId_Unified], [ResourceBReference_DocumentId])
REFERENCES [edfi].[ResourceB] ([ResourceBId], [StudentReference_StudentUniqueId], [DocumentId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_ResourceA_Document' AND parent_object_id = OBJECT_ID(N'edfi.ResourceA')
)
ALTER TABLE [edfi].[ResourceA]
ADD CONSTRAINT [FK_ResourceA_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_ResourceA_StudentReference_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.ResourceA')
)
ALTER TABLE [edfi].[ResourceA]
ADD CONSTRAINT [FK_ResourceA_StudentReference_RefKey]
FOREIGN KEY ([StudentReference_StudentUniqueId], [StudentReference_DocumentId])
REFERENCES [edfi].[Student] ([StudentUniqueId], [DocumentId])
ON DELETE NO ACTION
ON UPDATE CASCADE;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_ResourceB_Document' AND parent_object_id = OBJECT_ID(N'edfi.ResourceB')
)
ALTER TABLE [edfi].[ResourceB]
ADD CONSTRAINT [FK_ResourceB_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_ResourceB_StudentReference_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.ResourceB')
)
ALTER TABLE [edfi].[ResourceB]
ADD CONSTRAINT [FK_ResourceB_StudentReference_RefKey]
FOREIGN KEY ([StudentReference_StudentUniqueId], [StudentReference_DocumentId])
REFERENCES [edfi].[Student] ([StudentUniqueId], [DocumentId])
ON DELETE NO ACTION
ON UPDATE CASCADE;

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
    WHERE name = N'FK_Student_Document' AND parent_object_id = OBJECT_ID(N'edfi.Student')
)
ALTER TABLE [edfi].[Student]
ADD CONSTRAINT [FK_Student_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_StudentSchoolAssociation_Document' AND parent_object_id = OBJECT_ID(N'edfi.StudentSchoolAssociation')
)
ALTER TABLE [edfi].[StudentSchoolAssociation]
ADD CONSTRAINT [FK_StudentSchoolAssociation_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_StudentSchoolAssociation_SchoolReference_RefKey' AND parent_object_id = OBJECT_ID(N'edfi.StudentSchoolAssociation')
)
ALTER TABLE [edfi].[StudentSchoolAssociation]
ADD CONSTRAINT [FK_StudentSchoolAssociation_SchoolReference_RefKey]
FOREIGN KEY ([SchoolReference_SchoolId], [SchoolReference_DocumentId])
REFERENCES [edfi].[School] ([SchoolId], [DocumentId])
ON DELETE NO ACTION
ON UPDATE CASCADE;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_EducationOrganizationIdentity_Document' AND parent_object_id = OBJECT_ID(N'edfi.EducationOrganizationIdentity')
)
ALTER TABLE [edfi].[EducationOrganizationIdentity]
ADD CONSTRAINT [FK_EducationOrganizationIdentity_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'auth' AND t.name = N'EducationOrganizationIdToEducationOrganizationId' AND i.name = N'IX_EducationOrganizationIdToEducationOrganizationId_Target'
)
CREATE INDEX [IX_EducationOrganizationIdToEducationOrganizationId_Target] ON [auth].[EducationOrganizationIdToEducationOrganizationId] ([TargetEducationOrganizationId]) INCLUDE ([SourceEducationOrganizationId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'DateTimeKeyResource' AND i.name = N'IX_DateTimeKeyResource_ContentVersion'
)
CREATE INDEX [IX_DateTimeKeyResource_ContentVersion] ON [edfi].[DateTimeKeyResource] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'DecimalKeyResource' AND i.name = N'IX_DecimalKeyResource_ContentVersion'
)
CREATE INDEX [IX_DecimalKeyResource_ContentVersion] ON [edfi].[DecimalKeyResource] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'DecimalRefResource' AND i.name = N'IX_DecimalRefResource_ContentVersion'
)
CREATE INDEX [IX_DecimalRefResource_ContentVersion] ON [edfi].[DecimalRefResource] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'DecimalRefResource' AND i.name = N'IX_DecimalRefResource_DecimalKeyReference_DecimalKey_DecimalKeyReference_DocumentId'
)
CREATE INDEX [IX_DecimalRefResource_DecimalKeyReference_DecimalKey_DecimalKeyReference_DocumentId] ON [edfi].[DecimalRefResource] ([DecimalKeyReference_DecimalKey], [DecimalKeyReference_DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'EdOrgDependentChildResource' AND i.name = N'IX_EdOrgDependentChildResource_ContentVersion'
)
CREATE INDEX [IX_EdOrgDependentChildResource_ContentVersion] ON [edfi].[EdOrgDependentChildResource] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'EdOrgDependentChildResource' AND i.name = N'IX_EdOrgDependentChildResource_EdOrgDependentResourceReference_EdOrgDependentResourceId_EdOrgDependentResourceReferen_3459d40e7c'
)
CREATE INDEX [IX_EdOrgDependentChildResource_EdOrgDependentResourceReference_EdOrgDependentResourceId_EdOrgDependentResourceReferen_3459d40e7c] ON [edfi].[EdOrgDependentChildResource] ([EdOrgDependentResourceReference_EdOrgDependentResourceId], [EdOrgDependentResourceReference_EducationOrganizationId], [EdOrgDependentResourceReference_DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'EdOrgDependentResource' AND i.name = N'IX_EdOrgDependentResource_ContentVersion'
)
CREATE INDEX [IX_EdOrgDependentResource_ContentVersion] ON [edfi].[EdOrgDependentResource] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'EdOrgDependentResource' AND i.name = N'IX_EdOrgDependentResource_EducationOrganization_EducationOrganizationId_EducationOrganization_DocumentId'
)
CREATE INDEX [IX_EdOrgDependentResource_EducationOrganization_EducationOrganizationId_EducationOrganization_DocumentId] ON [edfi].[EdOrgDependentResource] ([EducationOrganization_EducationOrganizationId], [EducationOrganization_DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'KeyUnifiedResource' AND i.name = N'IX_KeyUnifiedResource_ContentVersion'
)
CREATE INDEX [IX_KeyUnifiedResource_ContentVersion] ON [edfi].[KeyUnifiedResource] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'KeyUnifiedResource' AND i.name = N'IX_KeyUnifiedResource_ResourceAReference_ResourceAId_StudentUniqueId_Unified_ResourceAReference_DocumentId'
)
CREATE INDEX [IX_KeyUnifiedResource_ResourceAReference_ResourceAId_StudentUniqueId_Unified_ResourceAReference_DocumentId] ON [edfi].[KeyUnifiedResource] ([ResourceAReference_ResourceAId], [StudentUniqueId_Unified], [ResourceAReference_DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'KeyUnifiedResource' AND i.name = N'IX_KeyUnifiedResource_ResourceBReference_ResourceBId_StudentUniqueId_Unified_ResourceBReference_DocumentId'
)
CREATE INDEX [IX_KeyUnifiedResource_ResourceBReference_ResourceBId_StudentUniqueId_Unified_ResourceBReference_DocumentId] ON [edfi].[KeyUnifiedResource] ([ResourceBReference_ResourceBId], [StudentUniqueId_Unified], [ResourceBReference_DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'ResourceA' AND i.name = N'IX_ResourceA_ContentVersion'
)
CREATE INDEX [IX_ResourceA_ContentVersion] ON [edfi].[ResourceA] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'ResourceA' AND i.name = N'IX_ResourceA_StudentReference_StudentUniqueId_StudentReference_DocumentId'
)
CREATE INDEX [IX_ResourceA_StudentReference_StudentUniqueId_StudentReference_DocumentId] ON [edfi].[ResourceA] ([StudentReference_StudentUniqueId], [StudentReference_DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'ResourceB' AND i.name = N'IX_ResourceB_ContentVersion'
)
CREATE INDEX [IX_ResourceB_ContentVersion] ON [edfi].[ResourceB] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'ResourceB' AND i.name = N'IX_ResourceB_StudentReference_StudentUniqueId_StudentReference_DocumentId'
)
CREATE INDEX [IX_ResourceB_StudentReference_StudentUniqueId_StudentReference_DocumentId] ON [edfi].[ResourceB] ([StudentReference_StudentUniqueId], [StudentReference_DocumentId]);

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
    WHERE s.name = N'edfi' AND t.name = N'Student' AND i.name = N'IX_Student_ContentVersion'
)
CREATE INDEX [IX_Student_ContentVersion] ON [edfi].[Student] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'StudentSchoolAssociation' AND i.name = N'IX_StudentSchoolAssociation_ContentVersion'
)
CREATE INDEX [IX_StudentSchoolAssociation_ContentVersion] ON [edfi].[StudentSchoolAssociation] ([ContentVersion]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'edfi' AND t.name = N'StudentSchoolAssociation' AND i.name = N'IX_StudentSchoolAssociation_SchoolReference_SchoolId_SchoolReference_DocumentId'
)
CREATE INDEX [IX_StudentSchoolAssociation_SchoolReference_SchoolId_SchoolReference_DocumentId] ON [edfi].[StudentSchoolAssociation] ([SchoolReference_SchoolId], [SchoolReference_DocumentId]);

GO
CREATE OR ALTER VIEW [edfi].[EducationOrganization_View] AS
SELECT [DocumentId] AS [DocumentId], [SchoolId] AS [EducationOrganizationId], CAST(N'Ed-Fi:School' AS nvarchar(256)) AS [Discriminator]
FROM [edfi].[School]
;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_DateTimeKeyResource_ReferentialIdentity]
ON [edfi].[DateTimeKeyResource]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiDateTimeKeyResource' AS nvarchar(max)) + N'$.eventTimestamp=' + CONVERT(nvarchar(19), i.[EventTimestamp], 126) + N'Z'), i.[DocumentId], 1
        FROM inserted i;
    END
    ELSE IF (UPDATE([EventTimestamp]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[EventTimestamp] <> d.[EventTimestamp] OR (i.[EventTimestamp] IS NULL AND d.[EventTimestamp] IS NOT NULL) OR (i.[EventTimestamp] IS NOT NULL AND d.[EventTimestamp] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiDateTimeKeyResource' AS nvarchar(max)) + N'$.eventTimestamp=' + CONVERT(nvarchar(19), i.[EventTimestamp], 126) + N'Z'), i.[DocumentId], 1
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_DateTimeKeyResource_Stamp]
ON [edfi].[DateTimeKeyResource]
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[EventTimestamp] <> del.[EventTimestamp] OR (i.[EventTimestamp] IS NULL AND del.[EventTimestamp] IS NOT NULL) OR (i.[EventTimestamp] IS NOT NULL AND del.[EventTimestamp] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[DateTimeKeyResource] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[DateTimeKeyResource] (
            [OldEventTimestamp],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[EventTimestamp],
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
        WHERE (i.[EventTimestamp] <> del.[EventTimestamp] OR (i.[EventTimestamp] IS NULL AND del.[EventTimestamp] IS NOT NULL) OR (i.[EventTimestamp] IS NOT NULL AND del.[EventTimestamp] IS NULL));
        INSERT INTO [tracked_changes_edfi].[DateTimeKeyResource] (
            [OldEventTimestamp],
            [NewEventTimestamp],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[EventTimestamp],
            i.[EventTimestamp],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_DecimalKeyResource_ReferentialIdentity]
ON [edfi].[DecimalKeyResource]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 2;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiDecimalKeyResource' AS nvarchar(max)) + N'$.decimalKey=' + CASE WHEN CHARINDEX(N'.', CAST(i.[DecimalKey] AS nvarchar(max))) = 0 THEN CAST(i.[DecimalKey] AS nvarchar(max)) ELSE CASE WHEN RIGHT(LEFT(CAST(i.[DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKey] AS nvarchar(max)))) + 1), 1) = N'.' THEN LEFT(CAST(i.[DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKey] AS nvarchar(max))))) ELSE LEFT(CAST(i.[DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKey] AS nvarchar(max)))) + 1) END END), i.[DocumentId], 2
        FROM inserted i;
    END
    ELSE IF (UPDATE([DecimalKey]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[DecimalKey] <> d.[DecimalKey] OR (i.[DecimalKey] IS NULL AND d.[DecimalKey] IS NOT NULL) OR (i.[DecimalKey] IS NOT NULL AND d.[DecimalKey] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 2;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiDecimalKeyResource' AS nvarchar(max)) + N'$.decimalKey=' + CASE WHEN CHARINDEX(N'.', CAST(i.[DecimalKey] AS nvarchar(max))) = 0 THEN CAST(i.[DecimalKey] AS nvarchar(max)) ELSE CASE WHEN RIGHT(LEFT(CAST(i.[DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKey] AS nvarchar(max)))) + 1), 1) = N'.' THEN LEFT(CAST(i.[DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKey] AS nvarchar(max))))) ELSE LEFT(CAST(i.[DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKey] AS nvarchar(max)))) + 1) END END), i.[DocumentId], 2
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_DecimalKeyResource_Stamp]
ON [edfi].[DecimalKeyResource]
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[DecimalKey] <> del.[DecimalKey] OR (i.[DecimalKey] IS NULL AND del.[DecimalKey] IS NOT NULL) OR (i.[DecimalKey] IS NOT NULL AND del.[DecimalKey] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[DecimalKeyResource] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[DecimalKeyResource] (
            [OldDecimalKey],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[DecimalKey],
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
        WHERE (i.[DecimalKey] <> del.[DecimalKey] OR (i.[DecimalKey] IS NULL AND del.[DecimalKey] IS NOT NULL) OR (i.[DecimalKey] IS NOT NULL AND del.[DecimalKey] IS NULL));
        INSERT INTO [tracked_changes_edfi].[DecimalKeyResource] (
            [OldDecimalKey],
            [NewDecimalKey],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[DecimalKey],
            i.[DecimalKey],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_DecimalRefResource_ReferentialIdentity]
ON [edfi].[DecimalRefResource]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 3;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiDecimalRefResource' AS nvarchar(max)) + N'$.refResourceId=' + i.[RefResourceId] + N'#' + N'$.decimalKeyReference.decimalKey=' + CASE WHEN CHARINDEX(N'.', CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))) = 0 THEN CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)) ELSE CASE WHEN RIGHT(LEFT(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)))) + 1), 1) = N'.' THEN LEFT(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))))) ELSE LEFT(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)))) + 1) END END), i.[DocumentId], 3
        FROM inserted i;
    END
    ELSE IF (UPDATE([RefResourceId]) OR UPDATE([DecimalKeyReference_DecimalKey]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[RefResourceId] AS varbinary(max)) <> CAST(d.[RefResourceId] AS varbinary(max)) OR (i.[RefResourceId] IS NULL AND d.[RefResourceId] IS NOT NULL) OR (i.[RefResourceId] IS NOT NULL AND d.[RefResourceId] IS NULL)) OR (i.[DecimalKeyReference_DecimalKey] <> d.[DecimalKeyReference_DecimalKey] OR (i.[DecimalKeyReference_DecimalKey] IS NULL AND d.[DecimalKeyReference_DecimalKey] IS NOT NULL) OR (i.[DecimalKeyReference_DecimalKey] IS NOT NULL AND d.[DecimalKeyReference_DecimalKey] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 3;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiDecimalRefResource' AS nvarchar(max)) + N'$.refResourceId=' + i.[RefResourceId] + N'#' + N'$.decimalKeyReference.decimalKey=' + CASE WHEN CHARINDEX(N'.', CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))) = 0 THEN CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)) ELSE CASE WHEN RIGHT(LEFT(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)))) + 1), 1) = N'.' THEN LEFT(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))))) ELSE LEFT(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)), LEN(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max))) - PATINDEX('%[^0]%', REVERSE(CAST(i.[DecimalKeyReference_DecimalKey] AS nvarchar(max)))) + 1) END END), i.[DocumentId], 3
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_DecimalRefResource_Stamp]
ON [edfi].[DecimalRefResource]
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[DecimalKeyReference_DocumentId] <> del.[DecimalKeyReference_DocumentId] OR (i.[DecimalKeyReference_DocumentId] IS NULL AND del.[DecimalKeyReference_DocumentId] IS NOT NULL) OR (i.[DecimalKeyReference_DocumentId] IS NOT NULL AND del.[DecimalKeyReference_DocumentId] IS NULL)) OR (i.[DecimalKeyReference_DecimalKey] <> del.[DecimalKeyReference_DecimalKey] OR (i.[DecimalKeyReference_DecimalKey] IS NULL AND del.[DecimalKeyReference_DecimalKey] IS NOT NULL) OR (i.[DecimalKeyReference_DecimalKey] IS NOT NULL AND del.[DecimalKeyReference_DecimalKey] IS NULL)) OR (CAST(i.[RefResourceId] AS varbinary(max)) <> CAST(del.[RefResourceId] AS varbinary(max)) OR (i.[RefResourceId] IS NULL AND del.[RefResourceId] IS NOT NULL) OR (i.[RefResourceId] IS NOT NULL AND del.[RefResourceId] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[DecimalRefResource] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[DecimalRefResource] (
            [OldRefResourceId],
            [OldDecimalKeyReference_DecimalKey],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[RefResourceId],
            del.[DecimalKeyReference_DecimalKey],
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
        WHERE (CAST(i.[RefResourceId] AS varbinary(max)) <> CAST(del.[RefResourceId] AS varbinary(max)) OR (i.[RefResourceId] IS NULL AND del.[RefResourceId] IS NOT NULL) OR (i.[RefResourceId] IS NOT NULL AND del.[RefResourceId] IS NULL)) OR (i.[DecimalKeyReference_DecimalKey] <> del.[DecimalKeyReference_DecimalKey] OR (i.[DecimalKeyReference_DecimalKey] IS NULL AND del.[DecimalKeyReference_DecimalKey] IS NOT NULL) OR (i.[DecimalKeyReference_DecimalKey] IS NOT NULL AND del.[DecimalKeyReference_DecimalKey] IS NULL));
        INSERT INTO [tracked_changes_edfi].[DecimalRefResource] (
            [OldRefResourceId],
            [OldDecimalKeyReference_DecimalKey],
            [NewRefResourceId],
            [NewDecimalKeyReference_DecimalKey],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[RefResourceId],
            del.[DecimalKeyReference_DecimalKey],
            i.[RefResourceId],
            i.[DecimalKeyReference_DecimalKey],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_EdOrgDependentChildResource_ReferentialIdentity]
ON [edfi].[EdOrgDependentChildResource]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 4;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEdOrgDependentChildResource' AS nvarchar(max)) + N'$.edOrgDependentChildResourceId=' + i.[EdOrgDependentChildResourceId] + N'#' + N'$.edOrgDependentResourceReference.edOrgDependentResourceId=' + i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] + N'#' + N'$.edOrgDependentResourceReference.educationOrganizationId=' + CAST(i.[EdOrgDependentResourceReference_EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 4
        FROM inserted i;
    END
    ELSE IF (UPDATE([EdOrgDependentChildResourceId]) OR UPDATE([EdOrgDependentResourceReference_EdOrgDependentResourceId]) OR UPDATE([EdOrgDependentResourceReference_EducationOrganizationId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[EdOrgDependentChildResourceId] AS varbinary(max)) <> CAST(d.[EdOrgDependentChildResourceId] AS varbinary(max)) OR (i.[EdOrgDependentChildResourceId] IS NULL AND d.[EdOrgDependentChildResourceId] IS NOT NULL) OR (i.[EdOrgDependentChildResourceId] IS NOT NULL AND d.[EdOrgDependentChildResourceId] IS NULL)) OR (CAST(i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] AS varbinary(max)) <> CAST(d.[EdOrgDependentResourceReference_EdOrgDependentResourceId] AS varbinary(max)) OR (i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NULL AND d.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NOT NULL) OR (i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NOT NULL AND d.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NULL)) OR (i.[EdOrgDependentResourceReference_EducationOrganizationId] <> d.[EdOrgDependentResourceReference_EducationOrganizationId] OR (i.[EdOrgDependentResourceReference_EducationOrganizationId] IS NULL AND d.[EdOrgDependentResourceReference_EducationOrganizationId] IS NOT NULL) OR (i.[EdOrgDependentResourceReference_EducationOrganizationId] IS NOT NULL AND d.[EdOrgDependentResourceReference_EducationOrganizationId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 4;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEdOrgDependentChildResource' AS nvarchar(max)) + N'$.edOrgDependentChildResourceId=' + i.[EdOrgDependentChildResourceId] + N'#' + N'$.edOrgDependentResourceReference.edOrgDependentResourceId=' + i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] + N'#' + N'$.edOrgDependentResourceReference.educationOrganizationId=' + CAST(i.[EdOrgDependentResourceReference_EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 4
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_EdOrgDependentChildResource_Stamp]
ON [edfi].[EdOrgDependentChildResource]
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[EdOrgDependentResourceReference_DocumentId] <> del.[EdOrgDependentResourceReference_DocumentId] OR (i.[EdOrgDependentResourceReference_DocumentId] IS NULL AND del.[EdOrgDependentResourceReference_DocumentId] IS NOT NULL) OR (i.[EdOrgDependentResourceReference_DocumentId] IS NOT NULL AND del.[EdOrgDependentResourceReference_DocumentId] IS NULL)) OR (CAST(i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] AS varbinary(max)) <> CAST(del.[EdOrgDependentResourceReference_EdOrgDependentResourceId] AS varbinary(max)) OR (i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NULL AND del.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NOT NULL) OR (i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NOT NULL AND del.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NULL)) OR (i.[EdOrgDependentResourceReference_EducationOrganizationId] <> del.[EdOrgDependentResourceReference_EducationOrganizationId] OR (i.[EdOrgDependentResourceReference_EducationOrganizationId] IS NULL AND del.[EdOrgDependentResourceReference_EducationOrganizationId] IS NOT NULL) OR (i.[EdOrgDependentResourceReference_EducationOrganizationId] IS NOT NULL AND del.[EdOrgDependentResourceReference_EducationOrganizationId] IS NULL)) OR (CAST(i.[EdOrgDependentChildResourceId] AS varbinary(max)) <> CAST(del.[EdOrgDependentChildResourceId] AS varbinary(max)) OR (i.[EdOrgDependentChildResourceId] IS NULL AND del.[EdOrgDependentChildResourceId] IS NOT NULL) OR (i.[EdOrgDependentChildResourceId] IS NOT NULL AND del.[EdOrgDependentChildResourceId] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[EdOrgDependentChildResource] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[EdOrgDependentChildResource] (
            [OldEdOrgDependentChildResourceId],
            [OldEdOrgDependentResourceReference_EdOrgDependentResourceId],
            [OldEdOrgDependentResourceReference_EducationOrganizationId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[EdOrgDependentChildResourceId],
            del.[EdOrgDependentResourceReference_EdOrgDependentResourceId],
            del.[EdOrgDependentResourceReference_EducationOrganizationId],
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
        WHERE (CAST(i.[EdOrgDependentChildResourceId] AS varbinary(max)) <> CAST(del.[EdOrgDependentChildResourceId] AS varbinary(max)) OR (i.[EdOrgDependentChildResourceId] IS NULL AND del.[EdOrgDependentChildResourceId] IS NOT NULL) OR (i.[EdOrgDependentChildResourceId] IS NOT NULL AND del.[EdOrgDependentChildResourceId] IS NULL)) OR (CAST(i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] AS varbinary(max)) <> CAST(del.[EdOrgDependentResourceReference_EdOrgDependentResourceId] AS varbinary(max)) OR (i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NULL AND del.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NOT NULL) OR (i.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NOT NULL AND del.[EdOrgDependentResourceReference_EdOrgDependentResourceId] IS NULL)) OR (i.[EdOrgDependentResourceReference_EducationOrganizationId] <> del.[EdOrgDependentResourceReference_EducationOrganizationId] OR (i.[EdOrgDependentResourceReference_EducationOrganizationId] IS NULL AND del.[EdOrgDependentResourceReference_EducationOrganizationId] IS NOT NULL) OR (i.[EdOrgDependentResourceReference_EducationOrganizationId] IS NOT NULL AND del.[EdOrgDependentResourceReference_EducationOrganizationId] IS NULL));
        INSERT INTO [tracked_changes_edfi].[EdOrgDependentChildResource] (
            [OldEdOrgDependentChildResourceId],
            [OldEdOrgDependentResourceReference_EdOrgDependentResourceId],
            [OldEdOrgDependentResourceReference_EducationOrganizationId],
            [NewEdOrgDependentChildResourceId],
            [NewEdOrgDependentResourceReference_EdOrgDependentResourceId],
            [NewEdOrgDependentResourceReference_EducationOrganizationId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[EdOrgDependentChildResourceId],
            del.[EdOrgDependentResourceReference_EdOrgDependentResourceId],
            del.[EdOrgDependentResourceReference_EducationOrganizationId],
            i.[EdOrgDependentChildResourceId],
            i.[EdOrgDependentResourceReference_EdOrgDependentResourceId],
            i.[EdOrgDependentResourceReference_EducationOrganizationId],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_EdOrgDependentResource_ReferentialIdentity]
ON [edfi].[EdOrgDependentResource]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 5;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEdOrgDependentResource' AS nvarchar(max)) + N'$.edOrgDependentResourceId=' + i.[EdOrgDependentResourceId] + N'#' + N'$.educationOrganizationReference.educationOrganizationId=' + CAST(i.[EducationOrganization_EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 5
        FROM inserted i;
    END
    ELSE IF (UPDATE([EdOrgDependentResourceId]) OR UPDATE([EducationOrganization_EducationOrganizationId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[EdOrgDependentResourceId] AS varbinary(max)) <> CAST(d.[EdOrgDependentResourceId] AS varbinary(max)) OR (i.[EdOrgDependentResourceId] IS NULL AND d.[EdOrgDependentResourceId] IS NOT NULL) OR (i.[EdOrgDependentResourceId] IS NOT NULL AND d.[EdOrgDependentResourceId] IS NULL)) OR (i.[EducationOrganization_EducationOrganizationId] <> d.[EducationOrganization_EducationOrganizationId] OR (i.[EducationOrganization_EducationOrganizationId] IS NULL AND d.[EducationOrganization_EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganization_EducationOrganizationId] IS NOT NULL AND d.[EducationOrganization_EducationOrganizationId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 5;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEdOrgDependentResource' AS nvarchar(max)) + N'$.edOrgDependentResourceId=' + i.[EdOrgDependentResourceId] + N'#' + N'$.educationOrganizationReference.educationOrganizationId=' + CAST(i.[EducationOrganization_EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 5
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_EdOrgDependentResource_Stamp]
ON [edfi].[EdOrgDependentResource]
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[EducationOrganization_DocumentId] <> del.[EducationOrganization_DocumentId] OR (i.[EducationOrganization_DocumentId] IS NULL AND del.[EducationOrganization_DocumentId] IS NOT NULL) OR (i.[EducationOrganization_DocumentId] IS NOT NULL AND del.[EducationOrganization_DocumentId] IS NULL)) OR (i.[EducationOrganization_EducationOrganizationId] <> del.[EducationOrganization_EducationOrganizationId] OR (i.[EducationOrganization_EducationOrganizationId] IS NULL AND del.[EducationOrganization_EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganization_EducationOrganizationId] IS NOT NULL AND del.[EducationOrganization_EducationOrganizationId] IS NULL)) OR (CAST(i.[EdOrgDependentResourceId] AS varbinary(max)) <> CAST(del.[EdOrgDependentResourceId] AS varbinary(max)) OR (i.[EdOrgDependentResourceId] IS NULL AND del.[EdOrgDependentResourceId] IS NOT NULL) OR (i.[EdOrgDependentResourceId] IS NOT NULL AND del.[EdOrgDependentResourceId] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[EdOrgDependentResource] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[EdOrgDependentResource] (
            [OldEdOrgDependentResourceId],
            [OldEducationOrganization_EducationOrganizationId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[EdOrgDependentResourceId],
            del.[EducationOrganization_EducationOrganizationId],
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
        WHERE (CAST(i.[EdOrgDependentResourceId] AS varbinary(max)) <> CAST(del.[EdOrgDependentResourceId] AS varbinary(max)) OR (i.[EdOrgDependentResourceId] IS NULL AND del.[EdOrgDependentResourceId] IS NOT NULL) OR (i.[EdOrgDependentResourceId] IS NOT NULL AND del.[EdOrgDependentResourceId] IS NULL)) OR (i.[EducationOrganization_EducationOrganizationId] <> del.[EducationOrganization_EducationOrganizationId] OR (i.[EducationOrganization_EducationOrganizationId] IS NULL AND del.[EducationOrganization_EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganization_EducationOrganizationId] IS NOT NULL AND del.[EducationOrganization_EducationOrganizationId] IS NULL));
        INSERT INTO [tracked_changes_edfi].[EdOrgDependentResource] (
            [OldEdOrgDependentResourceId],
            [OldEducationOrganization_EducationOrganizationId],
            [NewEdOrgDependentResourceId],
            [NewEducationOrganization_EducationOrganizationId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[EdOrgDependentResourceId],
            del.[EducationOrganization_EducationOrganizationId],
            i.[EdOrgDependentResourceId],
            i.[EducationOrganization_EducationOrganizationId],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_KeyUnifiedResource_ReferentialIdentity]
ON [edfi].[KeyUnifiedResource]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 7;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiKeyUnifiedResource' AS nvarchar(max)) + N'$.keyUnifiedResourceId=' + i.[KeyUnifiedResourceId] + N'#' + N'$.resourceAReference.resourceAId=' + i.[ResourceAReference_ResourceAId] + N'#' + N'$.resourceAReference.studentUniqueId=' + i.[ResourceAReference_StudentUniqueId] + N'#' + N'$.resourceBReference.resourceBId=' + i.[ResourceBReference_ResourceBId] + N'#' + N'$.resourceBReference.studentUniqueId=' + i.[ResourceBReference_StudentUniqueId]), i.[DocumentId], 7
        FROM inserted i;
    END
    ELSE IF (UPDATE([KeyUnifiedResourceId]) OR UPDATE([ResourceAReference_ResourceAId]) OR UPDATE([StudentUniqueId_Unified]) OR UPDATE([ResourceBReference_ResourceBId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[KeyUnifiedResourceId] AS varbinary(max)) <> CAST(d.[KeyUnifiedResourceId] AS varbinary(max)) OR (i.[KeyUnifiedResourceId] IS NULL AND d.[KeyUnifiedResourceId] IS NOT NULL) OR (i.[KeyUnifiedResourceId] IS NOT NULL AND d.[KeyUnifiedResourceId] IS NULL)) OR (CAST(i.[ResourceAReference_ResourceAId] AS varbinary(max)) <> CAST(d.[ResourceAReference_ResourceAId] AS varbinary(max)) OR (i.[ResourceAReference_ResourceAId] IS NULL AND d.[ResourceAReference_ResourceAId] IS NOT NULL) OR (i.[ResourceAReference_ResourceAId] IS NOT NULL AND d.[ResourceAReference_ResourceAId] IS NULL)) OR (CAST(i.[StudentUniqueId_Unified] AS varbinary(max)) <> CAST(d.[StudentUniqueId_Unified] AS varbinary(max)) OR (i.[StudentUniqueId_Unified] IS NULL AND d.[StudentUniqueId_Unified] IS NOT NULL) OR (i.[StudentUniqueId_Unified] IS NOT NULL AND d.[StudentUniqueId_Unified] IS NULL)) OR (CAST(i.[ResourceBReference_ResourceBId] AS varbinary(max)) <> CAST(d.[ResourceBReference_ResourceBId] AS varbinary(max)) OR (i.[ResourceBReference_ResourceBId] IS NULL AND d.[ResourceBReference_ResourceBId] IS NOT NULL) OR (i.[ResourceBReference_ResourceBId] IS NOT NULL AND d.[ResourceBReference_ResourceBId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 7;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiKeyUnifiedResource' AS nvarchar(max)) + N'$.keyUnifiedResourceId=' + i.[KeyUnifiedResourceId] + N'#' + N'$.resourceAReference.resourceAId=' + i.[ResourceAReference_ResourceAId] + N'#' + N'$.resourceAReference.studentUniqueId=' + i.[ResourceAReference_StudentUniqueId] + N'#' + N'$.resourceBReference.resourceBId=' + i.[ResourceBReference_ResourceBId] + N'#' + N'$.resourceBReference.studentUniqueId=' + i.[ResourceBReference_StudentUniqueId]), i.[DocumentId], 7
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_KeyUnifiedResource_Stamp]
ON [edfi].[KeyUnifiedResource]
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (CAST(i.[StudentUniqueId_Unified] AS varbinary(max)) <> CAST(del.[StudentUniqueId_Unified] AS varbinary(max)) OR (i.[StudentUniqueId_Unified] IS NULL AND del.[StudentUniqueId_Unified] IS NOT NULL) OR (i.[StudentUniqueId_Unified] IS NOT NULL AND del.[StudentUniqueId_Unified] IS NULL)) OR (i.[ResourceAReference_DocumentId] <> del.[ResourceAReference_DocumentId] OR (i.[ResourceAReference_DocumentId] IS NULL AND del.[ResourceAReference_DocumentId] IS NOT NULL) OR (i.[ResourceAReference_DocumentId] IS NOT NULL AND del.[ResourceAReference_DocumentId] IS NULL)) OR (CAST(i.[ResourceAReference_ResourceAId] AS varbinary(max)) <> CAST(del.[ResourceAReference_ResourceAId] AS varbinary(max)) OR (i.[ResourceAReference_ResourceAId] IS NULL AND del.[ResourceAReference_ResourceAId] IS NOT NULL) OR (i.[ResourceAReference_ResourceAId] IS NOT NULL AND del.[ResourceAReference_ResourceAId] IS NULL)) OR (i.[ResourceBReference_DocumentId] <> del.[ResourceBReference_DocumentId] OR (i.[ResourceBReference_DocumentId] IS NULL AND del.[ResourceBReference_DocumentId] IS NOT NULL) OR (i.[ResourceBReference_DocumentId] IS NOT NULL AND del.[ResourceBReference_DocumentId] IS NULL)) OR (CAST(i.[ResourceBReference_ResourceBId] AS varbinary(max)) <> CAST(del.[ResourceBReference_ResourceBId] AS varbinary(max)) OR (i.[ResourceBReference_ResourceBId] IS NULL AND del.[ResourceBReference_ResourceBId] IS NOT NULL) OR (i.[ResourceBReference_ResourceBId] IS NOT NULL AND del.[ResourceBReference_ResourceBId] IS NULL)) OR (CAST(i.[KeyUnifiedResourceId] AS varbinary(max)) <> CAST(del.[KeyUnifiedResourceId] AS varbinary(max)) OR (i.[KeyUnifiedResourceId] IS NULL AND del.[KeyUnifiedResourceId] IS NOT NULL) OR (i.[KeyUnifiedResourceId] IS NOT NULL AND del.[KeyUnifiedResourceId] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[KeyUnifiedResource] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[KeyUnifiedResource] (
            [OldKeyUnifiedResourceId],
            [OldResourceAReference_ResourceAId],
            [OldStudentUniqueId_Unified],
            [OldResourceBReference_ResourceBId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[KeyUnifiedResourceId],
            del.[ResourceAReference_ResourceAId],
            del.[StudentUniqueId_Unified],
            del.[ResourceBReference_ResourceBId],
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
        WHERE (CAST(i.[KeyUnifiedResourceId] AS varbinary(max)) <> CAST(del.[KeyUnifiedResourceId] AS varbinary(max)) OR (i.[KeyUnifiedResourceId] IS NULL AND del.[KeyUnifiedResourceId] IS NOT NULL) OR (i.[KeyUnifiedResourceId] IS NOT NULL AND del.[KeyUnifiedResourceId] IS NULL)) OR (CAST(i.[ResourceAReference_ResourceAId] AS varbinary(max)) <> CAST(del.[ResourceAReference_ResourceAId] AS varbinary(max)) OR (i.[ResourceAReference_ResourceAId] IS NULL AND del.[ResourceAReference_ResourceAId] IS NOT NULL) OR (i.[ResourceAReference_ResourceAId] IS NOT NULL AND del.[ResourceAReference_ResourceAId] IS NULL)) OR (CAST(i.[StudentUniqueId_Unified] AS varbinary(max)) <> CAST(del.[StudentUniqueId_Unified] AS varbinary(max)) OR (i.[StudentUniqueId_Unified] IS NULL AND del.[StudentUniqueId_Unified] IS NOT NULL) OR (i.[StudentUniqueId_Unified] IS NOT NULL AND del.[StudentUniqueId_Unified] IS NULL)) OR (CAST(i.[ResourceBReference_ResourceBId] AS varbinary(max)) <> CAST(del.[ResourceBReference_ResourceBId] AS varbinary(max)) OR (i.[ResourceBReference_ResourceBId] IS NULL AND del.[ResourceBReference_ResourceBId] IS NOT NULL) OR (i.[ResourceBReference_ResourceBId] IS NOT NULL AND del.[ResourceBReference_ResourceBId] IS NULL));
        INSERT INTO [tracked_changes_edfi].[KeyUnifiedResource] (
            [OldKeyUnifiedResourceId],
            [OldResourceAReference_ResourceAId],
            [OldStudentUniqueId_Unified],
            [OldResourceBReference_ResourceBId],
            [NewKeyUnifiedResourceId],
            [NewResourceAReference_ResourceAId],
            [NewStudentUniqueId_Unified],
            [NewResourceBReference_ResourceBId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[KeyUnifiedResourceId],
            del.[ResourceAReference_ResourceAId],
            del.[StudentUniqueId_Unified],
            del.[ResourceBReference_ResourceBId],
            i.[KeyUnifiedResourceId],
            i.[ResourceAReference_ResourceAId],
            i.[StudentUniqueId_Unified],
            i.[ResourceBReference_ResourceBId],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_ResourceA_ReferentialIdentity]
ON [edfi].[ResourceA]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 8;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiResourceA' AS nvarchar(max)) + N'$.resourceAId=' + i.[ResourceAId] + N'#' + N'$.studentReference.studentUniqueId=' + i.[StudentReference_StudentUniqueId]), i.[DocumentId], 8
        FROM inserted i;
    END
    ELSE IF (UPDATE([ResourceAId]) OR UPDATE([StudentReference_StudentUniqueId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[ResourceAId] AS varbinary(max)) <> CAST(d.[ResourceAId] AS varbinary(max)) OR (i.[ResourceAId] IS NULL AND d.[ResourceAId] IS NOT NULL) OR (i.[ResourceAId] IS NOT NULL AND d.[ResourceAId] IS NULL)) OR (CAST(i.[StudentReference_StudentUniqueId] AS varbinary(max)) <> CAST(d.[StudentReference_StudentUniqueId] AS varbinary(max)) OR (i.[StudentReference_StudentUniqueId] IS NULL AND d.[StudentReference_StudentUniqueId] IS NOT NULL) OR (i.[StudentReference_StudentUniqueId] IS NOT NULL AND d.[StudentReference_StudentUniqueId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 8;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiResourceA' AS nvarchar(max)) + N'$.resourceAId=' + i.[ResourceAId] + N'#' + N'$.studentReference.studentUniqueId=' + i.[StudentReference_StudentUniqueId]), i.[DocumentId], 8
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_ResourceA_Stamp]
ON [edfi].[ResourceA]
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[StudentReference_DocumentId] <> del.[StudentReference_DocumentId] OR (i.[StudentReference_DocumentId] IS NULL AND del.[StudentReference_DocumentId] IS NOT NULL) OR (i.[StudentReference_DocumentId] IS NOT NULL AND del.[StudentReference_DocumentId] IS NULL)) OR (CAST(i.[StudentReference_StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentReference_StudentUniqueId] AS varbinary(max)) OR (i.[StudentReference_StudentUniqueId] IS NULL AND del.[StudentReference_StudentUniqueId] IS NOT NULL) OR (i.[StudentReference_StudentUniqueId] IS NOT NULL AND del.[StudentReference_StudentUniqueId] IS NULL)) OR (CAST(i.[ResourceAId] AS varbinary(max)) <> CAST(del.[ResourceAId] AS varbinary(max)) OR (i.[ResourceAId] IS NULL AND del.[ResourceAId] IS NOT NULL) OR (i.[ResourceAId] IS NOT NULL AND del.[ResourceAId] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[ResourceA] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[ResourceA] (
            [OldResourceAId],
            [OldStudentReference_StudentUniqueId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[ResourceAId],
            del.[StudentReference_StudentUniqueId],
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
        WHERE (CAST(i.[ResourceAId] AS varbinary(max)) <> CAST(del.[ResourceAId] AS varbinary(max)) OR (i.[ResourceAId] IS NULL AND del.[ResourceAId] IS NOT NULL) OR (i.[ResourceAId] IS NOT NULL AND del.[ResourceAId] IS NULL)) OR (CAST(i.[StudentReference_StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentReference_StudentUniqueId] AS varbinary(max)) OR (i.[StudentReference_StudentUniqueId] IS NULL AND del.[StudentReference_StudentUniqueId] IS NOT NULL) OR (i.[StudentReference_StudentUniqueId] IS NOT NULL AND del.[StudentReference_StudentUniqueId] IS NULL));
        INSERT INTO [tracked_changes_edfi].[ResourceA] (
            [OldResourceAId],
            [OldStudentReference_StudentUniqueId],
            [NewResourceAId],
            [NewStudentReference_StudentUniqueId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[ResourceAId],
            del.[StudentReference_StudentUniqueId],
            i.[ResourceAId],
            i.[StudentReference_StudentUniqueId],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_ResourceB_ReferentialIdentity]
ON [edfi].[ResourceB]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 9;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiResourceB' AS nvarchar(max)) + N'$.resourceBId=' + i.[ResourceBId] + N'#' + N'$.studentReference.studentUniqueId=' + i.[StudentReference_StudentUniqueId]), i.[DocumentId], 9
        FROM inserted i;
    END
    ELSE IF (UPDATE([ResourceBId]) OR UPDATE([StudentReference_StudentUniqueId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[ResourceBId] AS varbinary(max)) <> CAST(d.[ResourceBId] AS varbinary(max)) OR (i.[ResourceBId] IS NULL AND d.[ResourceBId] IS NOT NULL) OR (i.[ResourceBId] IS NOT NULL AND d.[ResourceBId] IS NULL)) OR (CAST(i.[StudentReference_StudentUniqueId] AS varbinary(max)) <> CAST(d.[StudentReference_StudentUniqueId] AS varbinary(max)) OR (i.[StudentReference_StudentUniqueId] IS NULL AND d.[StudentReference_StudentUniqueId] IS NOT NULL) OR (i.[StudentReference_StudentUniqueId] IS NOT NULL AND d.[StudentReference_StudentUniqueId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 9;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiResourceB' AS nvarchar(max)) + N'$.resourceBId=' + i.[ResourceBId] + N'#' + N'$.studentReference.studentUniqueId=' + i.[StudentReference_StudentUniqueId]), i.[DocumentId], 9
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_ResourceB_Stamp]
ON [edfi].[ResourceB]
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[StudentReference_DocumentId] <> del.[StudentReference_DocumentId] OR (i.[StudentReference_DocumentId] IS NULL AND del.[StudentReference_DocumentId] IS NOT NULL) OR (i.[StudentReference_DocumentId] IS NOT NULL AND del.[StudentReference_DocumentId] IS NULL)) OR (CAST(i.[StudentReference_StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentReference_StudentUniqueId] AS varbinary(max)) OR (i.[StudentReference_StudentUniqueId] IS NULL AND del.[StudentReference_StudentUniqueId] IS NOT NULL) OR (i.[StudentReference_StudentUniqueId] IS NOT NULL AND del.[StudentReference_StudentUniqueId] IS NULL)) OR (CAST(i.[ResourceBId] AS varbinary(max)) <> CAST(del.[ResourceBId] AS varbinary(max)) OR (i.[ResourceBId] IS NULL AND del.[ResourceBId] IS NOT NULL) OR (i.[ResourceBId] IS NOT NULL AND del.[ResourceBId] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[ResourceB] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[ResourceB] (
            [OldResourceBId],
            [OldStudentReference_StudentUniqueId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[ResourceBId],
            del.[StudentReference_StudentUniqueId],
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
        WHERE (CAST(i.[ResourceBId] AS varbinary(max)) <> CAST(del.[ResourceBId] AS varbinary(max)) OR (i.[ResourceBId] IS NULL AND del.[ResourceBId] IS NOT NULL) OR (i.[ResourceBId] IS NOT NULL AND del.[ResourceBId] IS NULL)) OR (CAST(i.[StudentReference_StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentReference_StudentUniqueId] AS varbinary(max)) OR (i.[StudentReference_StudentUniqueId] IS NULL AND del.[StudentReference_StudentUniqueId] IS NOT NULL) OR (i.[StudentReference_StudentUniqueId] IS NOT NULL AND del.[StudentReference_StudentUniqueId] IS NULL));
        INSERT INTO [tracked_changes_edfi].[ResourceB] (
            [OldResourceBId],
            [OldStudentReference_StudentUniqueId],
            [NewResourceBId],
            [NewStudentReference_StudentUniqueId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[ResourceBId],
            del.[StudentReference_StudentUniqueId],
            i.[ResourceBId],
            i.[StudentReference_StudentUniqueId],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_School_AbstractIdentity]
ON [edfi].[School]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        UPDATE t
        SET t.[EducationOrganizationId] = s.[SchoolId]
        FROM [edfi].[EducationOrganizationIdentity] t
        INNER JOIN inserted s ON t.[DocumentId] = s.[DocumentId];
        INSERT INTO [edfi].[EducationOrganizationIdentity] ([DocumentId], [EducationOrganizationId], [Discriminator])
        SELECT s.[DocumentId], s.[SchoolId], N'Ed-Fi:School'
        FROM inserted s
        LEFT JOIN [edfi].[EducationOrganizationIdentity] existing ON existing.[DocumentId] = s.[DocumentId]
        WHERE existing.[DocumentId] IS NULL;
    END
    ELSE IF (UPDATE([SchoolId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[SchoolId] <> d.[SchoolId] OR (i.[SchoolId] IS NULL AND d.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND d.[SchoolId] IS NULL));
        UPDATE t
        SET t.[EducationOrganizationId] = s.[SchoolId]
        FROM [edfi].[EducationOrganizationIdentity] t
        INNER JOIN (SELECT i.* FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId]) AS s ON t.[DocumentId] = s.[DocumentId];
        INSERT INTO [edfi].[EducationOrganizationIdentity] ([DocumentId], [EducationOrganizationId], [Discriminator])
        SELECT s.[DocumentId], s.[SchoolId], N'Ed-Fi:School'
        FROM (SELECT i.* FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId]) AS s
        LEFT JOIN [edfi].[EducationOrganizationIdentity] existing ON existing.[DocumentId] = s.[DocumentId]
        WHERE existing.[DocumentId] IS NULL;
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_School_AuthHierarchy_Delete]
ON [edfi].[School]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE tuples
    FROM [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
        INNER JOIN deleted old
            ON tuples.[SourceEducationOrganizationId] = old.[SchoolId]
            AND tuples.[TargetEducationOrganizationId] = old.[SchoolId];
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_School_AuthHierarchy_Insert]
ON [edfi].[School]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
    SELECT new.[SchoolId], new.[SchoolId]
    FROM inserted new;
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
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 10;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiSchool' AS nvarchar(max)) + N'$.schoolId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 10
        FROM inserted i;
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 6;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEducationOrganization' AS nvarchar(max)) + N'$.educationOrganizationId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 6
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
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 10;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiSchool' AS nvarchar(max)) + N'$.schoolId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 10
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 6;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEducationOrganization' AS nvarchar(max)) + N'$.educationOrganizationId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 6
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[EducationOrganizationId] <> del.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND del.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND del.[EducationOrganizationId] IS NULL)) OR (CAST(i.[NameOfInstitution] AS varbinary(max)) <> CAST(del.[NameOfInstitution] AS varbinary(max)) OR (i.[NameOfInstitution] IS NULL AND del.[NameOfInstitution] IS NOT NULL) OR (i.[NameOfInstitution] IS NOT NULL AND del.[NameOfInstitution] IS NULL)) OR (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[School] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
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
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([SchoolId]))
    BEGIN
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL));
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
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 11;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStudent' AS nvarchar(max)) + N'$.studentUniqueId=' + i.[StudentUniqueId]), i.[DocumentId], 11
        FROM inserted i;
    END
    ELSE IF (UPDATE([StudentUniqueId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(d.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND d.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND d.[StudentUniqueId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 11;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStudent' AS nvarchar(max)) + N'$.studentUniqueId=' + i.[StudentUniqueId]), i.[DocumentId], 11
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[Student] r
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
        DECLARE @identityChangedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY, [ContentVersion] bigint NOT NULL);
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        OUTPUT inserted.[DocumentId], inserted.[ContentVersion] INTO @identityChangedDocs
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL));
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
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_StudentSchoolAssociation_ReferentialIdentity]
ON [edfi].[StudentSchoolAssociation]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 12;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStudentSchoolAssociation' AS nvarchar(max)) + N'$.studentUniqueId=' + i.[StudentUniqueId] + N'#' + N'$.schoolReference.schoolId=' + CAST(i.[SchoolReference_SchoolId] AS nvarchar(max))), i.[DocumentId], 12
        FROM inserted i;
    END
    ELSE IF (UPDATE([StudentUniqueId]) OR UPDATE([SchoolReference_SchoolId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(d.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND d.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND d.[StudentUniqueId] IS NULL)) OR (i.[SchoolReference_SchoolId] <> d.[SchoolReference_SchoolId] OR (i.[SchoolReference_SchoolId] IS NULL AND d.[SchoolReference_SchoolId] IS NOT NULL) OR (i.[SchoolReference_SchoolId] IS NOT NULL AND d.[SchoolReference_SchoolId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 12;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStudentSchoolAssociation' AS nvarchar(max)) + N'$.studentUniqueId=' + i.[StudentUniqueId] + N'#' + N'$.schoolReference.schoolId=' + CAST(i.[SchoolReference_SchoolId] AS nvarchar(max))), i.[DocumentId], 12
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_StudentSchoolAssociation_Stamp]
ON [edfi].[StudentSchoolAssociation]
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[SchoolReference_DocumentId] <> del.[SchoolReference_DocumentId] OR (i.[SchoolReference_DocumentId] IS NULL AND del.[SchoolReference_DocumentId] IS NOT NULL) OR (i.[SchoolReference_DocumentId] IS NOT NULL AND del.[SchoolReference_DocumentId] IS NULL)) OR (i.[SchoolReference_SchoolId] <> del.[SchoolReference_SchoolId] OR (i.[SchoolReference_SchoolId] IS NULL AND del.[SchoolReference_SchoolId] IS NOT NULL) OR (i.[SchoolReference_SchoolId] IS NOT NULL AND del.[SchoolReference_SchoolId] IS NULL)) OR (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL)))
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
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
        FROM [edfi].[StudentSchoolAssociation] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT INTO [tracked_changes_edfi].[StudentSchoolAssociation] (
            [OldStudentUniqueId],
            [OldSchoolReference_SchoolId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[StudentUniqueId],
            del.[SchoolReference_SchoolId],
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
        WHERE (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL)) OR (i.[SchoolReference_SchoolId] <> del.[SchoolReference_SchoolId] OR (i.[SchoolReference_SchoolId] IS NULL AND del.[SchoolReference_SchoolId] IS NOT NULL) OR (i.[SchoolReference_SchoolId] IS NOT NULL AND del.[SchoolReference_SchoolId] IS NULL));
        INSERT INTO [tracked_changes_edfi].[StudentSchoolAssociation] (
            [OldStudentUniqueId],
            [OldSchoolReference_SchoolId],
            [NewStudentUniqueId],
            [NewSchoolReference_SchoolId],
            [Id],
            [ChangeVersion]
        )
        SELECT
            del.[StudentUniqueId],
            del.[SchoolReference_SchoolId],
            i.[StudentUniqueId],
            i.[SchoolReference_SchoolId],
            doc.[DocumentUuid],
            idc.[ContentVersion]
        FROM @identityChangedDocs idc
        INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId];
    END
END;
GO

-- ==========================================================
-- Phase 7: Seed Data (insert-if-missing + validation)
-- ==========================================================

-- ResourceKey seed inserts (insert-if-missing)
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 1)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (1, N'Ed-Fi', N'DateTimeKeyResource', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 2)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (2, N'Ed-Fi', N'DecimalKeyResource', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 3)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (3, N'Ed-Fi', N'DecimalRefResource', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 4)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (4, N'Ed-Fi', N'EdOrgDependentChildResource', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 5)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (5, N'Ed-Fi', N'EdOrgDependentResource', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 6)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (6, N'Ed-Fi', N'EducationOrganization', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 7)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (7, N'Ed-Fi', N'KeyUnifiedResource', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 8)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (8, N'Ed-Fi', N'ResourceA', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 9)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (9, N'Ed-Fi', N'ResourceB', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 10)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (10, N'Ed-Fi', N'School', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 11)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (11, N'Ed-Fi', N'Student', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 12)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (12, N'Ed-Fi', N'StudentSchoolAssociation', N'5.0.0');

-- ResourceKey full-table validation (count + content)
DECLARE @actual_count integer;
DECLARE @mismatched_count integer;
DECLARE @rk_mismatched_ids nvarchar(max);

SELECT @actual_count = COUNT(*) FROM [dms].[ResourceKey];
IF @actual_count <> 12
BEGIN
    DECLARE @rk_count_msg nvarchar(200) = CONCAT(N'dms.ResourceKey count mismatch: expected 12, found ', CAST(@actual_count AS nvarchar(10)));
    THROW 50000, @rk_count_msg, 1;
END

SELECT @mismatched_count = COUNT(*)
FROM [dms].[ResourceKey] rk
WHERE NOT EXISTS (
    SELECT 1 FROM (VALUES
        (1, N'Ed-Fi', N'DateTimeKeyResource', N'5.0.0'),
        (2, N'Ed-Fi', N'DecimalKeyResource', N'5.0.0'),
        (3, N'Ed-Fi', N'DecimalRefResource', N'5.0.0'),
        (4, N'Ed-Fi', N'EdOrgDependentChildResource', N'5.0.0'),
        (5, N'Ed-Fi', N'EdOrgDependentResource', N'5.0.0'),
        (6, N'Ed-Fi', N'EducationOrganization', N'5.0.0'),
        (7, N'Ed-Fi', N'KeyUnifiedResource', N'5.0.0'),
        (8, N'Ed-Fi', N'ResourceA', N'5.0.0'),
        (9, N'Ed-Fi', N'ResourceB', N'5.0.0'),
        (10, N'Ed-Fi', N'School', N'5.0.0'),
        (11, N'Ed-Fi', N'Student', N'5.0.0'),
        (12, N'Ed-Fi', N'StudentSchoolAssociation', N'5.0.0')
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
                (1, N'Ed-Fi', N'DateTimeKeyResource', N'5.0.0'),
                (2, N'Ed-Fi', N'DecimalKeyResource', N'5.0.0'),
                (3, N'Ed-Fi', N'DecimalRefResource', N'5.0.0'),
                (4, N'Ed-Fi', N'EdOrgDependentChildResource', N'5.0.0'),
                (5, N'Ed-Fi', N'EdOrgDependentResource', N'5.0.0'),
                (6, N'Ed-Fi', N'EducationOrganization', N'5.0.0'),
                (7, N'Ed-Fi', N'KeyUnifiedResource', N'5.0.0'),
                (8, N'Ed-Fi', N'ResourceA', N'5.0.0'),
                (9, N'Ed-Fi', N'ResourceB', N'5.0.0'),
                (10, N'Ed-Fi', N'School', N'5.0.0'),
                (11, N'Ed-Fi', N'Student', N'5.0.0'),
                (12, N'Ed-Fi', N'StudentSchoolAssociation', N'5.0.0')
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
    VALUES (1, N'1.0.0', N'136957ea965b4c23f513963a407ed08e9203a723da63135a3543a74e48e58136', 12, 0xCF22BDA16C6555C3F9F4F106F8BA65C2461E58FC300892B4FBB958648BA026D1);

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
    IF @es_stored_count <> 12
    BEGIN
        DECLARE @es_count_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeyCount mismatch: expected 12, found ', CAST(@es_stored_count AS nvarchar(10)));
        THROW 50000, @es_count_msg, 1;
    END
    IF @es_stored_hash <> 0xCF22BDA16C6555C3F9F4F106F8BA65C2461E58FC300892B4FBB958648BA026D1
    BEGIN
        DECLARE @es_hash_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored ', CONVERT(nvarchar(66), @es_stored_hash, 1), N' but expected ', CONVERT(nvarchar(66), 0xCF22BDA16C6555C3F9F4F106F8BA65C2461E58FC300892B4FBB958648BA026D1, 1));
        THROW 50000, @es_hash_msg, 1;
    END
END

-- SchemaComponent seed inserts (insert-if-missing)
IF NOT EXISTS (SELECT 1 FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = N'136957ea965b4c23f513963a407ed08e9203a723da63135a3543a74e48e58136' AND [ProjectEndpointName] = N'ed-fi')
    INSERT INTO [dms].[SchemaComponent] ([EffectiveSchemaHash], [ProjectEndpointName], [ProjectName], [ProjectVersion], [IsExtensionProject])
    VALUES (N'136957ea965b4c23f513963a407ed08e9203a723da63135a3543a74e48e58136', N'ed-fi', N'Ed-Fi', N'5.0.0', 0);

-- SchemaComponent exact-match validation (count + content)
DECLARE @sc_actual_count integer;
DECLARE @sc_mismatched_count integer;
DECLARE @sc_mismatched_names nvarchar(max);

SELECT @sc_actual_count = COUNT(*) FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = N'136957ea965b4c23f513963a407ed08e9203a723da63135a3543a74e48e58136';
IF @sc_actual_count <> 1
BEGIN
    DECLARE @sc_count_msg nvarchar(200) = CONCAT(N'dms.SchemaComponent count mismatch: expected 1, found ', CAST(@sc_actual_count AS nvarchar(10)));
    THROW 50000, @sc_count_msg, 1;
END

SELECT @sc_mismatched_count = COUNT(*)
FROM [dms].[SchemaComponent] sc
WHERE sc.[EffectiveSchemaHash] = N'136957ea965b4c23f513963a407ed08e9203a723da63135a3543a74e48e58136'
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
        WHERE sc.[EffectiveSchemaHash] = N'136957ea965b4c23f513963a407ed08e9203a723da63135a3543a74e48e58136'
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

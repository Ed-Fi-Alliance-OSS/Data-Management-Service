-- ==========================================================
-- Phase 0: Preflight (fail fast on schema hash mismatch)
-- ==========================================================

-- Preflight: fail fast if database is provisioned for a different schema hash
DECLARE @preflight_stored_hash nvarchar(200);

IF OBJECT_ID(N'dms.EffectiveSchema', N'U') IS NOT NULL
BEGIN
    SELECT @preflight_stored_hash = [EffectiveSchemaHash] FROM [dms].[EffectiveSchema]
    WHERE [EffectiveSchemaSingletonId] = 1;
    IF @preflight_stored_hash IS NOT NULL AND @preflight_stored_hash <> N'6f3429c66f610a32a38eb294b7ce390ef46578a2f4c6908fc76a5029c205189b'
    BEGIN
        DECLARE @preflight_msg nvarchar(500) = CONCAT(N'EffectiveSchemaHash mismatch: database has ''', @preflight_stored_hash, N''' but expected ''', N'6f3429c66f610a32a38eb294b7ce390ef46578a2f4c6908fc76a5029c205189b', N'''');
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

IF OBJECT_ID(N'dms.DocumentChangeEvent', N'U') IS NULL
CREATE TABLE [dms].[DocumentChangeEvent]
(
    [ChangeVersion] bigint NOT NULL,
    [DocumentId] bigint NOT NULL,
    [ResourceKeyId] smallint NOT NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_DocumentChangeEvent_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_DocumentChangeEvent] PRIMARY KEY CLUSTERED ([ChangeVersion], [DocumentId])
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
    WHERE name = N'FK_DocumentChangeEvent_Document' AND parent_object_id = OBJECT_ID(N'dms.DocumentChangeEvent')
)
ALTER TABLE [dms].[DocumentChangeEvent]
ADD CONSTRAINT [FK_DocumentChangeEvent_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_DocumentChangeEvent_ResourceKey' AND parent_object_id = OBJECT_ID(N'dms.DocumentChangeEvent')
)
ALTER TABLE [dms].[DocumentChangeEvent]
ADD CONSTRAINT [FK_DocumentChangeEvent_ResourceKey]
FOREIGN KEY ([ResourceKeyId])
REFERENCES [dms].[ResourceKey] ([ResourceKeyId])
ON DELETE NO ACTION
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
    WHERE s.name = N'dms' AND t.name = N'DocumentChangeEvent' AND i.name = N'IX_DocumentChangeEvent_DocumentId'
)
CREATE INDEX [IX_DocumentChangeEvent_DocumentId] ON [dms].[DocumentChangeEvent] ([DocumentId]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dms' AND t.name = N'DocumentChangeEvent' AND i.name = N'IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion'
)
CREATE INDEX [IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion] ON [dms].[DocumentChangeEvent] ([ResourceKeyId], [ChangeVersion], [DocumentId]);

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
CREATE OR ALTER TRIGGER [dms].[TR_Document_Journal]
ON [dms].[Document]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE([ContentVersion]) OR NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        INSERT INTO [dms].[DocumentChangeEvent] ([ChangeVersion], [DocumentId], [ResourceKeyId], [CreatedAt])
        SELECT i.[ContentVersion], i.[DocumentId], i.[ResourceKeyId], sysutcdatetime()
        FROM inserted i;
    END
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'auth')
    EXEC('CREATE SCHEMA [auth]');

IF OBJECT_ID(N'edfi.School', N'U') IS NULL
CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    [NameOfInstitution] nvarchar(75) NULL,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_School_NK] UNIQUE ([SchoolId]),
    CONSTRAINT [UX_School_RefKey] UNIQUE ([DocumentId], [SchoolId])
);

IF OBJECT_ID(N'edfi.Student', N'U') IS NULL
CREATE TABLE [edfi].[Student]
(
    [DocumentId] bigint NOT NULL,
    [FirstName] nvarchar(75) NOT NULL,
    [StudentUniqueId] nvarchar(32) NOT NULL,
    CONSTRAINT [PK_Student] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_Student_NK] UNIQUE ([StudentUniqueId])
);

IF OBJECT_ID(N'edfi.StudentSchoolAssociation', N'U') IS NULL
CREATE TABLE [edfi].[StudentSchoolAssociation]
(
    [DocumentId] bigint NOT NULL,
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

IF OBJECT_ID(N'edfi.EducationOrganizationIdentity', N'U') IS NULL
CREATE TABLE [edfi].[EducationOrganizationIdentity]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    [Discriminator] nvarchar(256) NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdentity] PRIMARY KEY ([DocumentId]),
    CONSTRAINT [UX_EducationOrganizationIdentity_NK] UNIQUE ([EducationOrganizationId]),
    CONSTRAINT [UX_EducationOrganizationIdentity_RefKey] UNIQUE ([DocumentId], [EducationOrganizationId])
);

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
    WHERE name = N'FK_StudentSchoolAssociation_SchoolReference' AND parent_object_id = OBJECT_ID(N'edfi.StudentSchoolAssociation')
)
ALTER TABLE [edfi].[StudentSchoolAssociation]
ADD CONSTRAINT [FK_StudentSchoolAssociation_SchoolReference]
FOREIGN KEY ([SchoolReference_DocumentId])
REFERENCES [edfi].[School] ([DocumentId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

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
    WHERE s.name = N'edfi' AND t.name = N'StudentSchoolAssociation' AND i.name = N'IX_StudentSchoolAssociation_SchoolReference_DocumentId'
)
CREATE INDEX [IX_StudentSchoolAssociation_SchoolReference_DocumentId] ON [edfi].[StudentSchoolAssociation] ([SchoolReference_DocumentId]);

GO
CREATE OR ALTER VIEW [edfi].[EducationOrganization_View] AS
SELECT [DocumentId] AS [DocumentId], [SchoolId] AS [EducationOrganizationId], CAST(N'Ed-Fi:School' AS nvarchar(256)) AS [Discriminator]
FROM [edfi].[School]
;

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

CREATE OR ALTER TRIGGER [edfi].[TR_School_PropagateIdentity]
ON [edfi].[School]
INSTEAD OF UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF (UPDATE([EducationOrganizationId]) OR UPDATE([NameOfInstitution]) OR UPDATE([SchoolId]))
    BEGIN
        UPDATE r
        SET r.[SchoolReference_SchoolId] = i.[SchoolId]
        FROM [edfi].[StudentSchoolAssociation] r
        INNER JOIN deleted d ON r.[SchoolReference_DocumentId] = d.[DocumentId]
        INNER JOIN inserted i ON i.[DocumentId] = d.[DocumentId]
        WHERE (i.[SchoolId] <> d.[SchoolId] OR (i.[SchoolId] IS NULL AND d.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND d.[SchoolId] IS NULL))
        AND ((r.[SchoolReference_SchoolId] = d.[SchoolId]) OR (r.[SchoolReference_SchoolId] IS NULL AND d.[SchoolId] IS NULL));

    END

    UPDATE t
    SET t.[EducationOrganizationId] = i.[EducationOrganizationId], t.[NameOfInstitution] = i.[NameOfInstitution], t.[SchoolId] = i.[SchoolId]
    FROM [edfi].[School] t
    INNER JOIN inserted i ON t.[DocumentId] = i.[DocumentId];
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
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEducationOrganization' AS nvarchar(max)) + N'$.educationOrganizationId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 1
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
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEducationOrganization' AS nvarchar(max)) + N'$.educationOrganizationId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 1
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[EducationOrganizationId] <> del.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND del.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND del.[EducationOrganizationId] IS NULL)) OR (i.[NameOfInstitution] <> del.[NameOfInstitution] OR (i.[NameOfInstitution] IS NULL AND del.[NameOfInstitution] IS NOT NULL) OR (i.[NameOfInstitution] IS NOT NULL AND del.[NameOfInstitution] IS NULL)) OR (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL))
        UNION
        SELECT del.[DocumentId]
        FROM deleted del
        LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
        WHERE i.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[EducationOrganizationId] <> del.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND del.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND del.[EducationOrganizationId] IS NULL)) OR (i.[NameOfInstitution] <> del.[NameOfInstitution] OR (i.[NameOfInstitution] IS NULL AND del.[NameOfInstitution] IS NOT NULL) OR (i.[NameOfInstitution] IS NOT NULL AND del.[NameOfInstitution] IS NULL)) OR (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL))
    )
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
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
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 3;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStudent' AS nvarchar(max)) + N'$.studentUniqueId=' + i.[StudentUniqueId]), i.[DocumentId], 3
        FROM inserted i;
    END
    ELSE IF (UPDATE([StudentUniqueId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[StudentUniqueId] <> d.[StudentUniqueId] OR (i.[StudentUniqueId] IS NULL AND d.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND d.[StudentUniqueId] IS NULL));
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[FirstName] <> del.[FirstName] OR (i.[FirstName] IS NULL AND del.[FirstName] IS NOT NULL) OR (i.[FirstName] IS NOT NULL AND del.[FirstName] IS NULL)) OR (i.[StudentUniqueId] <> del.[StudentUniqueId] OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL))
        UNION
        SELECT del.[DocumentId]
        FROM deleted del
        LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
        WHERE i.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[FirstName] <> del.[FirstName] OR (i.[FirstName] IS NULL AND del.[FirstName] IS NOT NULL) OR (i.[FirstName] IS NOT NULL AND del.[FirstName] IS NULL)) OR (i.[StudentUniqueId] <> del.[StudentUniqueId] OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL))
    )
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([StudentUniqueId]))
    BEGIN
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[StudentUniqueId] <> del.[StudentUniqueId] OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL));
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
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 4;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStudentSchoolAssociation' AS nvarchar(max)) + N'$.studentUniqueId=' + i.[StudentUniqueId] + N'#' + N'$.schoolReference.schoolId=' + CAST(i.[SchoolReference_SchoolId] AS nvarchar(max))), i.[DocumentId], 4
        FROM inserted i;
    END
    ELSE IF (UPDATE([StudentUniqueId]) OR UPDATE([SchoolReference_SchoolId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[StudentUniqueId] <> d.[StudentUniqueId] OR (i.[StudentUniqueId] IS NULL AND d.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND d.[StudentUniqueId] IS NULL)) OR (i.[SchoolReference_SchoolId] <> d.[SchoolReference_SchoolId] OR (i.[SchoolReference_SchoolId] IS NULL AND d.[SchoolReference_SchoolId] IS NOT NULL) OR (i.[SchoolReference_SchoolId] IS NOT NULL AND d.[SchoolReference_SchoolId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 4;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStudentSchoolAssociation' AS nvarchar(max)) + N'$.studentUniqueId=' + i.[StudentUniqueId] + N'#' + N'$.schoolReference.schoolId=' + CAST(i.[SchoolReference_SchoolId] AS nvarchar(max))), i.[DocumentId], 4
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
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[SchoolReference_DocumentId] <> del.[SchoolReference_DocumentId] OR (i.[SchoolReference_DocumentId] IS NULL AND del.[SchoolReference_DocumentId] IS NOT NULL) OR (i.[SchoolReference_DocumentId] IS NOT NULL AND del.[SchoolReference_DocumentId] IS NULL)) OR (i.[SchoolReference_SchoolId] <> del.[SchoolReference_SchoolId] OR (i.[SchoolReference_SchoolId] IS NULL AND del.[SchoolReference_SchoolId] IS NOT NULL) OR (i.[SchoolReference_SchoolId] IS NOT NULL AND del.[SchoolReference_SchoolId] IS NULL)) OR (i.[StudentUniqueId] <> del.[StudentUniqueId] OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL))
        UNION
        SELECT del.[DocumentId]
        FROM deleted del
        LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
        WHERE i.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[SchoolReference_DocumentId] <> del.[SchoolReference_DocumentId] OR (i.[SchoolReference_DocumentId] IS NULL AND del.[SchoolReference_DocumentId] IS NOT NULL) OR (i.[SchoolReference_DocumentId] IS NOT NULL AND del.[SchoolReference_DocumentId] IS NULL)) OR (i.[SchoolReference_SchoolId] <> del.[SchoolReference_SchoolId] OR (i.[SchoolReference_SchoolId] IS NULL AND del.[SchoolReference_SchoolId] IS NOT NULL) OR (i.[SchoolReference_SchoolId] IS NOT NULL AND del.[SchoolReference_SchoolId] IS NULL)) OR (i.[StudentUniqueId] <> del.[StudentUniqueId] OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL))
    )
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([StudentUniqueId]) OR UPDATE([SchoolReference_SchoolId]))
    BEGIN
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[StudentUniqueId] <> del.[StudentUniqueId] OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL)) OR (i.[SchoolReference_SchoolId] <> del.[SchoolReference_SchoolId] OR (i.[SchoolReference_SchoolId] IS NULL AND del.[SchoolReference_SchoolId] IS NOT NULL) OR (i.[SchoolReference_SchoolId] IS NOT NULL AND del.[SchoolReference_SchoolId] IS NULL));
    END
END;
GO

-- ==========================================================
-- Phase 7: Seed Data (insert-if-missing + validation)
-- ==========================================================

-- ResourceKey seed inserts (insert-if-missing)
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 1)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (1, N'Ed-Fi', N'EducationOrganization', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 2)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (2, N'Ed-Fi', N'School', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 3)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (3, N'Ed-Fi', N'Student', N'5.0.0');
IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 4)
    INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
    VALUES (4, N'Ed-Fi', N'StudentSchoolAssociation', N'5.0.0');

-- ResourceKey full-table validation (count + content)
DECLARE @actual_count integer;
DECLARE @mismatched_count integer;
DECLARE @rk_mismatched_ids nvarchar(max);

SELECT @actual_count = COUNT(*) FROM [dms].[ResourceKey];
IF @actual_count <> 4
BEGIN
    DECLARE @rk_count_msg nvarchar(200) = CONCAT(N'dms.ResourceKey count mismatch: expected 4, found ', CAST(@actual_count AS nvarchar(10)));
    THROW 50000, @rk_count_msg, 1;
END

SELECT @mismatched_count = COUNT(*)
FROM [dms].[ResourceKey] rk
WHERE NOT EXISTS (
    SELECT 1 FROM (VALUES
        (1, N'Ed-Fi', N'EducationOrganization', N'5.0.0'),
        (2, N'Ed-Fi', N'School', N'5.0.0'),
        (3, N'Ed-Fi', N'Student', N'5.0.0'),
        (4, N'Ed-Fi', N'StudentSchoolAssociation', N'5.0.0')
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
                (1, N'Ed-Fi', N'EducationOrganization', N'5.0.0'),
                (2, N'Ed-Fi', N'School', N'5.0.0'),
                (3, N'Ed-Fi', N'Student', N'5.0.0'),
                (4, N'Ed-Fi', N'StudentSchoolAssociation', N'5.0.0')
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
    VALUES (1, N'1.0.0', N'6f3429c66f610a32a38eb294b7ce390ef46578a2f4c6908fc76a5029c205189b', 4, 0x312FB54DF0BEB8F19C81D0F9396791FB5D6CF28B01462E427AF1B13DB4DDBF99);

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
    IF @es_stored_count <> 4
    BEGIN
        DECLARE @es_count_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeyCount mismatch: expected 4, found ', CAST(@es_stored_count AS nvarchar(10)));
        THROW 50000, @es_count_msg, 1;
    END
    IF @es_stored_hash <> 0x312FB54DF0BEB8F19C81D0F9396791FB5D6CF28B01462E427AF1B13DB4DDBF99
    BEGIN
        DECLARE @es_hash_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored ', CONVERT(nvarchar(66), @es_stored_hash, 1), N' but expected ', CONVERT(nvarchar(66), 0x312FB54DF0BEB8F19C81D0F9396791FB5D6CF28B01462E427AF1B13DB4DDBF99, 1));
        THROW 50000, @es_hash_msg, 1;
    END
END

-- SchemaComponent seed inserts (insert-if-missing)
IF NOT EXISTS (SELECT 1 FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = N'6f3429c66f610a32a38eb294b7ce390ef46578a2f4c6908fc76a5029c205189b' AND [ProjectEndpointName] = N'ed-fi')
    INSERT INTO [dms].[SchemaComponent] ([EffectiveSchemaHash], [ProjectEndpointName], [ProjectName], [ProjectVersion], [IsExtensionProject])
    VALUES (N'6f3429c66f610a32a38eb294b7ce390ef46578a2f4c6908fc76a5029c205189b', N'ed-fi', N'Ed-Fi', N'5.0.0', 0);

-- SchemaComponent exact-match validation (count + content)
DECLARE @sc_actual_count integer;
DECLARE @sc_mismatched_count integer;
DECLARE @sc_mismatched_names nvarchar(max);

SELECT @sc_actual_count = COUNT(*) FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = N'6f3429c66f610a32a38eb294b7ce390ef46578a2f4c6908fc76a5029c205189b';
IF @sc_actual_count <> 1
BEGIN
    DECLARE @sc_count_msg nvarchar(200) = CONCAT(N'dms.SchemaComponent count mismatch: expected 1, found ', CAST(@sc_actual_count AS nvarchar(10)));
    THROW 50000, @sc_count_msg, 1;
END

SELECT @sc_mismatched_count = COUNT(*)
FROM [dms].[SchemaComponent] sc
WHERE sc.[EffectiveSchemaHash] = N'6f3429c66f610a32a38eb294b7ce390ef46578a2f4c6908fc76a5029c205189b'
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
        WHERE sc.[EffectiveSchemaHash] = N'6f3429c66f610a32a38eb294b7ce390ef46578a2f4c6908fc76a5029c205189b'
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


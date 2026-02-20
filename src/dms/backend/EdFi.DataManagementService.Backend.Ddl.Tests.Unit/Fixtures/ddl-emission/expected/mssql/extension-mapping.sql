IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'sample')
    EXEC('CREATE SCHEMA [sample]');

IF OBJECT_ID(N'edfi.School', N'U') IS NULL
CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.SchoolAddress', N'U') IS NULL
CREATE TABLE [edfi].[SchoolAddress]
(
    [DocumentId] bigint NOT NULL,
    [AddressOrdinal] int NOT NULL,
    [Street] nvarchar(100) NOT NULL,
    CONSTRAINT [PK_SchoolAddress] PRIMARY KEY ([DocumentId], [AddressOrdinal])
);

IF OBJECT_ID(N'sample.SchoolExtension', N'U') IS NULL
CREATE TABLE [sample].[SchoolExtension]
(
    [DocumentId] bigint NOT NULL,
    [ExtensionData] nvarchar(200) NULL,
    CONSTRAINT [PK_SchoolExtension] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'sample.SchoolAddressExtension', N'U') IS NULL
CREATE TABLE [sample].[SchoolAddressExtension]
(
    [DocumentId] bigint NOT NULL,
    [AddressOrdinal] int NOT NULL,
    [AddressExtensionData] nvarchar(100) NULL,
    CONSTRAINT [PK_SchoolAddressExtension] PRIMARY KEY ([DocumentId], [AddressOrdinal])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_SchoolAddress_School' AND parent_object_id = OBJECT_ID(N'edfi.SchoolAddress')
)
ALTER TABLE [edfi].[SchoolAddress]
ADD CONSTRAINT [FK_SchoolAddress_School]
FOREIGN KEY ([DocumentId])
REFERENCES [edfi].[School] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_SchoolExtension_School' AND parent_object_id = OBJECT_ID(N'sample.SchoolExtension')
)
ALTER TABLE [sample].[SchoolExtension]
ADD CONSTRAINT [FK_SchoolExtension_School]
FOREIGN KEY ([DocumentId])
REFERENCES [edfi].[School] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_SchoolAddressExtension_SchoolAddress' AND parent_object_id = OBJECT_ID(N'sample.SchoolAddressExtension')
)
ALTER TABLE [sample].[SchoolAddressExtension]
ADD CONSTRAINT [FK_SchoolAddressExtension_SchoolAddress]
FOREIGN KEY ([DocumentId], [AddressOrdinal])
REFERENCES [edfi].[SchoolAddress] ([DocumentId], [AddressOrdinal])
ON DELETE CASCADE
ON UPDATE NO ACTION;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_School_Stamp]
ON [edfi].[School]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (SELECT [DocumentId] FROM inserted UNION SELECT [DocumentId] FROM deleted)
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
CREATE OR ALTER TRIGGER [edfi].[TR_SchoolAddress_Stamp]
ON [edfi].[SchoolAddress]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (SELECT [DocumentId] FROM inserted UNION SELECT [DocumentId] FROM deleted)
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
END;

GO
CREATE OR ALTER TRIGGER [sample].[TR_SchoolAddressExtension_Stamp]
ON [sample].[SchoolAddressExtension]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (SELECT [DocumentId] FROM inserted UNION SELECT [DocumentId] FROM deleted)
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
END;

GO
CREATE OR ALTER TRIGGER [sample].[TR_SchoolExtension_Stamp]
ON [sample].[SchoolExtension]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (SELECT [DocumentId] FROM inserted UNION SELECT [DocumentId] FROM deleted)
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_School_ReferentialIdentity]
ON [edfi].[School]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dms].[ReferentialIdentity]
    WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
    INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
    SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiSchool' + N'$$.schoolId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 1
    FROM inserted i;
END;


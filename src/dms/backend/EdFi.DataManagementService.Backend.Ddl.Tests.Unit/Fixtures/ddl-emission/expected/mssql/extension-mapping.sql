CREATE SCHEMA [edfi];
CREATE SCHEMA [sample];

CREATE TABLE [edfi].[School] (
    [DocumentId] bigint NOT NULL,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

CREATE TABLE [edfi].[SchoolAddress] (
    [DocumentId] bigint NOT NULL,
    [AddressOrdinal] int NOT NULL,
    [Street] nvarchar(100) NOT NULL,
    CONSTRAINT [PK_SchoolAddress] PRIMARY KEY ([DocumentId], [AddressOrdinal])
);

CREATE TABLE [sample].[SchoolExtension] (
    [DocumentId] bigint NOT NULL,
    [ExtensionData] nvarchar(200) NULL,
    CONSTRAINT [PK_SchoolExtension] PRIMARY KEY ([DocumentId])
);

CREATE TABLE [sample].[SchoolAddressExtension] (
    [DocumentId] bigint NOT NULL,
    [AddressOrdinal] int NOT NULL,
    [AddressExtensionData] nvarchar(100) NULL,
    CONSTRAINT [PK_SchoolAddressExtension] PRIMARY KEY ([DocumentId], [AddressOrdinal])
);

ALTER TABLE [edfi].[SchoolAddress] ADD CONSTRAINT [FK_SchoolAddress_School] FOREIGN KEY ([DocumentId]) REFERENCES [edfi].[School] ([DocumentId]) ON DELETE CASCADE;

ALTER TABLE [sample].[SchoolExtension] ADD CONSTRAINT [FK_SchoolExtension_School] FOREIGN KEY ([DocumentId]) REFERENCES [edfi].[School] ([DocumentId]) ON DELETE CASCADE;

ALTER TABLE [sample].[SchoolAddressExtension] ADD CONSTRAINT [FK_SchoolAddressExtension_SchoolAddress] FOREIGN KEY ([DocumentId], [AddressOrdinal]) REFERENCES [edfi].[SchoolAddress] ([DocumentId], [AddressOrdinal]) ON DELETE CASCADE;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_School_Stamp]
ON [edfi].[School]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId];
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
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId];
END;

GO
CREATE OR ALTER TRIGGER [sample].[TR_SchoolAddressExtension_Stamp]
ON [sample].[SchoolAddressExtension]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId];
END;

GO
CREATE OR ALTER TRIGGER [sample].[TR_SchoolExtension_Stamp]
ON [sample].[SchoolExtension]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId];
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


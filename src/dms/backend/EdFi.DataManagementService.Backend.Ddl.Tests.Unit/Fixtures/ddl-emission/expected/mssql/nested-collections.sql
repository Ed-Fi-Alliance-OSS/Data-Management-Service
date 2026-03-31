IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');

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

IF OBJECT_ID(N'edfi.SchoolAddressPhoneNumber', N'U') IS NULL
CREATE TABLE [edfi].[SchoolAddressPhoneNumber]
(
    [DocumentId] bigint NOT NULL,
    [AddressOrdinal] int NOT NULL,
    [PhoneNumberOrdinal] int NOT NULL,
    [PhoneNumber] nvarchar(20) NOT NULL,
    CONSTRAINT [PK_SchoolAddressPhoneNumber] PRIMARY KEY ([DocumentId], [AddressOrdinal], [PhoneNumberOrdinal])
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
    WHERE name = N'FK_SchoolAddressPhoneNumber_SchoolAddress' AND parent_object_id = OBJECT_ID(N'edfi.SchoolAddressPhoneNumber')
)
ALTER TABLE [edfi].[SchoolAddressPhoneNumber]
ADD CONSTRAINT [FK_SchoolAddressPhoneNumber_SchoolAddress]
FOREIGN KEY ([DocumentId], [AddressOrdinal])
REFERENCES [edfi].[SchoolAddress] ([DocumentId], [AddressOrdinal])
ON DELETE CASCADE
ON UPDATE NO ACTION;

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
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiSchool' AS nvarchar(max)) + N'$.schoolId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 1
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
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiSchool' AS nvarchar(max)) + N'$.schoolId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 1
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
        WHERE del.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL))
        UNION
        SELECT del.[DocumentId]
        FROM deleted del
        LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
        WHERE i.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL))
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

CREATE OR ALTER TRIGGER [edfi].[TR_SchoolAddress_Stamp]
ON [edfi].[SchoolAddress]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId] AND del.[AddressOrdinal] = i.[AddressOrdinal]
        WHERE del.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[AddressOrdinal] <> del.[AddressOrdinal] OR (i.[AddressOrdinal] IS NULL AND del.[AddressOrdinal] IS NOT NULL) OR (i.[AddressOrdinal] IS NOT NULL AND del.[AddressOrdinal] IS NULL)) OR (i.[Street] <> del.[Street] OR (i.[Street] IS NULL AND del.[Street] IS NOT NULL) OR (i.[Street] IS NOT NULL AND del.[Street] IS NULL))
        UNION
        SELECT del.[DocumentId]
        FROM deleted del
        LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId] AND i.[AddressOrdinal] = del.[AddressOrdinal]
        WHERE i.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[AddressOrdinal] <> del.[AddressOrdinal] OR (i.[AddressOrdinal] IS NULL AND del.[AddressOrdinal] IS NOT NULL) OR (i.[AddressOrdinal] IS NOT NULL AND del.[AddressOrdinal] IS NULL)) OR (i.[Street] <> del.[Street] OR (i.[Street] IS NULL AND del.[Street] IS NOT NULL) OR (i.[Street] IS NOT NULL AND del.[Street] IS NULL))
    )
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_SchoolAddressPhoneNumber_Stamp]
ON [edfi].[SchoolAddressPhoneNumber]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId] AND del.[AddressOrdinal] = i.[AddressOrdinal] AND del.[PhoneNumberOrdinal] = i.[PhoneNumberOrdinal]
        WHERE del.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[AddressOrdinal] <> del.[AddressOrdinal] OR (i.[AddressOrdinal] IS NULL AND del.[AddressOrdinal] IS NOT NULL) OR (i.[AddressOrdinal] IS NOT NULL AND del.[AddressOrdinal] IS NULL)) OR (i.[PhoneNumberOrdinal] <> del.[PhoneNumberOrdinal] OR (i.[PhoneNumberOrdinal] IS NULL AND del.[PhoneNumberOrdinal] IS NOT NULL) OR (i.[PhoneNumberOrdinal] IS NOT NULL AND del.[PhoneNumberOrdinal] IS NULL)) OR (i.[PhoneNumber] <> del.[PhoneNumber] OR (i.[PhoneNumber] IS NULL AND del.[PhoneNumber] IS NOT NULL) OR (i.[PhoneNumber] IS NOT NULL AND del.[PhoneNumber] IS NULL))
        UNION
        SELECT del.[DocumentId]
        FROM deleted del
        LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId] AND i.[AddressOrdinal] = del.[AddressOrdinal] AND i.[PhoneNumberOrdinal] = del.[PhoneNumberOrdinal]
        WHERE i.[DocumentId] IS NULL OR (i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[AddressOrdinal] <> del.[AddressOrdinal] OR (i.[AddressOrdinal] IS NULL AND del.[AddressOrdinal] IS NOT NULL) OR (i.[AddressOrdinal] IS NOT NULL AND del.[AddressOrdinal] IS NULL)) OR (i.[PhoneNumberOrdinal] <> del.[PhoneNumberOrdinal] OR (i.[PhoneNumberOrdinal] IS NULL AND del.[PhoneNumberOrdinal] IS NOT NULL) OR (i.[PhoneNumberOrdinal] IS NOT NULL AND del.[PhoneNumberOrdinal] IS NULL)) OR (i.[PhoneNumber] <> del.[PhoneNumber] OR (i.[PhoneNumber] IS NULL AND del.[PhoneNumber] IS NOT NULL) OR (i.[PhoneNumber] IS NOT NULL AND del.[PhoneNumber] IS NULL))
    )
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
END;
GO


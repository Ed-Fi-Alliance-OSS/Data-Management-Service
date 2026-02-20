IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');

IF OBJECT_ID(N'edfi.School', N'U') IS NULL
CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.StudentSchoolAssociation', N'U') IS NULL
CREATE TABLE [edfi].[StudentSchoolAssociation]
(
    [DocumentId] bigint NOT NULL,
    [School_DocumentId] bigint NOT NULL,
    [SchoolId] int NOT NULL,
    [StudentUniqueId] nvarchar(32) NOT NULL,
    [EntryDate] date NOT NULL,
    CONSTRAINT [PK_StudentSchoolAssociation] PRIMARY KEY ([DocumentId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_StudentSchoolAssociation_School' AND parent_object_id = OBJECT_ID(N'edfi.StudentSchoolAssociation')
)
ALTER TABLE [edfi].[StudentSchoolAssociation]
ADD CONSTRAINT [FK_StudentSchoolAssociation_School]
FOREIGN KEY ([School_DocumentId])
REFERENCES [edfi].[School] ([DocumentId])
ON DELETE NO ACTION
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

GO
CREATE OR ALTER TRIGGER [edfi].[TR_StudentSchoolAssociation_Stamp]
ON [edfi].[StudentSchoolAssociation]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (SELECT [DocumentId] FROM inserted UNION SELECT [DocumentId] FROM deleted)
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([SchoolId]) OR UPDATE([StudentUniqueId]) OR UPDATE([EntryDate]))
    BEGIN
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL)) OR (i.[StudentUniqueId] <> del.[StudentUniqueId] OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL)) OR (i.[EntryDate] <> del.[EntryDate] OR (i.[EntryDate] IS NULL AND del.[EntryDate] IS NOT NULL) OR (i.[EntryDate] IS NOT NULL AND del.[EntryDate] IS NULL));
    END
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_StudentSchoolAssociation_ReferentialIdentity]
ON [edfi].[StudentSchoolAssociation]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dms].[ReferentialIdentity]
    WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 2;
    INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
    SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiStudentSchoolAssociation' + N'$$.schoolReference.schoolId=' + CAST(i.[SchoolId] AS nvarchar(max)) + N'#' + N'$$.studentReference.studentUniqueId=' + CAST(i.[StudentUniqueId] AS nvarchar(max)) + N'#' + N'$$.entryDate=' + CAST(i.[EntryDate] AS nvarchar(max))), i.[DocumentId], 2
    FROM inserted i;
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_School_Propagation]
ON [edfi].[School]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE r
    SET r.[SchoolId] = i.[SchoolId]
    FROM [edfi].[StudentSchoolAssociation] r
    INNER JOIN deleted d ON r.[School_DocumentId] = d.[DocumentId]
    INNER JOIN inserted i ON i.[DocumentId] = d.[DocumentId]
    WHERE (i.[SchoolId] <> d.[SchoolId] OR (i.[SchoolId] IS NULL AND d.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND d.[SchoolId] IS NULL));

END;


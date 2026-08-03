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
    [EntryTimestamp] datetime2(7) NOT NULL,
    [IsActive] bit NOT NULL,
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
CREATE OR ALTER TRIGGER [edfi].[TR_School_Propagation]
ON [edfi].[School]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF (UPDATE([SchoolId]))
    AND EXISTS (
        SELECT 1 FROM inserted i INNER JOIN deleted d ON i.[DocumentId] = d.[DocumentId]
        WHERE (i.[SchoolId] <> d.[SchoolId] OR (i.[SchoolId] IS NULL AND d.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND d.[SchoolId] IS NULL))
    )
    BEGIN
        UPDATE r
        SET r.[SchoolId] = i.[SchoolId]
        FROM [edfi].[StudentSchoolAssociation] r
        INNER JOIN deleted d ON r.[School_DocumentId] = d.[DocumentId]
        INNER JOIN inserted i ON i.[DocumentId] = d.[DocumentId]
        WHERE ((i.[SchoolId] <> d.[SchoolId] OR (i.[SchoolId] IS NULL AND d.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND d.[SchoolId] IS NULL)))
        AND ((r.[SchoolId] = d.[SchoolId]) OR (r.[SchoolId] IS NULL AND d.[SchoolId] IS NULL));

    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_School_Stamp]
ON [edfi].[School]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @insertedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
    INSERT INTO @insertedDocs ([DocumentId])
    SELECT i.[DocumentId]
    FROM inserted i
    LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
    WHERE del.[DocumentId] IS NULL;
    IF EXISTS (SELECT 1 FROM @insertedDocs)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[ContentLastModifiedAt] = sysutcdatetime(),
            r.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[IdentityLastModifiedAt] = sysutcdatetime(),
            r.[CreatedAt] = sysutcdatetime()
        FROM [edfi].[School] r
        INNER JOIN @insertedDocs s ON s.[DocumentId] = r.[DocumentId];
    END
    DECLARE @stamped TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL)))
    )
    INSERT INTO @stamped ([DocumentId])
    SELECT [DocumentId] FROM affectedDocs;
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[ContentLastModifiedAt] = sysutcdatetime()
        FROM [edfi].[School] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([SchoolId]))
    BEGIN
        UPDATE r
        SET r.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [edfi].[School] r
        INNER JOIN inserted i ON i.[DocumentId] = r.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL));
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_StudentSchoolAssociation_Stamp]
ON [edfi].[StudentSchoolAssociation]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @insertedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
    INSERT INTO @insertedDocs ([DocumentId])
    SELECT i.[DocumentId]
    FROM inserted i
    LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
    WHERE del.[DocumentId] IS NULL;
    IF EXISTS (SELECT 1 FROM @insertedDocs)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[ContentLastModifiedAt] = sysutcdatetime(),
            r.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[IdentityLastModifiedAt] = sysutcdatetime(),
            r.[CreatedAt] = sysutcdatetime()
        FROM [edfi].[StudentSchoolAssociation] r
        INNER JOIN @insertedDocs s ON s.[DocumentId] = r.[DocumentId];
    END
    DECLARE @stamped TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[School_DocumentId] <> del.[School_DocumentId] OR (i.[School_DocumentId] IS NULL AND del.[School_DocumentId] IS NOT NULL) OR (i.[School_DocumentId] IS NOT NULL AND del.[School_DocumentId] IS NULL)) OR (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL)) OR (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL)) OR (i.[EntryDate] <> del.[EntryDate] OR (i.[EntryDate] IS NULL AND del.[EntryDate] IS NOT NULL) OR (i.[EntryDate] IS NOT NULL AND del.[EntryDate] IS NULL)) OR (i.[EntryTimestamp] <> del.[EntryTimestamp] OR (i.[EntryTimestamp] IS NULL AND del.[EntryTimestamp] IS NOT NULL) OR (i.[EntryTimestamp] IS NOT NULL AND del.[EntryTimestamp] IS NULL)) OR (i.[IsActive] <> del.[IsActive] OR (i.[IsActive] IS NULL AND del.[IsActive] IS NOT NULL) OR (i.[IsActive] IS NOT NULL AND del.[IsActive] IS NULL)))
    )
    INSERT INTO @stamped ([DocumentId])
    SELECT [DocumentId] FROM affectedDocs;
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[ContentLastModifiedAt] = sysutcdatetime()
        FROM [edfi].[StudentSchoolAssociation] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([SchoolId]) OR UPDATE([StudentUniqueId]) OR UPDATE([EntryDate]) OR UPDATE([EntryTimestamp]) OR UPDATE([IsActive]))
    BEGIN
        UPDATE r
        SET r.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [edfi].[StudentSchoolAssociation] r
        INNER JOIN inserted i ON i.[DocumentId] = r.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL)) OR (CAST(i.[StudentUniqueId] AS varbinary(max)) <> CAST(del.[StudentUniqueId] AS varbinary(max)) OR (i.[StudentUniqueId] IS NULL AND del.[StudentUniqueId] IS NOT NULL) OR (i.[StudentUniqueId] IS NOT NULL AND del.[StudentUniqueId] IS NULL)) OR (i.[EntryDate] <> del.[EntryDate] OR (i.[EntryDate] IS NULL AND del.[EntryDate] IS NOT NULL) OR (i.[EntryDate] IS NOT NULL AND del.[EntryDate] IS NULL)) OR (i.[EntryTimestamp] <> del.[EntryTimestamp] OR (i.[EntryTimestamp] IS NULL AND del.[EntryTimestamp] IS NOT NULL) OR (i.[EntryTimestamp] IS NOT NULL AND del.[EntryTimestamp] IS NULL)) OR (i.[IsActive] <> del.[IsActive] OR (i.[IsActive] IS NULL AND del.[IsActive] IS NOT NULL) OR (i.[IsActive] IS NOT NULL AND del.[IsActive] IS NULL));
    END
END;
GO


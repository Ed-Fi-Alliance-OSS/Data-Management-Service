IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');

IF OBJECT_ID(N'edfi.CourseRegistration', N'U') IS NULL
CREATE TABLE [edfi].[CourseRegistration]
(
    [DocumentId] bigint NOT NULL,
    [CourseOffering_DocumentId] bigint NOT NULL,
    [School_DocumentId] bigint NOT NULL,
    [CourseOffering_SchoolId] AS (CASE WHEN [CourseOffering_DocumentId] IS NULL THEN NULL ELSE [SchoolId_Unified] END) PERSISTED,
    [CourseOffering_LocalCourseCode] nvarchar(60) NOT NULL,
    [School_SchoolId] AS (CASE WHEN [School_DocumentId] IS NULL THEN NULL ELSE [SchoolId_Unified] END) PERSISTED,
    [RegistrationDate] date NOT NULL,
    [SchoolId_Unified] int NOT NULL,
    CONSTRAINT [PK_CourseRegistration] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.School', N'U') IS NULL
CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_CourseRegistration_CourseOffering' AND parent_object_id = OBJECT_ID(N'edfi.CourseRegistration')
)
ALTER TABLE [edfi].[CourseRegistration]
ADD CONSTRAINT [FK_CourseRegistration_CourseOffering]
FOREIGN KEY ([CourseOffering_DocumentId])
REFERENCES [edfi].[CourseOffering] ([DocumentId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_CourseRegistration_School' AND parent_object_id = OBJECT_ID(N'edfi.CourseRegistration')
)
ALTER TABLE [edfi].[CourseRegistration]
ADD CONSTRAINT [FK_CourseRegistration_School]
FOREIGN KEY ([School_DocumentId])
REFERENCES [edfi].[School] ([DocumentId])
ON DELETE NO ACTION
ON UPDATE NO ACTION;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_CourseRegistration_Stamp]
ON [edfi].[CourseRegistration]
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
        FROM [edfi].[CourseRegistration] r
        INNER JOIN @insertedDocs s ON s.[DocumentId] = r.[DocumentId];
    END
    DECLARE @stamped TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
    ;WITH affectedDocs AS (
        SELECT i.[DocumentId]
        FROM inserted i
        LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[CourseOffering_DocumentId] <> del.[CourseOffering_DocumentId] OR (i.[CourseOffering_DocumentId] IS NULL AND del.[CourseOffering_DocumentId] IS NOT NULL) OR (i.[CourseOffering_DocumentId] IS NOT NULL AND del.[CourseOffering_DocumentId] IS NULL)) OR (i.[School_DocumentId] <> del.[School_DocumentId] OR (i.[School_DocumentId] IS NULL AND del.[School_DocumentId] IS NOT NULL) OR (i.[School_DocumentId] IS NOT NULL AND del.[School_DocumentId] IS NULL)) OR (CAST(i.[CourseOffering_LocalCourseCode] AS varbinary(max)) <> CAST(del.[CourseOffering_LocalCourseCode] AS varbinary(max)) OR (i.[CourseOffering_LocalCourseCode] IS NULL AND del.[CourseOffering_LocalCourseCode] IS NOT NULL) OR (i.[CourseOffering_LocalCourseCode] IS NOT NULL AND del.[CourseOffering_LocalCourseCode] IS NULL)) OR (i.[RegistrationDate] <> del.[RegistrationDate] OR (i.[RegistrationDate] IS NULL AND del.[RegistrationDate] IS NOT NULL) OR (i.[RegistrationDate] IS NOT NULL AND del.[RegistrationDate] IS NULL)) OR (i.[SchoolId_Unified] <> del.[SchoolId_Unified] OR (i.[SchoolId_Unified] IS NULL AND del.[SchoolId_Unified] IS NOT NULL) OR (i.[SchoolId_Unified] IS NOT NULL AND del.[SchoolId_Unified] IS NULL)))
    )
    INSERT INTO @stamped ([DocumentId])
    SELECT [DocumentId] FROM affectedDocs;
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[ContentLastModifiedAt] = sysutcdatetime()
        FROM [edfi].[CourseRegistration] r
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([SchoolId_Unified]) OR UPDATE([CourseOffering_LocalCourseCode]) OR UPDATE([RegistrationDate]))
    BEGIN
        UPDATE r
        SET r.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
            r.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [edfi].[CourseRegistration] r
        INNER JOIN inserted i ON i.[DocumentId] = r.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[SchoolId_Unified] <> del.[SchoolId_Unified] OR (i.[SchoolId_Unified] IS NULL AND del.[SchoolId_Unified] IS NOT NULL) OR (i.[SchoolId_Unified] IS NOT NULL AND del.[SchoolId_Unified] IS NULL)) OR (CAST(i.[CourseOffering_LocalCourseCode] AS varbinary(max)) <> CAST(del.[CourseOffering_LocalCourseCode] AS varbinary(max)) OR (i.[CourseOffering_LocalCourseCode] IS NULL AND del.[CourseOffering_LocalCourseCode] IS NOT NULL) OR (i.[CourseOffering_LocalCourseCode] IS NOT NULL AND del.[CourseOffering_LocalCourseCode] IS NULL)) OR (i.[RegistrationDate] <> del.[RegistrationDate] OR (i.[RegistrationDate] IS NULL AND del.[RegistrationDate] IS NOT NULL) OR (i.[RegistrationDate] IS NOT NULL AND del.[RegistrationDate] IS NULL));
    END
END;
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
        SET r.[SchoolId_Unified] = i.[SchoolId]
        FROM [edfi].[CourseRegistration] r
        INNER JOIN deleted d ON r.[School_DocumentId] = d.[DocumentId]
        INNER JOIN inserted i ON i.[DocumentId] = d.[DocumentId]
        WHERE ((i.[SchoolId] <> d.[SchoolId] OR (i.[SchoolId] IS NULL AND d.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND d.[SchoolId] IS NULL)))
        AND ((r.[SchoolId_Unified] = d.[SchoolId]) OR (r.[SchoolId_Unified] IS NULL AND d.[SchoolId] IS NULL));

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


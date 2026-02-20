IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');

IF OBJECT_ID(N'edfi.School', N'U') IS NULL
CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

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
    IF NOT EXISTS (SELECT 1 FROM deleted) OR (UPDATE([SchoolId]))
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiSchool' + N'$$.schoolId=' + CAST(i.[SchoolId] AS nvarchar(max))), i.[DocumentId], 1
        FROM inserted i;
    END
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_CourseRegistration_Stamp]
ON [edfi].[CourseRegistration]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (SELECT [DocumentId] FROM inserted UNION SELECT [DocumentId] FROM deleted)
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([SchoolId_Unified]) OR UPDATE([CourseOffering_LocalCourseCode]) OR UPDATE([RegistrationDate]))
    BEGIN
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[SchoolId_Unified] <> del.[SchoolId_Unified] OR (i.[SchoolId_Unified] IS NULL AND del.[SchoolId_Unified] IS NOT NULL) OR (i.[SchoolId_Unified] IS NOT NULL AND del.[SchoolId_Unified] IS NULL)) OR (i.[CourseOffering_LocalCourseCode] <> del.[CourseOffering_LocalCourseCode] OR (i.[CourseOffering_LocalCourseCode] IS NULL AND del.[CourseOffering_LocalCourseCode] IS NOT NULL) OR (i.[CourseOffering_LocalCourseCode] IS NOT NULL AND del.[CourseOffering_LocalCourseCode] IS NULL)) OR (i.[RegistrationDate] <> del.[RegistrationDate] OR (i.[RegistrationDate] IS NULL AND del.[RegistrationDate] IS NOT NULL) OR (i.[RegistrationDate] IS NOT NULL AND del.[RegistrationDate] IS NULL));
    END
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_CourseRegistration_ReferentialIdentity]
ON [edfi].[CourseRegistration]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted) OR (UPDATE([SchoolId_Unified]) OR UPDATE([CourseOffering_LocalCourseCode]) OR UPDATE([RegistrationDate]))
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 2;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiCourseRegistration' + N'$$.courseOfferingReference.schoolId=' + CAST(i.[CourseOffering_SchoolId] AS nvarchar(max)) + N'#' + N'$$.courseOfferingReference.localCourseCode=' + CAST(i.[CourseOffering_LocalCourseCode] AS nvarchar(max)) + N'#' + N'$$.schoolReference.schoolId=' + CAST(i.[School_SchoolId] AS nvarchar(max)) + N'#' + N'$$.registrationDate=' + CAST(i.[RegistrationDate] AS nvarchar(max))), i.[DocumentId], 2
        FROM inserted i;
    END
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_School_Propagation]
ON [edfi].[School]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE r
    SET r.[SchoolId_Unified] = i.[SchoolId]
    FROM [edfi].[CourseRegistration] r
    INNER JOIN deleted d ON r.[School_DocumentId] = d.[DocumentId]
    INNER JOIN inserted i ON i.[DocumentId] = d.[DocumentId]
    WHERE (i.[SchoolId] <> d.[SchoolId] OR (i.[SchoolId] IS NULL AND d.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND d.[SchoolId] IS NULL));

END;


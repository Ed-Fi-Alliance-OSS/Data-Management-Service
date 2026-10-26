IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');

IF OBJECT_ID(N'edfi.LocalEducationAgency', N'U') IS NULL
CREATE TABLE [edfi].[LocalEducationAgency]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_LocalEducationAgency] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.School', N'U') IS NULL
CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.EducationOrganizationIdentity', N'U') IS NULL
CREATE TABLE [edfi].[EducationOrganizationIdentity]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    [ResourceKeyId] smallint NOT NULL,
    [Discriminator] nvarchar(50) NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdentity] PRIMARY KEY ([DocumentId])
);

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_LocalEducationAgency_EducationOrganizationIdentity' AND parent_object_id = OBJECT_ID(N'edfi.LocalEducationAgency')
)
ALTER TABLE [edfi].[LocalEducationAgency]
ADD CONSTRAINT [FK_LocalEducationAgency_EducationOrganizationIdentity]
FOREIGN KEY ([DocumentId])
REFERENCES [edfi].[EducationOrganizationIdentity] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_School_EducationOrganizationIdentity' AND parent_object_id = OBJECT_ID(N'edfi.School')
)
ALTER TABLE [edfi].[School]
ADD CONSTRAINT [FK_School_EducationOrganizationIdentity]
FOREIGN KEY ([DocumentId])
REFERENCES [edfi].[EducationOrganizationIdentity] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_EducationOrganizationIdentity_DocumentResourceKey' AND parent_object_id = OBJECT_ID(N'edfi.EducationOrganizationIdentity')
)
ALTER TABLE [edfi].[EducationOrganizationIdentity]
ADD CONSTRAINT [FK_EducationOrganizationIdentity_DocumentResourceKey]
FOREIGN KEY ([DocumentId], [ResourceKeyId])
REFERENCES [dms].[Document] ([DocumentId], [ResourceKeyId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

GO
CREATE OR ALTER VIEW [edfi].[EducationOrganization_View] AS
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(2 AS smallint) AS [ResourceKeyId], CAST(N'Ed-Fi:School' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[School]
UNION ALL
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(3 AS smallint) AS [ResourceKeyId], CAST(N'Ed-Fi:LocalEducationAgency' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[LocalEducationAgency]
;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_AbstractIdentity]
ON [edfi].[LocalEducationAgency]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        IF EXISTS (SELECT 1 FROM [edfi].[EducationOrganizationIdentity] t INNER JOIN inserted i ON t.[DocumentId] = i.[DocumentId])
        BEGIN
            UPDATE t
            SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
            FROM [edfi].[EducationOrganizationIdentity] t
            INNER JOIN inserted s ON t.[DocumentId] = s.[DocumentId]
            OPTION (KEEPFIXED PLAN);
        END
        INSERT INTO [edfi].[EducationOrganizationIdentity] ([DocumentId], [EducationOrganizationId], [ResourceKeyId], [Discriminator])
        SELECT s.[DocumentId], s.[EducationOrganizationId], 3, N'Ed-Fi:LocalEducationAgency'
        FROM inserted s
        LEFT JOIN [edfi].[EducationOrganizationIdentity] existing ON existing.[DocumentId] = s.[DocumentId]
        WHERE existing.[DocumentId] IS NULL;
    END
    ELSE IF (UPDATE([EducationOrganizationId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[EducationOrganizationId] <> d.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND d.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND d.[EducationOrganizationId] IS NULL));
        IF EXISTS (SELECT 1 FROM @changedDocs)
        BEGIN
            UPDATE t
            SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
            FROM [edfi].[EducationOrganizationIdentity] t
            INNER JOIN (SELECT i.* FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId]) AS s ON t.[DocumentId] = s.[DocumentId]
            OPTION (KEEPFIXED PLAN);
            INSERT INTO [edfi].[EducationOrganizationIdentity] ([DocumentId], [EducationOrganizationId], [ResourceKeyId], [Discriminator])
            SELECT s.[DocumentId], s.[EducationOrganizationId], 3, N'Ed-Fi:LocalEducationAgency'
            FROM (SELECT i.* FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId]) AS s
            LEFT JOIN [edfi].[EducationOrganizationIdentity] existing ON existing.[DocumentId] = s.[DocumentId]
            WHERE existing.[DocumentId] IS NULL;
        END
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_Stamp]
ON [edfi].[LocalEducationAgency]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @stamped TABLE (
        [DocumentId] bigint NOT NULL PRIMARY KEY,
        [ContentVersion] bigint NOT NULL
    );
    INSERT INTO @stamped ([DocumentId], [ContentVersion])
    SELECT d.[DocumentId], d.[ContentVersion]
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
    LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
    WHERE del.[DocumentId] IS NULL;
    IF EXISTS (SELECT 1 FROM deleted) AND (NOT EXISTS (SELECT 1 FROM inserted) OR UPDATE([DocumentId]) OR UPDATE([EducationOrganizationId]))
    BEGIN
        ;WITH affectedDocs AS (
            SELECT i.[DocumentId]
            FROM inserted i
            LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
            WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[EducationOrganizationId] <> del.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND del.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND del.[EducationOrganizationId] IS NULL)))
            UNION ALL
            SELECT del.[DocumentId]
            FROM deleted del
            LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
            WHERE i.[DocumentId] IS NULL
        )
        UPDATE d
        SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence]
        OUTPUT inserted.[DocumentId], inserted.[ContentVersion] INTO @stamped
        FROM [dms].[Document] d
        INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = sysutcdatetime()
        FROM [edfi].[LocalEducationAgency] r WITH (FORCESEEK)
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
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
        IF EXISTS (SELECT 1 FROM [edfi].[EducationOrganizationIdentity] t INNER JOIN inserted i ON t.[DocumentId] = i.[DocumentId])
        BEGIN
            UPDATE t
            SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
            FROM [edfi].[EducationOrganizationIdentity] t
            INNER JOIN inserted s ON t.[DocumentId] = s.[DocumentId]
            OPTION (KEEPFIXED PLAN);
        END
        INSERT INTO [edfi].[EducationOrganizationIdentity] ([DocumentId], [EducationOrganizationId], [ResourceKeyId], [Discriminator])
        SELECT s.[DocumentId], s.[EducationOrganizationId], 2, N'Ed-Fi:School'
        FROM inserted s
        LEFT JOIN [edfi].[EducationOrganizationIdentity] existing ON existing.[DocumentId] = s.[DocumentId]
        WHERE existing.[DocumentId] IS NULL;
    END
    ELSE IF (UPDATE([EducationOrganizationId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[EducationOrganizationId] <> d.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND d.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND d.[EducationOrganizationId] IS NULL));
        IF EXISTS (SELECT 1 FROM @changedDocs)
        BEGIN
            UPDATE t
            SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
            FROM [edfi].[EducationOrganizationIdentity] t
            INNER JOIN (SELECT i.* FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId]) AS s ON t.[DocumentId] = s.[DocumentId]
            OPTION (KEEPFIXED PLAN);
            INSERT INTO [edfi].[EducationOrganizationIdentity] ([DocumentId], [EducationOrganizationId], [ResourceKeyId], [Discriminator])
            SELECT s.[DocumentId], s.[EducationOrganizationId], 2, N'Ed-Fi:School'
            FROM (SELECT i.* FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId]) AS s
            LEFT JOIN [edfi].[EducationOrganizationIdentity] existing ON existing.[DocumentId] = s.[DocumentId]
            WHERE existing.[DocumentId] IS NULL;
        END
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
        [ContentVersion] bigint NOT NULL
    );
    INSERT INTO @stamped ([DocumentId], [ContentVersion])
    SELECT d.[DocumentId], d.[ContentVersion]
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
    LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
    WHERE del.[DocumentId] IS NULL;
    IF EXISTS (SELECT 1 FROM deleted) AND (NOT EXISTS (SELECT 1 FROM inserted) OR UPDATE([DocumentId]) OR UPDATE([EducationOrganizationId]))
    BEGIN
        ;WITH affectedDocs AS (
            SELECT i.[DocumentId]
            FROM inserted i
            LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
            WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[EducationOrganizationId] <> del.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND del.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND del.[EducationOrganizationId] IS NULL)))
            UNION ALL
            SELECT del.[DocumentId]
            FROM deleted del
            LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]
            WHERE i.[DocumentId] IS NULL
        )
        UPDATE d
        SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence]
        OUTPUT inserted.[DocumentId], inserted.[ContentVersion] INTO @stamped
        FROM [dms].[Document] d
        INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    END
    IF EXISTS (SELECT 1 FROM @stamped)
    BEGIN
        UPDATE r
        SET r.[ContentVersion] = s.[ContentVersion],
            r.[ContentLastModifiedAt] = sysutcdatetime()
        FROM [edfi].[School] r WITH (FORCESEEK)
        INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
    END
END;
GO


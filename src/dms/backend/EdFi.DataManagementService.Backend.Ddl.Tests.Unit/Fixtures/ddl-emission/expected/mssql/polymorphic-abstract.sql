IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');

IF OBJECT_ID(N'edfi.School', N'U') IS NULL
CREATE TABLE [edfi].[School]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.LocalEducationAgency', N'U') IS NULL
CREATE TABLE [edfi].[LocalEducationAgency]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_LocalEducationAgency] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.EducationOrganizationIdentity', N'U') IS NULL
CREATE TABLE [edfi].[EducationOrganizationIdentity]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    [Discriminator] nvarchar(50) NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdentity] PRIMARY KEY ([DocumentId])
);

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
    WHERE name = N'FK_EducationOrganizationIdentity_Document' AND parent_object_id = OBJECT_ID(N'edfi.EducationOrganizationIdentity')
)
ALTER TABLE [edfi].[EducationOrganizationIdentity]
ADD CONSTRAINT [FK_EducationOrganizationIdentity_Document]
FOREIGN KEY ([DocumentId])
REFERENCES [dms].[Document] ([DocumentId])
ON DELETE CASCADE
ON UPDATE NO ACTION;

GO
CREATE OR ALTER VIEW [edfi].[EducationOrganization_View] AS
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(N'Ed-Fi:School' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[School]
UNION ALL
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(N'Ed-Fi:LocalEducationAgency' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[LocalEducationAgency]
;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_Stamp]
ON [edfi].[LocalEducationAgency]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH affectedDocs AS (SELECT [DocumentId] FROM inserted UNION SELECT [DocumentId] FROM deleted)
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([EducationOrganizationId]))
    BEGIN
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[EducationOrganizationId] <> del.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND del.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND del.[EducationOrganizationId] IS NULL));
    END
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_AbstractIdentity]
ON [edfi].[LocalEducationAgency]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        MERGE [edfi].[EducationOrganizationIdentity] AS t
        USING inserted AS s ON t.[DocumentId] = s.[DocumentId]
        WHEN MATCHED THEN UPDATE SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
        WHEN NOT MATCHED THEN INSERT ([DocumentId], [EducationOrganizationId], [Discriminator])
        VALUES (s.[DocumentId], s.[EducationOrganizationId], N'Ed-Fi:LocalEducationAgency');
    END
    ELSE IF (UPDATE([EducationOrganizationId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[EducationOrganizationId] <> d.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND d.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND d.[EducationOrganizationId] IS NULL));
        MERGE [edfi].[EducationOrganizationIdentity] AS t
        USING (SELECT i.* FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId]) AS s ON t.[DocumentId] = s.[DocumentId]
        WHEN MATCHED THEN UPDATE SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
        WHEN NOT MATCHED THEN INSERT ([DocumentId], [EducationOrganizationId], [Discriminator])
        VALUES (s.[DocumentId], s.[EducationOrganizationId], N'Ed-Fi:LocalEducationAgency');
    END
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_ReferentialIdentity]
ON [edfi].[LocalEducationAgency]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 3;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiLocalEducationAgency' + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 3
        FROM inserted i;
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiEducationOrganization' + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 1
        FROM inserted i;
    END
    ELSE IF (UPDATE([EducationOrganizationId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[EducationOrganizationId] <> d.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND d.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND d.[EducationOrganizationId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 3;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiLocalEducationAgency' + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 3
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiEducationOrganization' + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 1
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
    ;WITH affectedDocs AS (SELECT [DocumentId] FROM inserted UNION SELECT [DocumentId] FROM deleted)
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId];
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([EducationOrganizationId]))
    BEGIN
        UPDATE d
        SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()
        FROM [dms].[Document] d
        INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]
        INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]
        WHERE (i.[EducationOrganizationId] <> del.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND del.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND del.[EducationOrganizationId] IS NULL));
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
        MERGE [edfi].[EducationOrganizationIdentity] AS t
        USING inserted AS s ON t.[DocumentId] = s.[DocumentId]
        WHEN MATCHED THEN UPDATE SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
        WHEN NOT MATCHED THEN INSERT ([DocumentId], [EducationOrganizationId], [Discriminator])
        VALUES (s.[DocumentId], s.[EducationOrganizationId], N'Ed-Fi:School');
    END
    ELSE IF (UPDATE([EducationOrganizationId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[EducationOrganizationId] <> d.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND d.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND d.[EducationOrganizationId] IS NULL));
        MERGE [edfi].[EducationOrganizationIdentity] AS t
        USING (SELECT i.* FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId]) AS s ON t.[DocumentId] = s.[DocumentId]
        WHEN MATCHED THEN UPDATE SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
        WHEN NOT MATCHED THEN INSERT ([DocumentId], [EducationOrganizationId], [Discriminator])
        VALUES (s.[DocumentId], s.[EducationOrganizationId], N'Ed-Fi:School');
    END
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
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiSchool' + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 2
        FROM inserted i;
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiEducationOrganization' + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 1
        FROM inserted i;
    END
    ELSE IF (UPDATE([EducationOrganizationId]))
    BEGIN
        DECLARE @changedDocs TABLE ([DocumentId] bigint NOT NULL);
        INSERT INTO @changedDocs ([DocumentId])
        SELECT i.[DocumentId]
        FROM inserted i INNER JOIN deleted d ON d.[DocumentId] = i.[DocumentId]
        WHERE (i.[EducationOrganizationId] <> d.[EducationOrganizationId] OR (i.[EducationOrganizationId] IS NULL AND d.[EducationOrganizationId] IS NOT NULL) OR (i.[EducationOrganizationId] IS NOT NULL AND d.[EducationOrganizationId] IS NULL));
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 2;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiSchool' + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 2
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', N'Ed-FiEducationOrganization' + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 1
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;


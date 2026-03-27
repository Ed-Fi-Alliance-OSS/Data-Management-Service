IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'edfi')
    EXEC('CREATE SCHEMA [edfi]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'auth')
    EXEC('CREATE SCHEMA [auth]');

IF OBJECT_ID(N'edfi.LocalEducationAgency', N'U') IS NULL
CREATE TABLE [edfi].[LocalEducationAgency]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    [StateEducationAgency_EducationOrganizationId] int NULL,
    CONSTRAINT [PK_LocalEducationAgency] PRIMARY KEY ([DocumentId])
);

IF OBJECT_ID(N'edfi.StateEducationAgency', N'U') IS NULL
CREATE TABLE [edfi].[StateEducationAgency]
(
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_StateEducationAgency] PRIMARY KEY ([DocumentId])
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
    WHERE name = N'FK_StateEducationAgency_EducationOrganizationIdentity' AND parent_object_id = OBJECT_ID(N'edfi.StateEducationAgency')
)
ALTER TABLE [edfi].[StateEducationAgency]
ADD CONSTRAINT [FK_StateEducationAgency_EducationOrganizationIdentity]
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

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'auth' AND t.name = N'EducationOrganizationIdToEducationOrganizationId' AND i.name = N'IX_EducationOrganizationIdToEducationOrganizationId_Target'
)
CREATE INDEX [IX_EducationOrganizationIdToEducationOrganizationId_Target] ON [auth].[EducationOrganizationIdToEducationOrganizationId] ([TargetEducationOrganizationId]) INCLUDE ([SourceEducationOrganizationId]);

GO
CREATE OR ALTER VIEW [edfi].[EducationOrganization_View] AS
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(N'Ed-Fi:LocalEducationAgency' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[LocalEducationAgency]
UNION ALL
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(N'Ed-Fi:StateEducationAgency' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[StateEducationAgency]
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

CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_AuthHierarchy_Delete]
ON [edfi].[LocalEducationAgency]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE tbd
    FROM [auth].[EducationOrganizationIdToEducationOrganizationId] AS tbd
        INNER JOIN (
            SELECT d1.[SourceEducationOrganizationId], d2.[TargetEducationOrganizationId]
            FROM (
                SELECT tuples.[SourceEducationOrganizationId], old.[EducationOrganizationId]
                FROM deleted old
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON old.[StateEducationAgency_EducationOrganizationId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[StateEducationAgency_EducationOrganizationId] IS NOT NULL
            ) AS d1
            CROSS JOIN
            (
                SELECT old.[EducationOrganizationId], tuples.[TargetEducationOrganizationId]
                FROM deleted old
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON old.[EducationOrganizationId] = tuples.[SourceEducationOrganizationId]
            ) AS d2
            WHERE d1.[EducationOrganizationId] = d2.[EducationOrganizationId]
        ) AS cj
            ON tbd.[SourceEducationOrganizationId] = cj.[SourceEducationOrganizationId]
            AND tbd.[TargetEducationOrganizationId] = cj.[TargetEducationOrganizationId];

    DELETE tuples
    FROM [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
        INNER JOIN deleted old
            ON tuples.[SourceEducationOrganizationId] = old.[EducationOrganizationId]
            AND tuples.[TargetEducationOrganizationId] = old.[EducationOrganizationId];
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_AuthHierarchy_Insert]
ON [edfi].[LocalEducationAgency]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
    SELECT new.[EducationOrganizationId], new.[EducationOrganizationId]
    FROM inserted new;

    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
    SELECT sources.[SourceEducationOrganizationId], targets.[TargetEducationOrganizationId]
    FROM (
        SELECT tuples.[SourceEducationOrganizationId], new.[EducationOrganizationId]
        FROM inserted new
            INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                ON new.[StateEducationAgency_EducationOrganizationId] = tuples.[TargetEducationOrganizationId]
        WHERE new.[StateEducationAgency_EducationOrganizationId] IS NOT NULL
    ) AS sources
    CROSS JOIN
    (
        SELECT new.[EducationOrganizationId], tuples.[TargetEducationOrganizationId]
        FROM inserted new
            INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                ON new.[EducationOrganizationId] = tuples.[SourceEducationOrganizationId]
    ) AS targets
    WHERE sources.[EducationOrganizationId] = targets.[EducationOrganizationId];
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_AuthHierarchy_Update]
ON [edfi].[LocalEducationAgency]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE tbd
    FROM [auth].[EducationOrganizationIdToEducationOrganizationId] AS tbd
        INNER JOIN (
            SELECT d1.[SourceEducationOrganizationId], d2.[TargetEducationOrganizationId]
            FROM (
                SELECT tuples.[SourceEducationOrganizationId], new.[EducationOrganizationId]
                FROM inserted new
                    INNER JOIN deleted old
                        ON old.[EducationOrganizationId] = new.[EducationOrganizationId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON old.[StateEducationAgency_EducationOrganizationId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[StateEducationAgency_EducationOrganizationId] IS NOT NULL
                    AND (new.[StateEducationAgency_EducationOrganizationId] IS NULL OR old.[StateEducationAgency_EducationOrganizationId] <> new.[StateEducationAgency_EducationOrganizationId])

                EXCEPT

                SELECT tuples.[SourceEducationOrganizationId], new.[EducationOrganizationId]
                FROM inserted new
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON new.[StateEducationAgency_EducationOrganizationId] = tuples.[TargetEducationOrganizationId]
            ) AS d1
            CROSS JOIN
            (
                SELECT new.[EducationOrganizationId], tuples.[TargetEducationOrganizationId]
                FROM inserted new
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON new.[EducationOrganizationId] = tuples.[SourceEducationOrganizationId]
            ) AS d2
            WHERE d1.[EducationOrganizationId] = d2.[EducationOrganizationId]
        ) AS cj
            ON tbd.[SourceEducationOrganizationId] = cj.[SourceEducationOrganizationId]
            AND tbd.[TargetEducationOrganizationId] = cj.[TargetEducationOrganizationId];

    MERGE INTO [auth].[EducationOrganizationIdToEducationOrganizationId] target
    USING (
        SELECT sources.[SourceEducationOrganizationId], targets.[TargetEducationOrganizationId]
        FROM (
            SELECT tuples.[SourceEducationOrganizationId], new.[EducationOrganizationId]
            FROM inserted new
                INNER JOIN deleted old
                    ON new.[EducationOrganizationId] = old.[EducationOrganizationId]
                INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                    ON new.[StateEducationAgency_EducationOrganizationId] = tuples.[TargetEducationOrganizationId]
            WHERE (old.[StateEducationAgency_EducationOrganizationId] IS NULL AND new.[StateEducationAgency_EducationOrganizationId] IS NOT NULL)
                OR old.[StateEducationAgency_EducationOrganizationId] <> new.[StateEducationAgency_EducationOrganizationId]
        ) AS sources
        CROSS JOIN
        (
            SELECT new.[EducationOrganizationId], tuples.[TargetEducationOrganizationId]
            FROM inserted new
                INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                    ON new.[EducationOrganizationId] = tuples.[SourceEducationOrganizationId]
        ) AS targets
        WHERE sources.[EducationOrganizationId] = targets.[EducationOrganizationId]
    ) AS source
        ON target.[SourceEducationOrganizationId] = source.[SourceEducationOrganizationId]
        AND target.[TargetEducationOrganizationId] = source.[TargetEducationOrganizationId]
    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
        VALUES (source.[SourceEducationOrganizationId], source.[TargetEducationOrganizationId]);
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
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 2;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiLocalEducationAgency' AS nvarchar(max)) + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 2
        FROM inserted i;
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEducationOrganization' AS nvarchar(max)) + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 1
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
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiLocalEducationAgency' AS nvarchar(max)) + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 2
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEducationOrganization' AS nvarchar(max)) + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 1
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
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

CREATE OR ALTER TRIGGER [edfi].[TR_StateEducationAgency_AbstractIdentity]
ON [edfi].[StateEducationAgency]
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
        VALUES (s.[DocumentId], s.[EducationOrganizationId], N'Ed-Fi:StateEducationAgency');
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
        VALUES (s.[DocumentId], s.[EducationOrganizationId], N'Ed-Fi:StateEducationAgency');
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_StateEducationAgency_AuthHierarchy_Delete]
ON [edfi].[StateEducationAgency]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE tuples
    FROM [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
        INNER JOIN deleted old
            ON tuples.[SourceEducationOrganizationId] = old.[EducationOrganizationId]
            AND tuples.[TargetEducationOrganizationId] = old.[EducationOrganizationId];
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_StateEducationAgency_AuthHierarchy_Insert]
ON [edfi].[StateEducationAgency]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
    SELECT new.[EducationOrganizationId], new.[EducationOrganizationId]
    FROM inserted new;
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_StateEducationAgency_ReferentialIdentity]
ON [edfi].[StateEducationAgency]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 3;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStateEducationAgency' AS nvarchar(max)) + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 3
        FROM inserted i;
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM inserted) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEducationOrganization' AS nvarchar(max)) + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 1
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
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiStateEducationAgency' AS nvarchar(max)) + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 3
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
        DELETE FROM [dms].[ReferentialIdentity]
        WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 1;
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        SELECT [dms].[uuidv5]('edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiEducationOrganization' AS nvarchar(max)) + N'$$.educationOrganizationId=' + CAST(i.[EducationOrganizationId] AS nvarchar(max))), i.[DocumentId], 1
        FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId];
    END
END;
GO

CREATE OR ALTER TRIGGER [edfi].[TR_StateEducationAgency_Stamp]
ON [edfi].[StateEducationAgency]
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


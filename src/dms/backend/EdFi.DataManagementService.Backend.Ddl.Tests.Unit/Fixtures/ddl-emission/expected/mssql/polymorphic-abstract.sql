CREATE SCHEMA [edfi];

CREATE TABLE [edfi].[School] (
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

CREATE TABLE [edfi].[LocalEducationAgency] (
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    CONSTRAINT [PK_LocalEducationAgency] PRIMARY KEY ([DocumentId])
);

CREATE TABLE [edfi].[EducationOrganizationIdentity] (
    [DocumentId] bigint NOT NULL,
    [EducationOrganizationId] int NOT NULL,
    [Discriminator] nvarchar(50) NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdentity] PRIMARY KEY ([DocumentId])
);

ALTER TABLE [edfi].[School] ADD CONSTRAINT [FK_School_EducationOrganizationIdentity] FOREIGN KEY ([DocumentId]) REFERENCES [edfi].[EducationOrganizationIdentity] ([DocumentId]) ON DELETE CASCADE;

ALTER TABLE [edfi].[LocalEducationAgency] ADD CONSTRAINT [FK_LocalEducationAgency_EducationOrganizationIdentity] FOREIGN KEY ([DocumentId]) REFERENCES [edfi].[EducationOrganizationIdentity] ([DocumentId]) ON DELETE CASCADE;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_Stamp]
ON [edfi].[LocalEducationAgency]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE d
    SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[ContentLastModifiedAt] = sysutcdatetime()
    FROM [dms].[Document] d
    INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId];
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
    MERGE [edfi].[EducationOrganizationIdentity] AS t
    USING inserted AS s ON t.[DocumentId] = s.[DocumentId]
    WHEN MATCHED THEN UPDATE SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
    WHEN NOT MATCHED THEN INSERT ([DocumentId], [EducationOrganizationId], [Discriminator])
    VALUES (s.[DocumentId], s.[EducationOrganizationId], N'Ed-Fi:LocalEducationAgency');
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_ReferentialIdentity]
ON [edfi].[LocalEducationAgency]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
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
END;

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
    MERGE [edfi].[EducationOrganizationIdentity] AS t
    USING inserted AS s ON t.[DocumentId] = s.[DocumentId]
    WHEN MATCHED THEN UPDATE SET t.[EducationOrganizationId] = s.[EducationOrganizationId]
    WHEN NOT MATCHED THEN INSERT ([DocumentId], [EducationOrganizationId], [Discriminator])
    VALUES (s.[DocumentId], s.[EducationOrganizationId], N'Ed-Fi:School');
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_School_ReferentialIdentity]
ON [edfi].[School]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
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
END;

GO
CREATE OR ALTER VIEW [edfi].[EducationOrganization] AS
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(N'School' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[School]
UNION ALL
SELECT [DocumentId] AS [DocumentId], [EducationOrganizationId] AS [EducationOrganizationId], CAST(N'LocalEducationAgency' AS nvarchar(50)) AS [Discriminator]
FROM [edfi].[LocalEducationAgency]
;


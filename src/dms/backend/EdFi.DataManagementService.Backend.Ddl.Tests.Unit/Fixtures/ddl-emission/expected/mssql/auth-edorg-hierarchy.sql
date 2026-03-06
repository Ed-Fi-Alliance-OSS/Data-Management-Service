-- ==========================================================
-- Phase 1: Schemas
-- ==========================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'auth')
    EXEC('CREATE SCHEMA [auth]');

-- ==========================================================
-- Phase 2: Tables
-- ==========================================================

IF OBJECT_ID(N'auth.EducationOrganizationIdToEducationOrganizationId', N'U') IS NULL
CREATE TABLE [auth].[EducationOrganizationIdToEducationOrganizationId]
(
    [SourceEducationOrganizationId] bigint NOT NULL,
    [TargetEducationOrganizationId] bigint NOT NULL,
    CONSTRAINT [PK_EducationOrganizationIdToEducationOrganizationId] PRIMARY KEY CLUSTERED ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
);

-- ==========================================================
-- Phase 3: Indexes
-- ==========================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'auth' AND t.name = N'EducationOrganizationIdToEducationOrganizationId' AND i.name = N'IX_EducationOrganizationIdToEducationOrganizationId_Target'
)
CREATE NONCLUSTERED INDEX [IX_EducationOrganizationIdToEducationOrganizationId_Target] ON [auth].[EducationOrganizationIdToEducationOrganizationId] ([TargetEducationOrganizationId]) INCLUDE ([SourceEducationOrganizationId]);

-- ==========================================================
-- Phase 4: Triggers
-- ==========================================================

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
                SELECT tuples.[SourceEducationOrganizationId], old.[LocalEducationAgencyId]
                FROM deleted old
                    INNER JOIN [edfi].[EducationServiceCenter] AS parent
                        ON parent.[DocumentId] = old.[EducationServiceCenter_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[EducationServiceCenterId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[EducationServiceCenter_DocumentId] IS NOT NULL

                UNION

                SELECT tuples.[SourceEducationOrganizationId], old.[LocalEducationAgencyId]
                FROM deleted old
                    INNER JOIN [edfi].[LocalEducationAgency] AS parent
                        ON parent.[DocumentId] = old.[ParentLocalEducationAgency_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[ParentLocalEducationAgency_DocumentId] IS NOT NULL

                UNION

                SELECT tuples.[SourceEducationOrganizationId], old.[LocalEducationAgencyId]
                FROM deleted old
                    INNER JOIN [edfi].[StateEducationAgency] AS parent
                        ON parent.[DocumentId] = old.[StateEducationAgency_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[StateEducationAgencyId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[StateEducationAgency_DocumentId] IS NOT NULL
            ) AS d1
            CROSS JOIN
            (
                SELECT old.[LocalEducationAgencyId], tuples.[TargetEducationOrganizationId]
                FROM deleted old
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON old.[LocalEducationAgencyId] = tuples.[SourceEducationOrganizationId]
            ) AS d2
            WHERE d1.[LocalEducationAgencyId] = d2.[LocalEducationAgencyId]
        ) AS cj
            ON tbd.[SourceEducationOrganizationId] = cj.[SourceEducationOrganizationId]
            AND tbd.[TargetEducationOrganizationId] = cj.[TargetEducationOrganizationId];

    DELETE tuples
    FROM [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
        INNER JOIN deleted old
            ON tuples.[SourceEducationOrganizationId] = old.[LocalEducationAgencyId]
            AND tuples.[TargetEducationOrganizationId] = old.[LocalEducationAgencyId];
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_LocalEducationAgency_AuthHierarchy_Insert]
ON [edfi].[LocalEducationAgency]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
    SELECT new.[LocalEducationAgencyId], new.[LocalEducationAgencyId]
    FROM inserted new;

    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
    SELECT sources.[SourceEducationOrganizationId], targets.[TargetEducationOrganizationId]
    FROM (
        SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
        FROM inserted new
            INNER JOIN [edfi].[EducationServiceCenter] AS parent
                ON parent.[DocumentId] = new.[EducationServiceCenter_DocumentId]
            INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                ON parent.[EducationServiceCenterId] = tuples.[TargetEducationOrganizationId]
        WHERE new.[EducationServiceCenter_DocumentId] IS NOT NULL

        UNION

        SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
        FROM inserted new
            INNER JOIN [edfi].[LocalEducationAgency] AS parent
                ON parent.[DocumentId] = new.[ParentLocalEducationAgency_DocumentId]
            INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]
        WHERE new.[ParentLocalEducationAgency_DocumentId] IS NOT NULL

        UNION

        SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
        FROM inserted new
            INNER JOIN [edfi].[StateEducationAgency] AS parent
                ON parent.[DocumentId] = new.[StateEducationAgency_DocumentId]
            INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                ON parent.[StateEducationAgencyId] = tuples.[TargetEducationOrganizationId]
        WHERE new.[StateEducationAgency_DocumentId] IS NOT NULL
    ) AS sources
    CROSS JOIN
    (
        SELECT new.[LocalEducationAgencyId], tuples.[TargetEducationOrganizationId]
        FROM inserted new
            INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                ON new.[LocalEducationAgencyId] = tuples.[SourceEducationOrganizationId]
    ) AS targets
    WHERE sources.[LocalEducationAgencyId] = targets.[LocalEducationAgencyId];
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
                SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
                FROM inserted new
                    INNER JOIN deleted old
                        ON old.[LocalEducationAgencyId] = new.[LocalEducationAgencyId]
                    INNER JOIN [edfi].[EducationServiceCenter] AS parent
                        ON parent.[DocumentId] = old.[EducationServiceCenter_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[EducationServiceCenterId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[EducationServiceCenter_DocumentId] IS NOT NULL
                    AND (new.[EducationServiceCenter_DocumentId] IS NULL OR old.[EducationServiceCenter_DocumentId] <> new.[EducationServiceCenter_DocumentId])

                UNION

                SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
                FROM inserted new
                    INNER JOIN deleted old
                        ON old.[LocalEducationAgencyId] = new.[LocalEducationAgencyId]
                    INNER JOIN [edfi].[LocalEducationAgency] AS parent
                        ON parent.[DocumentId] = old.[ParentLocalEducationAgency_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[ParentLocalEducationAgency_DocumentId] IS NOT NULL
                    AND (new.[ParentLocalEducationAgency_DocumentId] IS NULL OR old.[ParentLocalEducationAgency_DocumentId] <> new.[ParentLocalEducationAgency_DocumentId])

                UNION

                SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
                FROM inserted new
                    INNER JOIN deleted old
                        ON old.[LocalEducationAgencyId] = new.[LocalEducationAgencyId]
                    INNER JOIN [edfi].[StateEducationAgency] AS parent
                        ON parent.[DocumentId] = old.[StateEducationAgency_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[StateEducationAgencyId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[StateEducationAgency_DocumentId] IS NOT NULL
                    AND (new.[StateEducationAgency_DocumentId] IS NULL OR old.[StateEducationAgency_DocumentId] <> new.[StateEducationAgency_DocumentId])

                EXCEPT

                SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
                FROM inserted new
                    INNER JOIN [edfi].[EducationServiceCenter] AS parent
                        ON parent.[DocumentId] = new.[EducationServiceCenter_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[EducationServiceCenterId] = tuples.[TargetEducationOrganizationId]

                EXCEPT

                SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
                FROM inserted new
                    INNER JOIN [edfi].[LocalEducationAgency] AS parent
                        ON parent.[DocumentId] = new.[ParentLocalEducationAgency_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]

                EXCEPT

                SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
                FROM inserted new
                    INNER JOIN [edfi].[StateEducationAgency] AS parent
                        ON parent.[DocumentId] = new.[StateEducationAgency_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[StateEducationAgencyId] = tuples.[TargetEducationOrganizationId]
            ) AS d1
            CROSS JOIN
            (
                SELECT new.[LocalEducationAgencyId], tuples.[TargetEducationOrganizationId]
                FROM inserted new
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON new.[LocalEducationAgencyId] = tuples.[SourceEducationOrganizationId]
            ) AS d2
            WHERE d1.[LocalEducationAgencyId] = d2.[LocalEducationAgencyId]
        ) AS cj
            ON tbd.[SourceEducationOrganizationId] = cj.[SourceEducationOrganizationId]
            AND tbd.[TargetEducationOrganizationId] = cj.[TargetEducationOrganizationId];

    MERGE INTO [auth].[EducationOrganizationIdToEducationOrganizationId] target
    USING (
        SELECT sources.[SourceEducationOrganizationId], targets.[TargetEducationOrganizationId]
        FROM (
            SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
            FROM inserted new
                INNER JOIN deleted old
                    ON new.[LocalEducationAgencyId] = old.[LocalEducationAgencyId]
                INNER JOIN [edfi].[EducationServiceCenter] AS parent
                    ON parent.[DocumentId] = new.[EducationServiceCenter_DocumentId]
                INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                    ON parent.[EducationServiceCenterId] = tuples.[TargetEducationOrganizationId]
            WHERE (old.[EducationServiceCenter_DocumentId] IS NULL AND new.[EducationServiceCenter_DocumentId] IS NOT NULL)
                OR old.[EducationServiceCenter_DocumentId] <> new.[EducationServiceCenter_DocumentId]

            UNION

            SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
            FROM inserted new
                INNER JOIN deleted old
                    ON new.[LocalEducationAgencyId] = old.[LocalEducationAgencyId]
                INNER JOIN [edfi].[LocalEducationAgency] AS parent
                    ON parent.[DocumentId] = new.[ParentLocalEducationAgency_DocumentId]
                INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                    ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]
            WHERE (old.[ParentLocalEducationAgency_DocumentId] IS NULL AND new.[ParentLocalEducationAgency_DocumentId] IS NOT NULL)
                OR old.[ParentLocalEducationAgency_DocumentId] <> new.[ParentLocalEducationAgency_DocumentId]

            UNION

            SELECT tuples.[SourceEducationOrganizationId], new.[LocalEducationAgencyId]
            FROM inserted new
                INNER JOIN deleted old
                    ON new.[LocalEducationAgencyId] = old.[LocalEducationAgencyId]
                INNER JOIN [edfi].[StateEducationAgency] AS parent
                    ON parent.[DocumentId] = new.[StateEducationAgency_DocumentId]
                INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                    ON parent.[StateEducationAgencyId] = tuples.[TargetEducationOrganizationId]
            WHERE (old.[StateEducationAgency_DocumentId] IS NULL AND new.[StateEducationAgency_DocumentId] IS NOT NULL)
                OR old.[StateEducationAgency_DocumentId] <> new.[StateEducationAgency_DocumentId]
        ) AS sources
        CROSS JOIN
        (
            SELECT new.[LocalEducationAgencyId], tuples.[TargetEducationOrganizationId]
            FROM inserted new
                INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                    ON new.[LocalEducationAgencyId] = tuples.[SourceEducationOrganizationId]
        ) AS targets
        WHERE sources.[LocalEducationAgencyId] = targets.[LocalEducationAgencyId]
    ) AS source
        ON target.[SourceEducationOrganizationId] = source.[SourceEducationOrganizationId]
        AND target.[TargetEducationOrganizationId] = source.[TargetEducationOrganizationId]
    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
        VALUES (source.[SourceEducationOrganizationId], source.[TargetEducationOrganizationId]);
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_School_AuthHierarchy_Delete]
ON [edfi].[School]
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE tbd
    FROM [auth].[EducationOrganizationIdToEducationOrganizationId] AS tbd
        INNER JOIN (
            SELECT d1.[SourceEducationOrganizationId], d2.[TargetEducationOrganizationId]
            FROM (
                SELECT tuples.[SourceEducationOrganizationId], old.[SchoolId]
                FROM deleted old
                    INNER JOIN [edfi].[LocalEducationAgency] AS parent
                        ON parent.[DocumentId] = old.[LocalEducationAgency_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[LocalEducationAgency_DocumentId] IS NOT NULL
            ) AS d1
            CROSS JOIN
            (
                SELECT old.[SchoolId], tuples.[TargetEducationOrganizationId]
                FROM deleted old
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON old.[SchoolId] = tuples.[SourceEducationOrganizationId]
            ) AS d2
            WHERE d1.[SchoolId] = d2.[SchoolId]
        ) AS cj
            ON tbd.[SourceEducationOrganizationId] = cj.[SourceEducationOrganizationId]
            AND tbd.[TargetEducationOrganizationId] = cj.[TargetEducationOrganizationId];

    DELETE tuples
    FROM [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
        INNER JOIN deleted old
            ON tuples.[SourceEducationOrganizationId] = old.[SchoolId]
            AND tuples.[TargetEducationOrganizationId] = old.[SchoolId];
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_School_AuthHierarchy_Insert]
ON [edfi].[School]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
    SELECT new.[SchoolId], new.[SchoolId]
    FROM inserted new;

    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
    SELECT sources.[SourceEducationOrganizationId], targets.[TargetEducationOrganizationId]
    FROM (
        SELECT tuples.[SourceEducationOrganizationId], new.[SchoolId]
        FROM inserted new
            INNER JOIN [edfi].[LocalEducationAgency] AS parent
                ON parent.[DocumentId] = new.[LocalEducationAgency_DocumentId]
            INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]
        WHERE new.[LocalEducationAgency_DocumentId] IS NOT NULL
    ) AS sources
    CROSS JOIN
    (
        SELECT new.[SchoolId], tuples.[TargetEducationOrganizationId]
        FROM inserted new
            INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                ON new.[SchoolId] = tuples.[SourceEducationOrganizationId]
    ) AS targets
    WHERE sources.[SchoolId] = targets.[SchoolId];
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_School_AuthHierarchy_Update]
ON [edfi].[School]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE tbd
    FROM [auth].[EducationOrganizationIdToEducationOrganizationId] AS tbd
        INNER JOIN (
            SELECT d1.[SourceEducationOrganizationId], d2.[TargetEducationOrganizationId]
            FROM (
                SELECT tuples.[SourceEducationOrganizationId], new.[SchoolId]
                FROM inserted new
                    INNER JOIN deleted old
                        ON old.[SchoolId] = new.[SchoolId]
                    INNER JOIN [edfi].[LocalEducationAgency] AS parent
                        ON parent.[DocumentId] = old.[LocalEducationAgency_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]
                WHERE old.[LocalEducationAgency_DocumentId] IS NOT NULL
                    AND (new.[LocalEducationAgency_DocumentId] IS NULL OR old.[LocalEducationAgency_DocumentId] <> new.[LocalEducationAgency_DocumentId])

                EXCEPT

                SELECT tuples.[SourceEducationOrganizationId], new.[SchoolId]
                FROM inserted new
                    INNER JOIN [edfi].[LocalEducationAgency] AS parent
                        ON parent.[DocumentId] = new.[LocalEducationAgency_DocumentId]
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]
            ) AS d1
            CROSS JOIN
            (
                SELECT new.[SchoolId], tuples.[TargetEducationOrganizationId]
                FROM inserted new
                    INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                        ON new.[SchoolId] = tuples.[SourceEducationOrganizationId]
            ) AS d2
            WHERE d1.[SchoolId] = d2.[SchoolId]
        ) AS cj
            ON tbd.[SourceEducationOrganizationId] = cj.[SourceEducationOrganizationId]
            AND tbd.[TargetEducationOrganizationId] = cj.[TargetEducationOrganizationId];

    MERGE INTO [auth].[EducationOrganizationIdToEducationOrganizationId] target
    USING (
        SELECT sources.[SourceEducationOrganizationId], targets.[TargetEducationOrganizationId]
        FROM (
            SELECT tuples.[SourceEducationOrganizationId], new.[SchoolId]
            FROM inserted new
                INNER JOIN deleted old
                    ON new.[SchoolId] = old.[SchoolId]
                INNER JOIN [edfi].[LocalEducationAgency] AS parent
                    ON parent.[DocumentId] = new.[LocalEducationAgency_DocumentId]
                INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                    ON parent.[LocalEducationAgencyId] = tuples.[TargetEducationOrganizationId]
            WHERE (old.[LocalEducationAgency_DocumentId] IS NULL AND new.[LocalEducationAgency_DocumentId] IS NOT NULL)
                OR old.[LocalEducationAgency_DocumentId] <> new.[LocalEducationAgency_DocumentId]
        ) AS sources
        CROSS JOIN
        (
            SELECT new.[SchoolId], tuples.[TargetEducationOrganizationId]
            FROM inserted new
                INNER JOIN [auth].[EducationOrganizationIdToEducationOrganizationId] AS tuples
                    ON new.[SchoolId] = tuples.[SourceEducationOrganizationId]
        ) AS targets
        WHERE sources.[SchoolId] = targets.[SchoolId]
    ) AS source
        ON target.[SourceEducationOrganizationId] = source.[SourceEducationOrganizationId]
        AND target.[TargetEducationOrganizationId] = source.[TargetEducationOrganizationId]
    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
        VALUES (source.[SourceEducationOrganizationId], source.[TargetEducationOrganizationId]);
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
            ON tuples.[SourceEducationOrganizationId] = old.[StateEducationAgencyId]
            AND tuples.[TargetEducationOrganizationId] = old.[StateEducationAgencyId];
END;

GO
CREATE OR ALTER TRIGGER [edfi].[TR_StateEducationAgency_AuthHierarchy_Insert]
ON [edfi].[StateEducationAgency]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
    SELECT new.[StateEducationAgencyId], new.[StateEducationAgencyId]
    FROM inserted new;
END;


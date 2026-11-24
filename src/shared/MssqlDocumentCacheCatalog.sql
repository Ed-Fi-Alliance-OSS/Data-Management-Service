-- SPDX-License-Identifier: Apache-2.0
-- The marker is selected dynamically so a fresh database or a wrong object type is safe.
-- The DDL renderer supplies its native-only precondition and handles a failed marker read as unverified.

DECLARE @document_cache_object_id int, @physical_marker_object_id int, @boundary_objects bigint;
SELECT
    @document_cache_object_id = MAX(CASE WHEN o.[name] = N'DocumentCache' THEN o.[object_id] END),
    @physical_marker_object_id = MAX(CASE WHEN o.[name] = N'MssqlPhysicalSchema' THEN o.[object_id] END),
    @boundary_objects = COUNT_BIG(*)
FROM sys.schemas s
INNER JOIN sys.objects o ON o.[schema_id] = s.[schema_id]
WHERE s.[name] = N'dms'
    AND o.[name] IN (N'DocumentCache', N'MssqlPhysicalSchema', N'EffectiveSchema', N'DocumentCacheState', N'DocumentProjectionWork');

DECLARE @document_json_type sysname, @document_json_max_length smallint;
SELECT @document_json_type = ty.[name], @document_json_max_length = c.[max_length]
FROM sys.tables t
INNER JOIN sys.schemas s ON s.[schema_id] = t.[schema_id]
INNER JOIN sys.columns c ON c.[object_id] = t.[object_id]
INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
WHERE t.[object_id] = @document_cache_object_id
    AND s.[name] COLLATE Latin1_General_100_BIN2 = N'dms'
    AND t.[name] COLLATE Latin1_General_100_BIN2 = N'DocumentCache'
    AND c.[name] COLLATE Latin1_General_100_BIN2 = N'DocumentJson'
    AND c.[user_type_id] = c.[system_type_id] AND ty.[is_user_defined] = 0;

-- Match the generated predicate without letting formatting normalization change an identifier.
DECLARE @object_check_valid bit = CASE WHEN EXISTS (
    SELECT 1
    FROM sys.check_constraints ck
    INNER JOIN sys.columns c ON c.[object_id] = ck.[parent_object_id]
    WHERE ck.[parent_object_id] = @document_cache_object_id
        AND ck.[name] COLLATE Latin1_General_100_BIN2 = N'CK_DocumentCache_IsJsonObject'
        AND c.[name] COLLATE Latin1_General_100_BIN2 = N'DocumentJson'
        AND c.[is_nullable] = 0
        AND ck.[is_disabled] = 0 AND ck.[is_not_trusted] = 0 AND ck.[is_not_for_replication] = 0
        AND REPLACE(REPLACE(REPLACE(
            TRANSLATE(REPLACE(ck.[definition] COLLATE Latin1_General_100_BIN2, N'[DocumentJson]', N'DocumentJson')
                COLLATE Latin1_General_100_CI_AS, N'()' + NCHAR(9) + NCHAR(10) + NCHAR(13), N'     '),
            N'ISJSON', N'ISJSON'), N'OBJECT', N'OBJECT'), N' ', N'')
            COLLATE Latin1_General_100_BIN2 = N'ISJSONDocumentJson,OBJECT=1'
) THEN 1 ELSE 0 END;

DECLARE @physical_marker_valid bit = 0;
IF /* RequireNativeDocumentCache */ EXISTS (
    SELECT 1
    FROM sys.tables t
    INNER JOIN sys.schemas s ON s.[schema_id] = t.[schema_id]
    INNER JOIN sys.columns c ON c.[object_id] = t.[object_id]
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE t.[object_id] = @physical_marker_object_id
        AND s.[name] COLLATE Latin1_General_100_BIN2 = N'dms'
        AND t.[name] COLLATE Latin1_General_100_BIN2 = N'MssqlPhysicalSchema'
        AND c.[user_type_id] = c.[system_type_id] AND ty.[is_user_defined] = 0
        AND c.[is_nullable] = 0
        AND ((c.[name] COLLATE Latin1_General_100_BIN2 = N'SingletonId' AND ty.[name] = N'smallint')
            OR (c.[name] COLLATE Latin1_General_100_BIN2 = N'MssqlPhysicalSchemaVersion' AND ty.[name] = N'varchar' AND c.[max_length] = 2))
        AND EXISTS (
            SELECT 1
            FROM sys.check_constraints ck
            WHERE ck.[parent_object_id] = t.[object_id]
                AND ck.[name] COLLATE Latin1_General_100_BIN2 = N'CK_MssqlPhysicalSchema_Singleton'
                AND ck.[is_disabled] = 0 AND ck.[is_not_trusted] = 0 AND ck.[is_not_for_replication] = 0
                AND REPLACE(
                    TRANSLATE(REPLACE(ck.[definition] COLLATE Latin1_General_100_BIN2,
                        N'[SingletonId]', N'SingletonId'),
                        N'()' + NCHAR(9) + NCHAR(10) + NCHAR(13), N'     '), N' ', N'')
                    = N'SingletonId=1'
        )
        AND EXISTS (
            SELECT 1
            FROM sys.indexes pk
            INNER JOIN sys.index_columns ic ON ic.[object_id] = pk.[object_id] AND ic.[index_id] = pk.[index_id]
            INNER JOIN sys.columns id ON id.[object_id] = ic.[object_id] AND id.[column_id] = ic.[column_id]
            WHERE pk.[object_id] = t.[object_id] AND pk.[is_primary_key] = 1 AND pk.[is_disabled] = 0
                AND ic.[key_ordinal] > 0
            GROUP BY pk.[index_id]
            HAVING COUNT(*) = 1 AND MAX(CASE WHEN id.[name] COLLATE Latin1_General_100_BIN2
                = N'SingletonId' THEN 1 ELSE 0 END) = 1
        )
    GROUP BY t.[object_id]
    HAVING COUNT(*) = 2
)
BEGIN
    BEGIN TRY
        EXEC sys.sp_executesql N'
            SELECT @valid = CASE WHEN COUNT_BIG(*) = 1 AND MAX(CASE WHEN [SingletonId] = 1
                AND CONVERT(varbinary(2), [MssqlPhysicalSchemaVersion]) = CONVERT(varbinary(2), ''v2'')
                THEN 1 ELSE 0 END) = 1 THEN 1 ELSE 0 END
            FROM [dms].[MssqlPhysicalSchema] WITH (HOLDLOCK);',
            N'@valid bit OUTPUT', @valid = @physical_marker_valid OUTPUT;
    END TRY
    BEGIN CATCH
        THROW; -- PreserveMarkerReadError
    END CATCH;
END;

-- Object-level DENY can hide a boundary object even with broader VIEW DEFINITION.
-- Only the database owner can establish absence from the catalog for this guard.
DECLARE @can_prove_boundary_absence bit =
    CASE WHEN USER_ID() = DATABASE_PRINCIPAL_ID(N'dbo') THEN 1 ELSE 0 END;
DECLARE @document_cache_physical_version varchar(2) = CASE
    WHEN @physical_marker_valid = 1 THEN 'v2'
    WHEN @can_prove_boundary_absence = 1 AND @physical_marker_object_id IS NULL
        AND @document_json_type = N'nvarchar' AND @document_json_max_length = -1 THEN 'v1'
    ELSE NULL END;

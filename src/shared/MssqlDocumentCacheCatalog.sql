-- SPDX-License-Identifier: Apache-2.0
-- The marker is selected dynamically so a fresh database or a wrong object type is safe.
-- Callers declare @require_native_document_cache. Restore callers read the independent facts.

DECLARE @document_cache_object_id int, @physical_marker_object_id int, @boundary_objects bigint;
DECLARE @physical_marker_read_error int;
SELECT
    @document_cache_object_id = MAX(CASE WHEN o.[name] = N'DocumentCache' THEN o.[object_id] END),
    @physical_marker_object_id = MAX(CASE WHEN o.[name] = N'MssqlPhysicalSchema' THEN o.[object_id] END),
    @boundary_objects = COUNT_BIG(*)
FROM sys.schemas s
INNER JOIN sys.objects o ON o.[schema_id] = s.[schema_id]
WHERE s.[name] = N'dms'
    AND o.[name] IN (N'DocumentCache', N'MssqlPhysicalSchema', N'EffectiveSchema', N'DocumentCacheState', N'DocumentProjectionWork');

-- Observe each carrier once, keeping the database's identifier collation for enumeration
-- and requiring exact names on a user table for the authoritative cache column.
DECLARE @document_json_columns TABLE (table_name sysname, column_type nvarchar(256), is_cache_column bit);
INSERT @document_json_columns (table_name, column_type, is_cache_column)
SELECT o.name,
    CASE WHEN t.is_user_defined = 1 OR c.user_type_id <> c.system_type_id THEN N'user-defined:' ELSE N'' END
    + t.name + CASE
        WHEN t.name IN (N'nvarchar', N'varchar', N'varbinary') AND c.max_length = -1 THEN '(max)'
        WHEN t.name IN (N'nvarchar', N'nchar') THEN '(' + CONVERT(nvarchar(11), c.max_length / 2) + ')'
        WHEN t.name IN (N'varchar', N'char', N'varbinary', N'binary') THEN '(' + CONVERT(nvarchar(11), c.max_length) + ')'
        ELSE '' END,
    CONVERT(bit, CASE WHEN o.type = 'U' AND s.name COLLATE Latin1_General_100_BIN2 = N'dms'
        AND o.name COLLATE Latin1_General_100_BIN2 = N'DocumentCache'
        AND c.name COLLATE Latin1_General_100_BIN2 = N'DocumentJson' THEN 1 ELSE 0 END)
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
JOIN sys.objects o ON o.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = N'dms' AND c.name = N'DocumentJson';

DECLARE @document_json_type nvarchar(256);
SELECT @document_json_type = column_type FROM @document_json_columns WHERE is_cache_column = 1;

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
IF (@require_native_document_cache = 0 OR (@document_json_type = N'json' AND @object_check_valid = 1)) AND EXISTS (
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
        SET @physical_marker_read_error = ERROR_NUMBER();
        -- Only missing/changed columns or objects and denied SELECT are failed schema proof.
        -- Preserve provider failures, including deadlocks and lock timeouts, for caller retry.
        IF @require_native_document_cache = 0 OR ERROR_NUMBER() NOT IN (207, 208, 229, 230) THROW;
    END CATCH;
END;

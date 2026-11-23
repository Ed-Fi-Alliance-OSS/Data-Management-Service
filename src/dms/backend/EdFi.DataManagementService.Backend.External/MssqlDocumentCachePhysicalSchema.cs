// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// The SQL Server physical baseline for the DocumentCache JSON column.
/// </summary>
public static class MssqlDocumentCachePhysicalSchema
{
    public const int MinimumServerMajorVersion = 17;
    public const string SchemaName = "dms";
    public const string TableName = "MssqlPhysicalSchema";
    public const string SingletonIdColumn = "SingletonId";
    public const string SingletonCheckName = "CK_MssqlPhysicalSchema_Singleton";
    public const string VersionColumn = "MssqlPhysicalSchemaVersion";
    public const string Version = "v2";
    public const string NativeJsonType = "json";
    public const string ManifestPropertyName = "mssql_physical_schema_version";

    public const int UnsupportedServerError = 51128;
    public const int LegacySchemaError = 51129;
    public const int IncompatibleSchemaError = 51130;

    public const string UnsupportedServerMessage =
        "Native dms.DocumentCache.DocumentJson requires SQL Server 2025 (major version 17) or later. Upgrade the SQL Server engine before provisioning.";
    public const string LegacySchemaMessage =
        "The dms.DocumentCache.DocumentJson column uses the legacy v1 physical schema. Drop and recreate the database before provisioning the v2 DocumentCache schema.";
    public const string IncompatibleSchemaMessage =
        "The SQL Server DocumentCache physical schema is incompatible or could not be verified. Use the database owner to verify an absent boundary and ensure the marker can be read. Drop and recreate an incompatible database before provisioning the v2 DocumentCache schema.";

    public static readonly DbTableName Table = new(DocumentCacheInventoryDefinition.DmsSchema, TableName);

    private const string LegacyVersion = "v1";
    private const string ObjectCheckName = DocumentCacheInventoryDefinition
        .DocumentCacheConstraints
        .MssqlJsonObject;

    public static string PlatformGuardSql { get; } = $$"""
        IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) < {{MinimumServerMajorVersion}}
            THROW {{UnsupportedServerError}}, '{{UnsupportedServerMessage}}', 1;
        """;

    // This batch never names the marker in a static SELECT. A missing marker is expected
    // on a new database, and an existing object of the wrong kind must not be read.
    private static string CatalogObservationSql { get; } = $$"""
        DECLARE @document_cache_object_id int, @physical_marker_object_id int, @boundary_objects bigint;
        SELECT
            @document_cache_object_id = MAX(CASE WHEN o.[name] = N'DocumentCache' THEN o.[object_id] END),
            @physical_marker_object_id = MAX(CASE WHEN o.[name] = N'{{TableName}}' THEN o.[object_id] END),
            @boundary_objects = COUNT_BIG(*)
        FROM sys.schemas s
        INNER JOIN sys.objects o ON o.[schema_id] = s.[schema_id]
        WHERE s.[name] = N'{{SchemaName}}'
            AND o.[name] IN (N'DocumentCache', N'{{TableName}}', N'EffectiveSchema', N'DocumentCacheState', N'DocumentProjectionWork');

        DECLARE @document_json_type sysname, @document_json_max_length smallint;
        SELECT @document_json_type = ty.[name], @document_json_max_length = c.[max_length]
        FROM sys.tables t
        INNER JOIN sys.schemas s ON s.[schema_id] = t.[schema_id]
        INNER JOIN sys.columns c ON c.[object_id] = t.[object_id]
        INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
        WHERE t.[object_id] = @document_cache_object_id
            AND s.[name] COLLATE Latin1_General_100_BIN2 = N'{{SchemaName}}'
            AND t.[name] COLLATE Latin1_General_100_BIN2 = N'DocumentCache'
            AND c.[name] COLLATE Latin1_General_100_BIN2 = N'DocumentJson'
            AND c.[user_type_id] = c.[system_type_id] AND ty.[is_user_defined] = 0;

        -- Match the generated predicate without letting formatting normalization change an identifier.
        DECLARE @object_check_valid bit = CASE WHEN EXISTS (
            SELECT 1
            FROM sys.check_constraints ck
            INNER JOIN sys.columns c ON c.[object_id] = ck.[parent_object_id]
            WHERE ck.[parent_object_id] = @document_cache_object_id
                AND ck.[name] COLLATE Latin1_General_100_BIN2 = N'{{ObjectCheckName}}'
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
        IF @document_json_type = N'{{NativeJsonType}}' AND @object_check_valid = 1 AND EXISTS (
            SELECT 1
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.[schema_id] = t.[schema_id]
            INNER JOIN sys.columns c ON c.[object_id] = t.[object_id]
            INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
            WHERE t.[object_id] = @physical_marker_object_id
                AND s.[name] COLLATE Latin1_General_100_BIN2 = N'{{SchemaName}}'
                AND t.[name] COLLATE Latin1_General_100_BIN2 = N'{{TableName}}'
                AND c.[user_type_id] = c.[system_type_id] AND ty.[is_user_defined] = 0
                AND c.[is_nullable] = 0
                AND ((c.[name] COLLATE Latin1_General_100_BIN2 = N'{{SingletonIdColumn}}' AND ty.[name] = N'smallint')
                    OR (c.[name] COLLATE Latin1_General_100_BIN2 = N'{{VersionColumn}}' AND ty.[name] = N'varchar' AND c.[max_length] = 2))
                AND EXISTS (
                    SELECT 1
                    FROM sys.check_constraints ck
                    WHERE ck.[parent_object_id] = t.[object_id]
                        AND ck.[name] COLLATE Latin1_General_100_BIN2 = N'{{SingletonCheckName}}'
                        AND ck.[is_disabled] = 0 AND ck.[is_not_trusted] = 0 AND ck.[is_not_for_replication] = 0
                        AND REPLACE(
                            TRANSLATE(REPLACE(ck.[definition] COLLATE Latin1_General_100_BIN2,
                                N'[{{SingletonIdColumn}}]', N'{{SingletonIdColumn}}'),
                                N'()' + NCHAR(9) + NCHAR(10) + NCHAR(13), N'     '), N' ', N'')
                            = N'{{SingletonIdColumn}}=1'
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
                        = N'{{SingletonIdColumn}}' THEN 1 ELSE 0 END) = 1
                )
            GROUP BY t.[object_id]
            HAVING COUNT(*) = 2
        )
        BEGIN
            BEGIN TRY
                EXEC sys.sp_executesql N'
                    SELECT @valid = CASE WHEN COUNT_BIG(*) = 1 AND MAX(CASE WHEN [{{SingletonIdColumn}}] = 1
                        AND CONVERT(varbinary(2), [{{VersionColumn}}]) = CONVERT(varbinary(2), ''{{Version}}'')
                        THEN 1 ELSE 0 END) = 1 THEN 1 ELSE 0 END
                    FROM [{{SchemaName}}].[{{TableName}}] WITH (HOLDLOCK);',
                    N'@valid bit OUTPUT', @valid = @physical_marker_valid OUTPUT;
            END TRY
            BEGIN CATCH
                SET @physical_marker_valid = 0;
            END CATCH;
        END;

        -- Object-level DENY can hide a boundary object even with broader VIEW DEFINITION.
        -- Only the database owner can establish absence from the catalog for this guard.
        DECLARE @can_prove_boundary_absence bit =
            CASE WHEN USER_ID() = DATABASE_PRINCIPAL_ID(N'dbo') THEN 1 ELSE 0 END;
        DECLARE @document_cache_physical_version varchar(2) = CASE
            WHEN @physical_marker_valid = 1 THEN '{{Version}}'
            WHEN @can_prove_boundary_absence = 1 AND @physical_marker_object_id IS NULL
                AND @document_json_type = N'nvarchar' AND @document_json_max_length = -1 THEN '{{LegacyVersion}}'
            ELSE NULL END;
        """;

    /// <summary>
    /// Runs before mutations in both raw DDL and the SchemaTools provider. On an admitted
    /// existing schema the marker read is held through the surrounding apply transaction.
    /// </summary>
    public static string ProvisioningGuardSql { get; } = $$"""
        -- Preflight: verify the SQL Server engine and DocumentCache physical baseline
        {{PlatformGuardSql}}

        {{CatalogObservationSql}}

        IF @document_cache_physical_version = '{{LegacyVersion}}'
            THROW {{LegacySchemaError}}, '{{LegacySchemaMessage}}', 1;
        IF @document_cache_physical_version IS NULL AND (@boundary_objects <> 0 OR @can_prove_boundary_absence = 0)
            THROW {{IncompatibleSchemaError}}, '{{IncompatibleSchemaMessage}}', 1;
        """;

    public static string ObservedVersionSql { get; } = $"""
        {CatalogObservationSql}
        SELECT @document_cache_physical_version AS physical_schema_version;
        """;
}

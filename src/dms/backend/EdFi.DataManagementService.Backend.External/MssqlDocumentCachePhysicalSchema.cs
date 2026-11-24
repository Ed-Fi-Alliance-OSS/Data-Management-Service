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

    public static string PlatformGuardSql { get; } = $$"""
        IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) < {{MinimumServerMajorVersion}}
            THROW {{UnsupportedServerError}}, '{{UnsupportedServerMessage}}', 1;
        """;

    private static string CatalogObservationSql { get; } = ReadCatalogObservationSql();

    private static string ReadCatalogObservationSql()
    {
        using var stream = typeof(MssqlDocumentCachePhysicalSchema).Assembly.GetManifestResourceStream(
            "MssqlDocumentCacheCatalog.sql"
        ) ?? throw new InvalidOperationException("Missing SQL Server DocumentCache catalog resource.");
        using var reader = new StreamReader(stream);

        // Preserve the emitted DDL while restore callers receive independent marker and CHECK
        // facts and retain their normal SQL error handling.
        string sql = reader.ReadToEnd().ReplaceLineEndings("\n");
        return sql[(sql.IndexOf("DECLARE", StringComparison.Ordinal))..]
            .TrimEnd('\n')
            .Replace(
                "/* RequireNativeDocumentCache */",
                $"@document_json_type = N'{NativeJsonType}' AND @object_check_valid = 1 AND",
                StringComparison.Ordinal
            )
            .Replace(
                "THROW; -- PreserveMarkerReadError",
                "SET @physical_marker_valid = 0;",
                StringComparison.Ordinal
            );
    }

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

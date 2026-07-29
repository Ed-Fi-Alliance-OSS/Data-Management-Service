// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

/// <summary>
/// Shared helpers for DDL provision integration tests across both PostgreSQL and MSSQL.
/// Accepts DbConnection (common base of NpgsqlConnection and SqlConnection) and a
/// dialect string ("pgsql" or "mssql") for SQL formatting differences.
/// </summary>
internal static partial class ProvisionTestHelper
{
    internal static readonly string[] ExpectedCoreTables =
    [
        "DataStoreIdentity",
        "Document",
        "DocumentCache",
        "DocumentCacheState",
        "DocumentProjectionWork",
        "Descriptor",
        "EffectiveSchema",
        "ReferentialIdentity",
        "ResourceKey",
        "SchemaComponent",
    ];

    internal sealed record DocumentCacheMutableStateSnapshot(
        Guid SourceIdentity,
        string ProjectionLifecycleState,
        bool CacheAheadRecoveryRequired,
        long DocumentCacheRows,
        long DocumentProjectionWorkRows,
        long? RequiredContentVersion,
        DateTime? FirstEnqueuedAt,
        DateTime? LastEnqueuedAt
    );

    internal static (int ExitCode, string Output, string Error) RunProvision(
        string dialect,
        string connectionString,
        string[]? schemaPaths = null,
        bool createDatabase = false
    )
    {
        schemaPaths ??= [CliTestHelper.GetMinimalSchemaPath()];
        List<string> args =
        [
            "ddl",
            "provision",
            "--schema",
            .. schemaPaths,
            "--connection-string",
            connectionString,
            "--dialect",
            dialect,
        ];
        if (createDatabase)
        {
            args.Add("--create-database");
        }
        return CliTestHelper.RunCli([.. args]);
    }

    internal static void AssertCoreTablesExist(DbConnection connection)
    {
        foreach (var table in ExpectedCoreTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = @table;";
            var param = command.CreateParameter();
            param.ParameterName = "table";
            param.Value = table;
            command.Parameters.Add(param);

            var result = command.ExecuteScalar();
            result.Should().NotBeNull($"table dms.{table} should exist");
        }
    }

    internal static void AssertPostgresqlEnqueueFunctionSecurity(NpgsqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM pg_catalog.pg_proc p
                INNER JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'dms'
                AND p.proname IN ('TF_Document_EnqueueProjectionInsert', 'TF_Document_EnqueueProjectionUpdate')
                AND p.prosecdef
                AND p.proowner = 'edfi_dms_enqueue_owner'::pg_catalog.regrole
                AND COALESCE(p.proconfig, ARRAY[]::text[]) @> ARRAY['search_path=pg_catalog']::text[]
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.aclexplode(COALESCE(p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
                    WHERE acl.grantee = 0
                    AND acl.privilege_type = 'EXECUTE'
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.aclexplode(COALESCE(p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
                    WHERE acl.grantee <> p.proowner
                    AND acl.privilege_type = 'EXECUTE'
                )
                """;
            Convert
                .ToInt64(command.ExecuteScalar())
                .Should()
                .Be(
                    2,
                    "both enqueue functions should be SECURITY DEFINER with the locked owner and no non-owner execute"
                );
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    pg_catalog.has_schema_privilege(
                        'edfi_dms_enqueue_owner',
                        'dms',
                        'USAGE'
                    )
                    AND NOT pg_catalog.has_schema_privilege(
                        'edfi_dms_enqueue_owner',
                        'dms',
                        'CREATE'
                    )
                    AND pg_catalog.has_table_privilege(
                        'edfi_dms_enqueue_owner',
                        '"dms"."DocumentCacheState"',
                        'SELECT'
                    )
                    AND pg_catalog.has_table_privilege(
                        'edfi_dms_enqueue_owner',
                        '"dms"."DocumentProjectionWork"',
                        'SELECT, INSERT, UPDATE'
                    )
                    AND NOT EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_class table_info
                        INNER JOIN pg_catalog.pg_namespace namespace_info
                            ON namespace_info.oid = table_info.relnamespace
                        CROSS JOIN LATERAL pg_catalog.aclexplode(
                            COALESCE(table_info.relacl, pg_catalog.acldefault('r', table_info.relowner))
                        ) acl
                        WHERE namespace_info.nspname = 'dms'
                        AND table_info.relname = 'DocumentProjectionWork'
                        AND acl.grantee = 0
                        AND acl.privilege_type IN ('INSERT', 'UPDATE', 'DELETE')
                    )
                """;
            ((bool)command.ExecuteScalar()!)
                .Should()
                .BeTrue(
                    "the enqueue owner should have only the local privileges needed by the definer functions"
                );
        }
    }

    internal static long GetDmsTableCount(DbConnection connection, string dialect, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            dialect == "pgsql"
                ? $"""SELECT COUNT(*) FROM dms."{tableName}";"""
                : $"SELECT COUNT(*) FROM dms.{tableName};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void AssertEffectiveSchemaSeeded(DbConnection connection, string dialect)
    {
        GetDmsTableCount(connection, dialect, "EffectiveSchema")
            .Should()
            .Be(1, "there should be exactly one EffectiveSchema row");

        using var hashCommand = connection.CreateCommand();
        hashCommand.CommandText =
            dialect == "pgsql"
                ? """SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema";"""
                : "SELECT EffectiveSchemaHash FROM dms.EffectiveSchema;";
        var hash = (string)hashCommand.ExecuteScalar()!;
        hash.Should().NotBeNullOrEmpty("the effective schema hash should be non-empty");
    }

    internal static void AssertSchemaComponentsSeeded(DbConnection connection, string dialect)
    {
        GetDmsTableCount(connection, dialect, "SchemaComponent")
            .Should()
            .BeGreaterThan(0, "there should be at least one SchemaComponent row");
    }

    internal static void AssertResourceKeysSeeded(DbConnection connection, string dialect, int minCount)
    {
        GetDmsTableCount(connection, dialect, "ResourceKey")
            .Should()
            .BeGreaterThanOrEqualTo(minCount, $"ResourceKey should have at least {minCount} rows");
    }

    internal static void AssertDocumentCacheStateSeeded(DbConnection connection, string dialect)
    {
        GetDmsTableCount(connection, dialect, "DataStoreIdentity")
            .Should()
            .Be(1, "there should be exactly one DataStoreIdentity row");
        GetDmsTableCount(connection, dialect, "DocumentCacheState")
            .Should()
            .Be(1, "there should be exactly one DocumentCacheState row");

        var snapshot = ReadDocumentCacheMutableStateSnapshot(connection, dialect);
        snapshot.SourceIdentity.Should().NotBe(Guid.Empty);
        snapshot.ProjectionLifecycleState.Should().Be("Disabled");
        snapshot.CacheAheadRecoveryRequired.Should().BeFalse();
    }

    internal static void AssertDocumentCacheLifecycleRejectsInvalidValues(
        DbConnection connection,
        string dialect
    )
    {
        string[] invalidStates = ["disabled", " Disabled", "Disabled ", "", "Unknown"];

        foreach (var invalidState in invalidStates)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                dialect == "pgsql"
                    ? """
                        UPDATE dms."DocumentCacheState"
                        SET "ProjectionLifecycleState" = @state
                        WHERE "StateId" = 1
                        """
                    : """
                        UPDATE [dms].[DocumentCacheState]
                        SET [ProjectionLifecycleState] = @state
                        WHERE [StateId] = 1
                        """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "state";
            parameter.Value = invalidState;
            command.Parameters.Add(parameter);

            Action action = () => command.ExecuteNonQuery();
            action.Should().Throw<DbException>($"'{invalidState}' must fail the lifecycle check constraint");
        }

        ReadDocumentCacheMutableStateSnapshot(connection, dialect)
            .ProjectionLifecycleState.Should()
            .Be("Disabled", "failed lifecycle updates must leave the singleton unchanged");
    }

    internal static void InsertRowsThatMustSurviveRerun(DbConnection connection, string dialect)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            dialect == "pgsql"
                ? """
                    UPDATE dms."DocumentCacheState"
                    SET "ProjectionLifecycleState" = 'Tracking',
                        "CacheAheadRecoveryRequired" = TRUE
                    WHERE "StateId" = 1;

                    WITH selected_resource AS (
                        SELECT "ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion"
                        FROM dms."ResourceKey"
                        ORDER BY "ResourceKeyId"
                        LIMIT 1
                    ),
                    inserted_document AS (
                        INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                        SELECT gen_random_uuid(), "ResourceKeyId"
                        FROM selected_resource
                        RETURNING "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion", "ContentLastModifiedAt"
                    )
                    INSERT INTO dms."DocumentCache" (
                        "DocumentId",
                        "DocumentUuid",
                        "ProjectName",
                        "ResourceName",
                        "ResourceVersion",
                        "ContentVersion",
                        "StreamEtag",
                        "LastModifiedAt",
                        "DocumentJson"
                    )
                    SELECT
                        inserted_document."DocumentId",
                        inserted_document."DocumentUuid",
                        selected_resource."ProjectName",
                        selected_resource."ResourceName",
                        selected_resource."ResourceVersion",
                        inserted_document."ContentVersion",
                        'preserved-etag-' || inserted_document."ContentVersion"::text,
                        inserted_document."ContentLastModifiedAt",
                        '{"id":"preserved"}'::jsonb
                    FROM inserted_document
                    INNER JOIN selected_resource
                        ON selected_resource."ResourceKeyId" = inserted_document."ResourceKeyId";
                    """
                : """
                    UPDATE [dms].[DocumentCacheState]
                    SET [ProjectionLifecycleState] = 'Tracking',
                        [CacheAheadRecoveryRequired] = 1
                    WHERE [StateId] = 1;

                    DECLARE @inserted_document TABLE (
                        [DocumentId] bigint NOT NULL,
                        [DocumentUuid] uniqueidentifier NOT NULL,
                        [ResourceKeyId] smallint NOT NULL,
                        [ContentVersion] bigint NOT NULL,
                        [ContentLastModifiedAt] datetime2(7) NOT NULL
                    );

                    INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
                    OUTPUT
                        inserted.[DocumentId],
                        inserted.[DocumentUuid],
                        inserted.[ResourceKeyId],
                        inserted.[ContentVersion],
                        inserted.[ContentLastModifiedAt]
                    INTO @inserted_document
                    SELECT TOP (1) NEWID(), [ResourceKeyId]
                    FROM [dms].[ResourceKey]
                    ORDER BY [ResourceKeyId];

                    INSERT INTO [dms].[DocumentCache] (
                        [DocumentId],
                        [DocumentUuid],
                        [ProjectName],
                        [ResourceName],
                        [ResourceVersion],
                        [ContentVersion],
                        [StreamEtag],
                        [LastModifiedAt],
                        [DocumentJson]
                    )
                    SELECT
                        inserted_document.[DocumentId],
                        inserted_document.[DocumentUuid],
                        resource_key.[ProjectName],
                        resource_key.[ResourceName],
                        resource_key.[ResourceVersion],
                        inserted_document.[ContentVersion],
                        CONCAT(N'preserved-etag-', CONVERT(varchar(20), inserted_document.[ContentVersion])),
                        inserted_document.[ContentLastModifiedAt],
                        N'{"id":"preserved"}'
                    FROM @inserted_document inserted_document
                    INNER JOIN [dms].[ResourceKey] resource_key
                        ON resource_key.[ResourceKeyId] = inserted_document.[ResourceKeyId];
                    """;
        command.ExecuteNonQuery();
    }

    internal static DocumentCacheMutableStateSnapshot ReadDocumentCacheMutableStateSnapshot(
        DbConnection connection,
        string dialect
    )
    {
        Guid sourceIdentity;
        string lifecycleState;
        bool cacheAheadRecoveryRequired;

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                dialect == "pgsql"
                    ? """
                        SELECT "SourceIdentity"
                        FROM dms."DataStoreIdentity"
                        WHERE "DataStoreIdentitySingletonId" = 1
                        """
                    : """
                        SELECT [SourceIdentity]
                        FROM [dms].[DataStoreIdentity]
                        WHERE [DataStoreIdentitySingletonId] = 1
                        """;
            sourceIdentity = (Guid)command.ExecuteScalar()!;
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                dialect == "pgsql"
                    ? """
                        SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired"
                        FROM dms."DocumentCacheState"
                        WHERE "StateId" = 1
                        """
                    : """
                        SELECT [ProjectionLifecycleState], [CacheAheadRecoveryRequired]
                        FROM [dms].[DocumentCacheState]
                        WHERE [StateId] = 1
                        """;
            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue("DocumentCacheState singleton row should exist");
            lifecycleState = reader.GetString(0);
            cacheAheadRecoveryRequired = reader.GetBoolean(1);
        }

        var documentCacheRows = GetDmsTableCount(connection, dialect, "DocumentCache");
        var documentProjectionWorkRows = GetDmsTableCount(connection, dialect, "DocumentProjectionWork");

        long? requiredContentVersion = null;
        DateTime? firstEnqueuedAt = null;
        DateTime? lastEnqueuedAt = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                dialect == "pgsql"
                    ? """
                        SELECT "RequiredContentVersion", "FirstEnqueuedAt", "LastEnqueuedAt"
                        FROM dms."DocumentProjectionWork"
                        ORDER BY "DocumentId"
                        LIMIT 1
                        """
                    : """
                        SELECT TOP (1) [RequiredContentVersion], [FirstEnqueuedAt], [LastEnqueuedAt]
                        FROM [dms].[DocumentProjectionWork]
                        ORDER BY [DocumentId]
                        """;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                requiredContentVersion = reader.GetInt64(0);
                firstEnqueuedAt = reader.GetDateTime(1);
                lastEnqueuedAt = reader.GetDateTime(2);
            }
        }

        return new DocumentCacheMutableStateSnapshot(
            sourceIdentity,
            lifecycleState,
            cacheAheadRecoveryRequired,
            documentCacheRows,
            documentProjectionWorkRows,
            requiredContentVersion,
            firstEnqueuedAt,
            lastEnqueuedAt
        );
    }

    internal static string? ExtractHashFromOutput(string output)
    {
        var match = EffectiveSchemaHashRegex().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Effective schema hash:\s*([0-9a-fA-F]+)")]
    private static partial Regex EffectiveSchemaHashRegex();

    internal static List<string> DiscoverProvisionedSchemasPgsql(string connectionString)
    {
        var systemSchemas = new HashSet<string>(StringComparer.Ordinal)
        {
            "pg_catalog",
            "information_schema",
            "pg_toast",
            "public",
        };

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT nspname FROM pg_catalog.pg_namespace ORDER BY nspname";

        using var reader = command.ExecuteReader();
        var schemas = new List<string>();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!systemSchemas.Contains(name) && !name.StartsWith("pg_", StringComparison.Ordinal))
            {
                schemas.Add(name);
            }
        }
        return schemas;
    }

    internal static List<string> DiscoverProvisionedSchemasMssql(string connectionString)
    {
        var systemSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sys",
            "INFORMATION_SCHEMA",
            "guest",
        };

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.schemas ORDER BY name";

        using var reader = command.ExecuteReader();
        var schemas = new List<string>();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!systemSchemas.Contains(name) && !name.StartsWith("db_", StringComparison.OrdinalIgnoreCase))
            {
                schemas.Add(name);
            }
        }
        return schemas;
    }

    internal static (int ExitCode, string Output, string Error) RunEmit(
        string dialect,
        string outputDir,
        string[]? schemaPaths = null
    )
    {
        schemaPaths ??= CliTestHelper.GetAuthoritativeSchemaPaths();
        List<string> args =
        [
            "ddl",
            "emit",
            "--schema",
            .. schemaPaths,
            "--output",
            outputDir,
            "--dialect",
            dialect,
        ];
        return CliTestHelper.RunCli([.. args]);
    }

    internal static (int ExitCode, string Output, string Error) RunPsql(
        string connectionString,
        string sqlFilePath
    )
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        var args = new List<string>
        {
            "-h",
            builder.Host!,
            "-p",
            builder.Port.ToString(),
            "-U",
            builder.Username!,
            "-d",
            builder.Database!,
            "-v",
            "ON_ERROR_STOP=1",
            "-f",
            sqlFilePath,
        };

        var env = new Dictionary<string, string> { ["PGPASSWORD"] = builder.Password! };

        return CliTestHelper.RunProcess("psql", args, env);
    }

    internal static (int ExitCode, string Output, string Error) RunSqlcmd(
        string connectionString,
        string sqlFilePath
    )
    {
        return CliTestHelper.RunProcess("sqlcmd", BuildSqlcmdArguments(connectionString, sqlFilePath));
    }

    internal static string[] BuildSqlcmdArguments(string connectionString, string sqlFilePath)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);

        List<string> args =
        [
            "-S",
            builder.DataSource,
            "-d",
            builder.InitialCatalog,
            "-b",
            "-I",
            "-r",
            "1",
            "-i",
            sqlFilePath,
        ];

        if (builder.IntegratedSecurity)
        {
            args.Add("-E");
        }
        else
        {
            args.Add("-U");
            args.Add(builder.UserID);
            args.Add("-P");
            args.Add(builder.Password);
        }

        if (builder.TrustServerCertificate)
        {
            args.Add("-C");
        }

        return [.. args];
    }
}

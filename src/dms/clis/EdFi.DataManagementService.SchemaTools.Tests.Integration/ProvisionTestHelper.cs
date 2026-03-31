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
        "Document",
        "ResourceKey",
        "Descriptor",
        "ReferentialIdentity",
        "EffectiveSchema",
        "SchemaComponent",
        "DocumentCache",
        "DocumentChangeEvent",
    ];

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

    internal static string? ExtractHashFromOutput(string output)
    {
        var match = EffectiveSchemaHashRegex().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Effective schema hash:\s*([0-9a-fA-F]+)")]
    private static partial Regex EffectiveSchemaHashRegex();

    private static string Quote(string dialect, string identifier) =>
        dialect == "pgsql" ? $"\"{identifier}\"" : identifier;

    private static string QualifyTable(string dialect, string schema, string table) =>
        $"{Quote(dialect, schema)}.{Quote(dialect, table)}";

    internal static void AssertJournalRowOnInsert(DbConnection connection, string dialect)
    {
        using var getResourceKeyCommand = connection.CreateCommand();
        getResourceKeyCommand.CommandText =
            $"SELECT MIN({Quote(dialect, "ResourceKeyId")}) FROM {QualifyTable(dialect, "dms", "ResourceKey")};";
        var resourceKeyId = Convert.ToInt16(getResourceKeyCommand.ExecuteScalar());

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            dialect == "pgsql"
                ? """
                    INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                    VALUES (@uuid, @resourceKeyId)
                    RETURNING "DocumentId";
                    """
                : """
                    INSERT INTO dms.Document (DocumentUuid, ResourceKeyId)
                    VALUES (@uuid, @resourceKeyId);
                    SELECT SCOPE_IDENTITY();
                    """;
        var uuidParam = insertCommand.CreateParameter();
        uuidParam.ParameterName = "uuid";
        uuidParam.Value = Guid.NewGuid();
        insertCommand.Parameters.Add(uuidParam);
        var resourceKeyParam = insertCommand.CreateParameter();
        resourceKeyParam.ParameterName = "resourceKeyId";
        resourceKeyParam.Value = resourceKeyId;
        insertCommand.Parameters.Add(resourceKeyParam);
        var documentId = Convert.ToInt64(insertCommand.ExecuteScalar());

        using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = $"""
            SELECT {Quote(dialect, "ChangeVersion")}, {Quote(dialect, "DocumentId")}, {Quote(
                dialect,
                "ResourceKeyId"
            )}
            FROM {QualifyTable(dialect, "dms", "DocumentChangeEvent")}
            WHERE {Quote(dialect, "DocumentId")} = @documentId;
            """;
        var docParam = queryCommand.CreateParameter();
        docParam.ParameterName = "documentId";
        docParam.Value = documentId;
        queryCommand.Parameters.Add(docParam);

        using var reader = queryCommand.ExecuteReader();
        reader.Read().Should().BeTrue("a journal row should exist for the inserted document");
        reader.GetInt16(reader.GetOrdinal("ResourceKeyId")).Should().Be(resourceKeyId);
        reader.Read().Should().BeFalse("there should be exactly one journal row");
    }

    internal static void AssertDistinctChangeVersionsOnMultiRowUpdate(DbConnection connection, string dialect)
    {
        using var resourceKeyCommand = connection.CreateCommand();
        resourceKeyCommand.CommandText =
            $"SELECT MIN({Quote(dialect, "ResourceKeyId")}) FROM {QualifyTable(dialect, "dms", "ResourceKey")};";
        var resourceKeyId = Convert.ToInt16(resourceKeyCommand.ExecuteScalar());

        var documentIds = new long[3];
        for (int i = 0; i < 3; i++)
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText =
                dialect == "pgsql"
                    ? """
                        INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                        VALUES (@uuid, @resourceKeyId)
                        RETURNING "DocumentId";
                        """
                    : """
                        INSERT INTO dms.Document (DocumentUuid, ResourceKeyId)
                        VALUES (@uuid, @resourceKeyId);
                        SELECT SCOPE_IDENTITY();
                        """;
            var uuidParam = insertCommand.CreateParameter();
            uuidParam.ParameterName = "uuid";
            uuidParam.Value = Guid.NewGuid();
            insertCommand.Parameters.Add(uuidParam);
            var resourceKeyParam = insertCommand.CreateParameter();
            resourceKeyParam.ParameterName = "resourceKeyId";
            resourceKeyParam.Value = resourceKeyId;
            insertCommand.Parameters.Add(resourceKeyParam);
            documentIds[i] = Convert.ToInt64(insertCommand.ExecuteScalar());
        }

        using var deleteCommand = connection.CreateCommand();
        deleteCommand.CommandText = $"""
            DELETE FROM {QualifyTable(dialect, "dms", "DocumentChangeEvent")}
            WHERE {Quote(dialect, "DocumentId")} IN (@id0, @id1, @id2);
            """;
        AddDocumentIdParams(deleteCommand, documentIds);
        deleteCommand.ExecuteNonQuery();

        using var updateCommand = connection.CreateCommand();
        var seqExpr =
            dialect == "pgsql"
                ? """nextval('"dms"."ChangeVersionSequence"')"""
                : "NEXT VALUE FOR dms.ChangeVersionSequence";
        updateCommand.CommandText = $"""
            UPDATE {QualifyTable(dialect, "dms", "Document")}
            SET {Quote(dialect, "ContentVersion")} = {seqExpr}
            WHERE {Quote(dialect, "DocumentId")} IN (@id0, @id1, @id2);
            """;
        AddDocumentIdParams(updateCommand, documentIds);
        updateCommand.ExecuteNonQuery();

        using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = $"""
            SELECT {Quote(dialect, "ChangeVersion")}, {Quote(dialect, "DocumentId")}
            FROM {QualifyTable(dialect, "dms", "DocumentChangeEvent")}
            WHERE {Quote(dialect, "DocumentId")} IN (@id0, @id1, @id2);
            """;
        AddDocumentIdParams(queryCommand, documentIds);

        var changeVersions = new List<long>();
        var journalDocIds = new List<long>();
        using var reader = queryCommand.ExecuteReader();
        while (reader.Read())
        {
            changeVersions.Add(reader.GetInt64(reader.GetOrdinal("ChangeVersion")));
            journalDocIds.Add(reader.GetInt64(reader.GetOrdinal("DocumentId")));
        }

        changeVersions.Should().HaveCount(3, "one journal row per updated document");
        changeVersions.Distinct().Should().HaveCount(3, "each ChangeVersion must be distinct");
        journalDocIds.Distinct().Should().HaveCount(3, "each DocumentId must be distinct");
    }

    private static void AddDocumentIdParams(DbCommand command, long[] documentIds)
    {
        for (int i = 0; i < documentIds.Length; i++)
        {
            var param = command.CreateParameter();
            param.ParameterName = $"id{i}";
            param.Value = documentIds[i];
            command.Parameters.Add(param);
        }
    }

    internal static List<string> DiscoverProvisionedSchemasPgsql(string connectionString)
    {
        var systemSchemas = new HashSet<string>(StringComparer.Ordinal)
        {
            "pg_catalog",
            "information_schema",
            "pg_toast",
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
        var builder = new SqlConnectionStringBuilder(connectionString);

        var args = new List<string>
        {
            "-S",
            builder.DataSource,
            "-U",
            builder.UserID,
            "-P",
            builder.Password,
            "-d",
            builder.InitialCatalog,
            "-b",
            "-I",
            "-i",
            sqlFilePath,
        };

        return CliTestHelper.RunProcess("sqlcmd", args);
    }
}

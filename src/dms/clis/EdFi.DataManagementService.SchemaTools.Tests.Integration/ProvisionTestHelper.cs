// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;

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
        string? fixturePath = null,
        bool createDatabase = false
    )
    {
        fixturePath ??= CliTestHelper.GetMinimalFixturePath();
        List<string> args =
        [
            "ddl",
            "provision",
            "--schema",
            fixturePath,
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
}

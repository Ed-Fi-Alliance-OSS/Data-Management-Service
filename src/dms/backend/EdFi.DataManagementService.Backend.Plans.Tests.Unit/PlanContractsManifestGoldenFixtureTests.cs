// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_A_PlanContracts_Manifest_GoldenFixture
{
    private string _diffOutput = null!;

    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj"
        );
        var expectedPath = Path.Combine(
            projectRoot,
            "Fixtures",
            "plan-contracts-manifest",
            "minimal",
            "expected",
            "plan-contracts.manifest.json"
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "plan-contracts-manifest",
            "minimal",
            "actual",
            "plan-contracts.manifest.json"
        );

        var manifest = PlanContractsManifestFixtureBuilder.Build();

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue($"plan contracts manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate.");

        _diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);
    }

    [Test]
    public void It_should_match_the_expected_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }
}

internal static class PlanContractsManifestFixtureBuilder
{
    private const string StoryPath =
        "reference/design/backend-redesign/epics/15-plan-compilation/02-plan-contracts-and-deterministic-bindings.md";

    public static string Build()
    {
        var queryPlanSnapshots = BuildQueryPlanSnapshots();

        IReadOnlyList<DbColumnName> columnsInOrder =
        [
            new DbColumnName("SchoolId"),
            new DbColumnName("schoolId"),
            new DbColumnName("School-ID"),
            new DbColumnName("School__ID"),
            new DbColumnName("1School Name"),
        ];

        var parameterNamesInOrder = PlanNamingConventions.DeriveWriteParameterNamesInOrder(columnsInOrder);
        var mssqlBatching = PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(
            SqlDialect.Mssql,
            parametersPerRow: 3
        );
        var pgsqlBatching = PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(
            SqlDialect.Pgsql,
            parametersPerRow: 100
        );

        return PlanContractsManifestJsonEmitter.Emit(
            storyPath: StoryPath,
            queryPlanSnapshots: queryPlanSnapshots,
            columnsInOrder: columnsInOrder,
            parameterNamesInOrder: parameterNamesInOrder,
            mssqlBatching: mssqlBatching,
            pgsqlBatching: pgsqlBatching
        );
    }

    private static IReadOnlyList<QueryPlanSnapshot> BuildQueryPlanSnapshots()
    {
        return [BuildQueryPlanSnapshot(SqlDialect.Pgsql), BuildQueryPlanSnapshot(SqlDialect.Mssql)];
    }

    private static QueryPlanSnapshot BuildQueryPlanSnapshot(SqlDialect dialect)
    {
        var plan = PlanSqlGoldenFixtureQueryPlans.CompileContractsManifestPageDocumentIdPlan(dialect);

        return new QueryPlanSnapshot(
            dialect,
            plan.PageDocumentIdSql,
            plan.TotalCountSql,
            plan.ParametersInOrder
        );
    }
}

internal static class PlanContractsManifestJsonEmitter
{
    // Explicit \n keeps fixture output stable across platforms.
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true, NewLine = "\n" };

    public static string Emit(
        string storyPath,
        IReadOnlyList<QueryPlanSnapshot> queryPlanSnapshots,
        IReadOnlyList<DbColumnName> columnsInOrder,
        IReadOnlyList<string> parameterNamesInOrder,
        BulkInsertBatchingInfo mssqlBatching,
        BulkInsertBatchingInfo pgsqlBatching
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storyPath);
        ArgumentNullException.ThrowIfNull(queryPlanSnapshots);
        ArgumentNullException.ThrowIfNull(columnsInOrder);
        ArgumentNullException.ThrowIfNull(parameterNamesInOrder);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            writer.WriteStartObject();

            writer.WriteString("story", storyPath);
            writer.WritePropertyName("query_plans");
            writer.WriteStartArray();
            foreach (var queryPlanSnapshot in queryPlanSnapshots)
            {
                WriteQueryPlanSnapshot(writer, queryPlanSnapshot);
            }
            writer.WriteEndArray();

            writer.WritePropertyName("write_parameter_naming");
            writer.WriteStartObject();
            writer.WritePropertyName("columns_in_order");
            writer.WriteStartArray();
            foreach (var column in columnsInOrder)
            {
                writer.WriteStringValue(column.Value);
            }
            writer.WriteEndArray();
            writer.WritePropertyName("parameter_names_in_order");
            writer.WriteStartArray();
            foreach (var parameterName in parameterNamesInOrder)
            {
                writer.WriteStringValue(parameterName);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("batching");
            writer.WriteStartArray();
            WriteBatchingInfo(writer, SqlDialect.Mssql, mssqlBatching);
            WriteBatchingInfo(writer, SqlDialect.Pgsql, pgsqlBatching);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void WriteQueryPlanSnapshot(Utf8JsonWriter writer, QueryPlanSnapshot queryPlanSnapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("dialect", ToManifestDialect(queryPlanSnapshot.Dialect));
        writer.WriteString(
            "page_document_id_sql",
            NormalizeMultilineText(queryPlanSnapshot.PageDocumentIdSql)
        );
        writer.WriteString(
            "page_document_id_sql_sha256",
            ComputeNormalizedSha256(queryPlanSnapshot.PageDocumentIdSql)
        );

        if (queryPlanSnapshot.TotalCountSql is null)
        {
            writer.WriteNull("total_count_sql");
            writer.WriteNull("total_count_sql_sha256");
        }
        else
        {
            writer.WriteString("total_count_sql", NormalizeMultilineText(queryPlanSnapshot.TotalCountSql));
            writer.WriteString(
                "total_count_sql_sha256",
                ComputeNormalizedSha256(queryPlanSnapshot.TotalCountSql)
            );
        }

        writer.WritePropertyName("parameters_in_order");
        writer.WriteStartArray();
        foreach (var parameter in queryPlanSnapshot.ParametersInOrder)
        {
            writer.WriteStartObject();
            writer.WriteString("role", ToManifestParameterRole(parameter.Role));
            writer.WriteString("parameter_name", parameter.ParameterName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteBatchingInfo(
        Utf8JsonWriter writer,
        SqlDialect dialect,
        BulkInsertBatchingInfo batchingInfo
    )
    {
        writer.WriteStartObject();
        writer.WriteString("dialect", ToManifestDialect(dialect));
        writer.WriteNumber("max_rows_per_batch", batchingInfo.MaxRowsPerBatch);
        writer.WriteNumber("parameters_per_row", batchingInfo.ParametersPerRow);
        writer.WriteNumber("max_parameters_per_command", batchingInfo.MaxParametersPerCommand);
        writer.WriteEndObject();
    }

    private static string ToManifestDialect(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Mssql => "mssql",
            SqlDialect.Pgsql => "pgsql",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string ToManifestParameterRole(QuerySqlParameterRole role)
    {
        return role switch
        {
            QuerySqlParameterRole.Filter => "filter",
            QuerySqlParameterRole.Offset => "offset",
            QuerySqlParameterRole.Limit => "limit",
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Unsupported query parameter role."
            ),
        };
    }

    private static string ComputeNormalizedSha256(string text)
    {
        var normalizedBytes = Encoding.UTF8.GetBytes(NormalizeMultilineText(text));

        return Convert.ToHexStringLower(SHA256.HashData(normalizedBytes));
    }

    private static string NormalizeMultilineText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.ReplaceLineEndings("\n").TrimEnd();
    }
}

internal sealed record QueryPlanSnapshot(
    SqlDialect Dialect,
    string PageDocumentIdSql,
    string? TotalCountSql,
    IReadOnlyList<QuerySqlParameter> ParametersInOrder
);

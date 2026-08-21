// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

internal static class PgsqlCaptureSamples
{
    // The generated hydration batch's shape: temp-table setup, the parameterized
    // page-selection INSERT, then hydration SELECTs.
    public const string HydrationBatchSql = """
        DROP TABLE IF EXISTS "page";
        CREATE TEMP TABLE "page" ("DocumentId" bigint PRIMARY KEY) ON COMMIT DROP;

        WITH page_ids AS (
        SELECT r."DocumentId"
        FROM "edfi"."Student" r
        ORDER BY r."DocumentId" ASC
        LIMIT @limit OFFSET @offset
        )
        INSERT INTO "page" ("DocumentId")
        SELECT "DocumentId" FROM page_ids;

        SELECT d."DocumentId"
        FROM "dms"."Document" d
        INNER JOIN "page" k ON d."DocumentId" = k."DocumentId"
        ORDER BY d."DocumentId";
        """;

    public static string ExplainJson(long buffersHit, long buffersRead, double executionMs) =>
        $$"""
            [
              {
                "Plan": {
                  "Node Type": "Limit",
                  "Shared Hit Blocks": {{buffersHit}},
                  "Shared Read Blocks": {{buffersRead}},
                  "Plans": [
                    {
                      "Node Type": "Index Only Scan",
                      "Shared Hit Blocks": {{buffersHit}},
                      "Shared Read Blocks": {{buffersRead}}
                    }
                  ]
                },
                "Planning Time": 0.5,
                "Execution Time": {{executionMs}}
              }
            ]
            """;
}

[TestFixture]
public class Given_The_Hydration_Batch_Splitter
{
    private IReadOnlyList<PgsqlBatchStatement> _statements = null!;

    [SetUp]
    public void Setup()
    {
        _statements = PgsqlPlanCapture.SplitHydrationBatch(PgsqlCaptureSamples.HydrationBatchSql);
    }

    [Test]
    public void It_splits_the_batch_into_numbered_statements()
    {
        _statements.Should().HaveCount(4);
        _statements.Select(statement => statement.StatementNumber).Should().Equal(1, 2, 3, 4);
    }

    [Test]
    public void It_classifies_temp_table_ddl_as_setup_and_dml_selects_as_explained()
    {
        _statements
            .Select(statement => statement.Kind)
            .Should()
            .Equal(
                PgsqlStatementKind.Setup,
                PgsqlStatementKind.Setup,
                PgsqlStatementKind.Explained,
                PgsqlStatementKind.Explained
            );
    }

    [Test]
    public void It_keeps_each_statement_text_without_the_terminator()
    {
        _statements[0].Sql.Should().Be("DROP TABLE IF EXISTS \"page\"");
        _statements[2].Sql.Should().StartWith("WITH page_ids AS (");
        _statements[2].Sql.Should().Contain("LIMIT @limit OFFSET @offset");
        _statements.Should().OnlyContain(statement => !statement.Sql.Contains(';'));
    }

    [Test]
    public void It_keeps_a_semicolon_inside_a_quoted_identifier()
    {
        IReadOnlyList<PgsqlBatchStatement> statements = PgsqlPlanCapture.SplitHydrationBatch(
            "SELECT \"a;b\" FROM \"t\";"
        );
        statements.Should().HaveCount(1);
        statements[0].Sql.Should().Be("SELECT \"a;b\" FROM \"t\"");
    }
}

[TestFixture]
public class Given_Unsupported_Hydration_Batch_Constructs
{
    [TestCase("SELECT 'literal';", "*string literal*")]
    [TestCase("SELECT $$x$$;", "*dollar quoting*")]
    [TestCase("SELECT 1 -- trailing;", "*line comment*")]
    [TestCase("SELECT 1 /* block */;", "*block comment*")]
    [TestCase("TRUNCATE \"page\"; SELECT 1;", "*starting with 'TRUNCATE'*")]
    [TestCase("SELECT \"unterminated;", "*unterminated quoted identifier*")]
    [TestCase("DROP INDEX \"i\";", "*a DROP statement other than DROP TABLE*")]
    [TestCase("CREATE INDEX \"i\" ON \"t\" (\"c\");", "*a CREATE statement other than CREATE TEMP TABLE*")]
    [TestCase(
        "CREATE TEMP TABLE \"x\" AS SELECT \"a\" FROM \"y\";",
        "*a CREATE TEMP TABLE statement containing SELECT*"
    )]
    public void It_refuses_to_split_or_classify(string batchSql, string expectedMessage)
    {
        FluentActions
            .Invoking(() => PgsqlPlanCapture.SplitHydrationBatch(batchSql))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage(expectedMessage);
    }

    [Test]
    public void It_rejects_a_batch_with_no_explainable_statement()
    {
        FluentActions
            .Invoking(() => PgsqlPlanCapture.SplitHydrationBatch("DROP TABLE IF EXISTS \"page\";"))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*no explainable DML/SELECT statement*");
    }
}

[TestFixture]
public class Given_Assembled_Batch_Replay_Evidence
{
    private IReadOnlyList<PgsqlBatchStatement> _statements = null!;
    private PgsqlPlanCaptureResult _result = null!;

    [SetUp]
    public void Setup()
    {
        _statements = PgsqlPlanCapture.SplitHydrationBatch(PgsqlCaptureSamples.HydrationBatchSql);
        _result = PgsqlPlanCapture.AssembleResult(
            _statements,
            [
                new PgsqlExplainedStatement(_statements[2], PgsqlCaptureSamples.ExplainJson(1200, 34, 6.25)),
                new PgsqlExplainedStatement(_statements[3], PgsqlCaptureSamples.ExplainJson(800, 16, 3.5)),
            ]
        );
    }

    [Test]
    public void It_aggregates_metrics_across_the_explained_statements()
    {
        _result.Metrics.BuffersHit.Should().Be(2000);
        _result.Metrics.BuffersRead.Should().Be(50);
        _result.Metrics.DbExecutionMs.Should().Be(9.75);
    }

    [Test]
    public void It_leaves_sql_server_metrics_null()
    {
        _result.Metrics.LogicalReads.Should().BeNull();
        _result.Metrics.PhysicalReads.Should().BeNull();
        _result.Metrics.DbCpuMs.Should().BeNull();
        _result.Metrics.DbElapsedMs.Should().BeNull();
    }

    [Test]
    public void It_lists_every_batch_statement_in_the_plan_artifact()
    {
        JsonNode artifact = JsonNode.Parse(_result.PlanArtifactJson)!;
        JsonArray statements = artifact["statements"]!.AsArray();
        statements.Should().HaveCount(4);
        statements
            .Select(entry => entry!["kind"]!.GetValue<string>())
            .Should()
            .Equal("setup", "setup", "explained", "explained");
        statements[0]!["explain"].Should().BeNull();
        statements[2]!["explain"]![0]!["Execution Time"]!.GetValue<double>().Should().Be(6.25);
        statements[3]!["explain"]![0]!["Plan"]!["Shared Hit Blocks"]!.GetValue<long>().Should().Be(800);
    }

    [Test]
    public void It_writes_the_plan_artifact_with_lf_only_newlines()
    {
        _result.PlanArtifactJson.Should().NotContain("\r");
    }

    [Test]
    public void It_rejects_missing_explain_evidence()
    {
        FluentActions
            .Invoking(() =>
                PgsqlPlanCapture.AssembleResult(
                    _statements,
                    [
                        new PgsqlExplainedStatement(
                            _statements[2],
                            PgsqlCaptureSamples.ExplainJson(1200, 34, 6.25)
                        ),
                    ]
                )
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*Expected EXPLAIN evidence for all 2*got 1*");
    }
}

[TestFixture]
public class Given_A_Canned_Explain_Document
{
    private PerfDatabaseMetrics _metrics = null!;

    [SetUp]
    public void Setup()
    {
        _metrics = PgsqlPlanCapture.ParseMetrics(PgsqlCaptureSamples.ExplainJson(1200, 34, 6.25));
    }

    [Test]
    public void It_parses_the_cumulative_root_buffer_counters()
    {
        _metrics.BuffersHit.Should().Be(1200);
        _metrics.BuffersRead.Should().Be(34);
    }

    [Test]
    public void It_parses_the_execution_time()
    {
        _metrics.DbExecutionMs.Should().Be(6.25);
    }

    [Test]
    public void It_leaves_sql_server_metrics_null()
    {
        _metrics.LogicalReads.Should().BeNull();
        _metrics.PhysicalReads.Should().BeNull();
        _metrics.DbCpuMs.Should().BeNull();
        _metrics.DbElapsedMs.Should().BeNull();
    }
}

[TestFixture]
public class Given_Malformed_Explain_Documents
{
    [Test]
    public void It_rejects_a_non_array_document()
    {
        FluentActions
            .Invoking(() => PgsqlPlanCapture.ParseMetrics("""{"Plan": {}}"""))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*non-empty array*");
    }

    [Test]
    public void It_rejects_a_missing_plan_node()
    {
        FluentActions
            .Invoking(() => PgsqlPlanCapture.ParseMetrics("""[{"Execution Time": 1.0}]"""))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*no Plan node*");
    }

    [Test]
    public void It_rejects_a_missing_execution_time()
    {
        FluentActions
            .Invoking(() =>
                PgsqlPlanCapture.ParseMetrics(
                    """[{"Plan": {"Shared Hit Blocks": 1, "Shared Read Blocks": 0}}]"""
                )
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*Execution Time*");
    }

    [Test]
    public void It_rejects_missing_buffer_counters()
    {
        FluentActions
            .Invoking(() => PgsqlPlanCapture.ParseMetrics("""[{"Plan": {}, "Execution Time": 1.0}]"""))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*Shared Hit Blocks*");
    }
}

[TestFixture]
public class Given_The_Explain_Statement_Builder
{
    [Test]
    public void It_prefixes_the_statement_sql()
    {
        PgsqlPlanCapture
            .ExplainSql("SELECT 1")
            .Should()
            .Be("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\nSELECT 1");
    }
}

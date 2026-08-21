// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_A_Canned_Explain_Document
{
    private const string ExplainJson = """
        [
          {
            "Plan": {
              "Node Type": "Limit",
              "Shared Hit Blocks": 1200,
              "Shared Read Blocks": 34,
              "Plans": [
                {
                  "Node Type": "Index Only Scan",
                  "Shared Hit Blocks": 1200,
                  "Shared Read Blocks": 34
                }
              ]
            },
            "Planning Time": 0.5,
            "Execution Time": 6.25
          }
        ]
        """;

    private PerfDatabaseMetrics _metrics = null!;

    [SetUp]
    public void Setup()
    {
        _metrics = PgsqlPlanCapture.ParseMetrics(ExplainJson);
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
    public void It_prefixes_the_page_selection_sql()
    {
        PgsqlPlanCapture
            .ExplainSql("SELECT 1;")
            .Should()
            .Be("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\nSELECT 1;");
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_A_Canned_Statistics_Message_Text
{
    // Shaped like real SET STATISTICS IO, TIME output: a parse-and-compile times block first,
    // per-table IO lines including lob and read-ahead counters, then the execution block.
    private const string StatisticsText = """
        SQL Server parse and compile time:
           CPU time = 2 ms, elapsed time = 3 ms.
        Table 'Student'. Scan count 1, logical reads 42, physical reads 1, page server reads 0, read-ahead reads 5, page server read-ahead reads 0, lob logical reads 9, lob physical reads 9, lob read-ahead reads 0, lob page server read-ahead reads 0.
        Table 'Document'. Scan count 1, logical reads 2058, physical reads 11, page server reads 0, read-ahead reads 0, page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0, lob page server read-ahead reads 0.

         SQL Server Execution Times:
           CPU time = 5 ms,  elapsed time = 7 ms.
        """;

    private PerfDatabaseMetrics _metrics = null!;

    [SetUp]
    public void Setup()
    {
        _metrics = MssqlStatisticsParser.Parse(StatisticsText);
    }

    [Test]
    public void It_sums_logical_reads_across_tables_excluding_lob_counters()
    {
        _metrics.LogicalReads.Should().Be(2100);
    }

    [Test]
    public void It_sums_physical_reads_across_tables_excluding_lob_counters()
    {
        _metrics.PhysicalReads.Should().Be(12);
    }

    [Test]
    public void It_takes_cpu_and_elapsed_from_the_execution_block_not_parse_time()
    {
        _metrics.DbCpuMs.Should().Be(5.0);
        _metrics.DbElapsedMs.Should().Be(7.0);
    }

    [Test]
    public void It_leaves_postgresql_metrics_null()
    {
        _metrics.BuffersHit.Should().BeNull();
        _metrics.BuffersRead.Should().BeNull();
        _metrics.DbExecutionMs.Should().BeNull();
    }
}

[TestFixture]
public class Given_Multiple_Execution_Times_Blocks
{
    [Test]
    public void It_uses_the_last_block()
    {
        const string text = """
            Table 'Document'. Scan count 1, logical reads 10, physical reads 0.
             SQL Server Execution Times:
               CPU time = 1 ms,  elapsed time = 1 ms.
             SQL Server Execution Times:
               CPU time = 9 ms,  elapsed time = 11 ms.
            """;
        PerfDatabaseMetrics metrics = MssqlStatisticsParser.Parse(text);
        metrics.DbCpuMs.Should().Be(9.0);
        metrics.DbElapsedMs.Should().Be(11.0);
    }
}

[TestFixture]
public class Given_Malformed_Statistics_Text
{
    [Test]
    public void It_rejects_text_without_table_io_lines()
    {
        FluentActions
            .Invoking(() =>
                MssqlStatisticsParser.Parse(
                    "SQL Server Execution Times:\n   CPU time = 5 ms,  elapsed time = 7 ms."
                )
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*per-table read counters*");
    }

    [Test]
    public void It_rejects_text_without_an_execution_times_block()
    {
        FluentActions
            .Invoking(() =>
                MssqlStatisticsParser.Parse(
                    "Table 'Document'. Scan count 1, logical reads 10, physical reads 0."
                )
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*execution-times block*");
    }
}

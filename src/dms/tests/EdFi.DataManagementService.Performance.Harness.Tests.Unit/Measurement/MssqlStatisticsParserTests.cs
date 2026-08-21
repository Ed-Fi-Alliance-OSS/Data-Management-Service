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
public class Given_A_Multi_Statement_Batch_Statistics_Text
{
    // Shaped like a replayed hydration batch: one execution-times block per statement, with
    // per-table IO lines and a mid-batch parse-and-compile block that must not be counted.
    private const string BatchText = """
        Table '#page___000000000012'. Scan count 0, logical reads 26, physical reads 0.
        Table 'Student'. Scan count 1, logical reads 42, physical reads 1.
         SQL Server Execution Times:
           CPU time = 1 ms,  elapsed time = 2 ms.
        SQL Server parse and compile time:
           CPU time = 4 ms, elapsed time = 6 ms.
        Table 'Document'. Scan count 1, logical reads 2058, physical reads 11.
         SQL Server Execution Times:
           CPU time = 9 ms,  elapsed time = 11 ms.
        """;

    private PerfDatabaseMetrics _metrics = null!;

    [SetUp]
    public void Setup()
    {
        _metrics = MssqlStatisticsParser.Parse(BatchText);
    }

    [Test]
    public void It_sums_cpu_and_elapsed_across_all_execution_blocks()
    {
        _metrics.DbCpuMs.Should().Be(10.0);
        _metrics.DbElapsedMs.Should().Be(13.0);
    }

    [Test]
    public void It_excludes_parse_and_compile_time_from_the_sums()
    {
        // The parse-and-compile block's 4/6 ms must not appear in the totals.
        _metrics.DbCpuMs.Should().NotBe(14.0);
        _metrics.DbElapsedMs.Should().NotBe(19.0);
    }

    [Test]
    public void It_sums_reads_across_every_statement_and_table()
    {
        _metrics.LogicalReads.Should().Be(2126);
        _metrics.PhysicalReads.Should().Be(12);
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

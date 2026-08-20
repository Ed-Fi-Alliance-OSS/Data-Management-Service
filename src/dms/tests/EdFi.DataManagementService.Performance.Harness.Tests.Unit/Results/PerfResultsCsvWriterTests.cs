// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal static class CsvTestSupport
{
    public static string[] Lines(string csv) => csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public static string Field(string row, string columnName)
    {
        int index = -1;
        for (int position = 0; position < PerfResultsCsvWriter.HeaderColumns.Count; position++)
        {
            if (PerfResultsCsvWriter.HeaderColumns[position] == columnName)
            {
                index = position;
                break;
            }
        }

        index.Should().BeGreaterThanOrEqualTo(0, $"column '{columnName}' must exist");
        return row.Split(',')[index];
    }
}

[TestFixture]
public class Given_A_Written_Results_Csv
{
    private string _csv = null!;
    private string[] _lines = null!;

    [SetUp]
    public void Setup()
    {
        _csv = PerfResultsCsvWriter.Write([
            ResultSamples.Postgresql(),
            ResultSamples.Mssql(PerfScenarios.TraditionalOffsetShallow, 500),
        ]);
        _lines = CsvTestSupport.Lines(_csv);
    }

    [Test]
    public void It_writes_the_documented_header_in_order()
    {
        _lines[0]
            .Should()
            .Be(
                "provider,scenario_id,page_size,offset,returned_rows,command_count_per_request,"
                    + "warmup_iterations,measured_iterations,p50_ms,p95_ms,mean_ms,min_ms,max_ms,"
                    + "db_command_p50_ms,db_command_p95_ms,db_command_mean_ms,db_command_min_ms,"
                    + "db_command_max_ms,db_execution_ms,db_cpu_ms,db_elapsed_ms,db_logical_reads,"
                    + "db_physical_reads,db_buffers_hit,db_buffers_read,plan_file,"
                    + "page_selection_sql_sha256,runner_commit,subject_commit"
            );
    }

    [Test]
    public void It_writes_one_data_row_per_result()
    {
        _lines.Should().HaveCount(3);
    }

    [Test]
    public void It_writes_every_column_on_each_row()
    {
        _lines[1].Split(',').Should().HaveCount(PerfResultsCsvWriter.HeaderColumns.Count);
        _lines[2].Split(',').Should().HaveCount(PerfResultsCsvWriter.HeaderColumns.Count);
    }

    [Test]
    public void It_orders_rows_canonically()
    {
        CsvTestSupport.Field(_lines[1], "provider").Should().Be("mssql");
        CsvTestSupport.Field(_lines[2], "provider").Should().Be("postgresql");
    }

    [Test]
    public void It_formats_latency_with_three_invariant_decimals()
    {
        CsvTestSupport.Field(_lines[2], "p50_ms").Should().Be("12.500");
        CsvTestSupport.Field(_lines[2], "db_command_p95_ms").Should().Be("15.000");
    }

    [Test]
    public void It_blanks_sql_server_metrics_on_the_postgresql_row()
    {
        CsvTestSupport.Field(_lines[2], "db_logical_reads").Should().BeEmpty();
        CsvTestSupport.Field(_lines[2], "db_cpu_ms").Should().BeEmpty();
        CsvTestSupport.Field(_lines[2], "db_buffers_hit").Should().Be("1200");
    }

    [Test]
    public void It_blanks_postgresql_metrics_on_the_sql_server_row()
    {
        CsvTestSupport.Field(_lines[1], "db_buffers_hit").Should().BeEmpty();
        CsvTestSupport.Field(_lines[1], "db_execution_ms").Should().BeEmpty();
        CsvTestSupport.Field(_lines[1], "db_logical_reads").Should().Be("2100");
    }

    [Test]
    public void It_uses_lf_only_line_endings()
    {
        _csv.Should().NotContain("\r");
    }
}

[TestFixture]
public class Given_A_Field_Containing_A_Comma
{
    private string _row = null!;

    [SetUp]
    public void Setup()
    {
        PerfScenarioResult result = ResultSamples.Postgresql() with { PlanFile = "plans/a,b.json" };
        _row = CsvTestSupport.Lines(PerfResultsCsvWriter.Write([result]))[1];
    }

    [Test]
    public void It_quotes_the_field()
    {
        _row.Should().Contain("\"plans/a,b.json\"");
    }
}

[TestFixture]
public class Given_A_Comma_Decimal_Current_Culture
{
    private CultureInfo _originalCulture = null!;
    private string _csv = null!;

    [SetUp]
    public void Setup()
    {
        _originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        _csv = PerfResultsCsvWriter.Write([ResultSamples.Postgresql()]);
    }

    [TearDown]
    public void TearDown()
    {
        CultureInfo.CurrentCulture = _originalCulture;
    }

    [Test]
    public void It_still_formats_with_invariant_decimal_points()
    {
        string row = CsvTestSupport.Lines(_csv)[1];
        CsvTestSupport.Field(row, "p50_ms").Should().Be("12.500");
        CsvTestSupport.Field(row, "db_execution_ms").Should().Be("6.250");
    }
}

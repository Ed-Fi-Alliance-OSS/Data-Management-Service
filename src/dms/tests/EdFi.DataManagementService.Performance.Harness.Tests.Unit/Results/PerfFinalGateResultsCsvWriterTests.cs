// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

[TestFixture]
public class Given_The_Final_Gate_Csv_Writer
{
    [Test]
    public void It_writes_the_fixed_header_column_set()
    {
        string csv = PerfFinalGateResultsCsvWriter.Write([]);

        csv.Should().Be(string.Join(',', PerfFinalGateResultsCsvWriter.HeaderColumns) + "\n");
        PerfFinalGateResultsCsvWriter.HeaderColumns.Should().HaveCount(37);
    }

    [Test]
    public void It_writes_one_lf_terminated_row_per_cell_in_document_order()
    {
        string csv = PerfFinalGateResultsCsvWriter.Write(FinalGateResultSamples.PrimaryDocument().Results);
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(PerfFinalGateScenarios.PrimaryCellsInExecutionOrder.Count + 1);
        csv.Should().NotContain("\r");
        for (int index = 0; index < PerfFinalGateScenarios.PrimaryCellsInExecutionOrder.Count; index++)
        {
            lines[index + 1]
                .Should()
                .StartWith(
                    "postgresql," + PerfFinalGateScenarios.PrimaryCellsInExecutionOrder[index].ScenarioId
                );
        }
    }

    [Test]
    public void It_leaves_page_shaped_columns_blank_on_a_partition_row()
    {
        PerfFinalGateScenarioResult partitionRow = FinalGateResultSamples.Row(
            PerfFinalGateScenarios.PrimaryCellsInExecutionOrder.First(cell =>
                cell.Family == PerfScenarioFamily.Partition
            )
        );

        string csv = PerfFinalGateResultsCsvWriter.Write([partitionRow]);
        string[] fields = csv.Split('\n')[1].Split(',');
        IReadOnlyList<string> header = PerfFinalGateResultsCsvWriter.HeaderColumns;

        fields[header.IndexOf("page_size")].Should().BeEmpty();
        fields[header.IndexOf("offset")].Should().BeEmpty();
        fields[header.IndexOf("cursor_range")].Should().BeEmpty();
        fields[header.IndexOf("returned_rows")].Should().BeEmpty();
        fields[header.IndexOf("requested_partition_number")].Should().NotBeEmpty();
        fields[header.IndexOf("returned_token_count")].Should().NotBeEmpty();
    }

    [Test]
    public void It_leaves_the_other_provider_metric_columns_blank()
    {
        PerfFinalGateScenarioResult postgresqlRow = FinalGateResultSamples.Row(
            PerfFinalGateScenarios.PrimaryCellsInExecutionOrder[0]
        );

        string csv = PerfFinalGateResultsCsvWriter.Write([postgresqlRow]);
        string[] fields = csv.Split('\n')[1].Split(',');
        IReadOnlyList<string> header = PerfFinalGateResultsCsvWriter.HeaderColumns;

        fields[header.IndexOf("db_buffers_hit")].Should().NotBeEmpty();
        fields[header.IndexOf("db_logical_reads")].Should().BeEmpty();
        fields[header.IndexOf("db_cpu_ms")].Should().BeEmpty();
    }
}

internal static class HeaderColumnExtensions
{
    public static int IndexOf(this IReadOnlyList<string> columns, string name)
    {
        for (int index = 0; index < columns.Count; index++)
        {
            if (columns[index] == name)
            {
                return index;
            }
        }

        throw new ArgumentException($"Column '{name}' is not in the header.", nameof(name));
    }
}

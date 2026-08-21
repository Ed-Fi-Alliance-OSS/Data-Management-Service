// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Writes results.csv: one row per scenario cell in the canonical ordering, a fixed column
/// set, invariant-culture formatting, LF-only line endings, and blank cells where a metric
/// does not apply to the row's provider. Any column change requires a schema version bump.
/// </summary>
public static class PerfResultsCsvWriter
{
    public static readonly IReadOnlyList<string> HeaderColumns =
    [
        "provider",
        "scenario_id",
        "page_size",
        "offset",
        "returned_rows",
        "command_count_per_request",
        "warmup_iterations",
        "measured_iterations",
        "p50_ms",
        "p95_ms",
        "mean_ms",
        "min_ms",
        "max_ms",
        "driver_execute_p50_ms",
        "driver_execute_p95_ms",
        "driver_execute_mean_ms",
        "driver_execute_min_ms",
        "driver_execute_max_ms",
        "db_execution_ms",
        "db_cpu_ms",
        "db_elapsed_ms",
        "db_logical_reads",
        "db_physical_reads",
        "db_buffers_hit",
        "db_buffers_read",
        "plan_file",
        "page_selection_sql_sha256",
        "runner_commit",
        "subject_commit",
    ];

    public static string Write(IEnumerable<PerfScenarioResult> results)
    {
        StringBuilder builder = new();
        AppendRow(builder, HeaderColumns);

        foreach (PerfScenarioResult result in PerfResultOrdering.Order(results))
        {
            AppendRow(builder, FieldsFor(result));
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, IEnumerable<string> fields)
    {
        builder.Append(string.Join(',', fields));
        builder.Append('\n');
    }

    private static IReadOnlyList<string> FieldsFor(PerfScenarioResult result) =>
        [
            Escape(result.Provider),
            Escape(result.ScenarioId),
            Integer(result.PageSize),
            Integer(result.Offset),
            Integer(result.ReturnedRows),
            Integer(result.CommandCountPerRequest),
            Integer(result.WarmupIterations),
            Integer(result.MeasuredIterations),
            Milliseconds(result.LatencyMs.P50Ms),
            Milliseconds(result.LatencyMs.P95Ms),
            Milliseconds(result.LatencyMs.MeanMs),
            Milliseconds(result.LatencyMs.MinMs),
            Milliseconds(result.LatencyMs.MaxMs),
            Milliseconds(result.DriverExecuteMs.P50Ms),
            Milliseconds(result.DriverExecuteMs.P95Ms),
            Milliseconds(result.DriverExecuteMs.MeanMs),
            Milliseconds(result.DriverExecuteMs.MinMs),
            Milliseconds(result.DriverExecuteMs.MaxMs),
            MillisecondsOrBlank(result.Database.DbExecutionMs),
            MillisecondsOrBlank(result.Database.DbCpuMs),
            MillisecondsOrBlank(result.Database.DbElapsedMs),
            IntegerOrBlank(result.Database.LogicalReads),
            IntegerOrBlank(result.Database.PhysicalReads),
            IntegerOrBlank(result.Database.BuffersHit),
            IntegerOrBlank(result.Database.BuffersRead),
            Escape(result.PlanFile),
            Escape(result.PageSelectionSqlSha256),
            Escape(result.RunnerCommit),
            Escape(result.SubjectCommit),
        ];

    private static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string IntegerOrBlank(long? value) => value is null ? string.Empty : Integer(value.Value);

    private static string Milliseconds(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string MillisecondsOrBlank(double? value) =>
        value is null ? string.Empty : Milliseconds(value.Value);

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

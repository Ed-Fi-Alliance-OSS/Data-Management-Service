// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Writes a final-gate results.csv: one row per cell in execution order, a fixed column set,
/// invariant-culture formatting, LF-only line endings, and blank cells where a field does not
/// apply to the row's family or provider. Any column change requires a schema version bump.
/// </summary>
public static class PerfFinalGateResultsCsvWriter
{
    public static readonly IReadOnlyList<string> HeaderColumns =
    [
        "provider",
        "scenario_id",
        "family",
        "variant",
        "phase",
        "page_size",
        "offset",
        "cursor_range",
        "start_anchor_document_id",
        "requested_partition_number",
        "returned_rows",
        "returned_token_count",
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
        "selection_sql_sha256",
        "replay_parameter_source",
        "runner_commit",
        "subject_commit",
    ];

    public static string Write(IEnumerable<PerfFinalGateScenarioResult> results)
    {
        StringBuilder builder = new();
        AppendRow(builder, HeaderColumns);

        foreach (PerfFinalGateScenarioResult result in results)
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

    private static IReadOnlyList<string> FieldsFor(PerfFinalGateScenarioResult result) =>
        [
            Escape(result.Provider),
            Escape(result.ScenarioId),
            Escape(result.Family),
            Escape(result.Variant),
            Escape(result.Phase ?? string.Empty),
            IntegerOrBlank(result.PageSize),
            IntegerOrBlank(result.Offset),
            Escape(result.CursorRange ?? string.Empty),
            IntegerOrBlank(result.StartAnchorDocumentId),
            IntegerOrBlank(result.RequestedPartitionNumber),
            IntegerOrBlank(result.ReturnedRows),
            IntegerOrBlank(result.ReturnedTokenCount),
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
            Escape(result.SelectionSqlSha256),
            Escape(result.ReplayParameterSource),
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

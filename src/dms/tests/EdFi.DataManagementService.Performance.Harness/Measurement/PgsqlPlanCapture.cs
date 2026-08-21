// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// The captured PostgreSQL plan evidence for one cell: the full EXPLAIN JSON retained as a
/// text artifact, and the metrics parsed out of it for the results row.
/// </summary>
public sealed record PgsqlPlanCaptureResult(string ExplainJson, Results.PerfDatabaseMetrics Metrics);

/// <summary>
/// Replays the recorded page-selection statement under
/// EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) with the same bound parameter values, on an
/// out-of-band connection to the same warm database. The root plan node's shared buffer
/// counters are cumulative over its children, so they are the statement totals.
/// </summary>
public static class PgsqlPlanCapture
{
    private const int CommandTimeoutSeconds = 600;

    public static string ExplainSql(string pageSelectionSql) =>
        "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\n" + pageSelectionSql;

    public static async Task<PgsqlPlanCaptureResult> CaptureAsync(
        DbConnection connection,
        PageSelectionQueryCapture capture
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = ExplainSql(capture.PageDocumentIdSql);
        command.CommandTimeout = CommandTimeoutSeconds;
        foreach ((string name, object? value) in capture.ParameterValues)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        object? scalar = await command.ExecuteScalarAsync();
        string explainJson =
            scalar as string
            ?? throw new PerfObservationException(
                "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) returned no JSON document."
            );

        return new PgsqlPlanCaptureResult(explainJson, ParseMetrics(explainJson));
    }

    public static Results.PerfDatabaseMetrics ParseMetrics(string explainJson)
    {
        JsonNode root =
            JsonNode.Parse(explainJson) ?? throw new PerfObservationException("EXPLAIN JSON parsed to null.");

        if (root is not JsonArray array || array.Count == 0)
        {
            throw new PerfObservationException("EXPLAIN JSON must be a non-empty array.");
        }

        JsonNode entry = array[0] ?? throw new PerfObservationException("EXPLAIN JSON entry is missing.");
        JsonNode plan =
            entry["Plan"] ?? throw new PerfObservationException("EXPLAIN JSON entry carries no Plan node.");

        double executionMs = RequiredDouble(entry, "Execution Time");
        long buffersHit = RequiredLong(plan, "Shared Hit Blocks");
        long buffersRead = RequiredLong(plan, "Shared Read Blocks");

        return new Results.PerfDatabaseMetrics(
            BuffersHit: buffersHit,
            BuffersRead: buffersRead,
            DbExecutionMs: executionMs,
            LogicalReads: null,
            PhysicalReads: null,
            DbCpuMs: null,
            DbElapsedMs: null
        );
    }

    private static double RequiredDouble(JsonNode node, string propertyName) =>
        node[propertyName]?.GetValue<double>()
        ?? throw new PerfObservationException($"EXPLAIN JSON carries no '{propertyName}' value.");

    private static long RequiredLong(JsonNode node, string propertyName) =>
        node[propertyName]?.GetValue<long>()
        ?? throw new PerfObservationException($"EXPLAIN JSON plan carries no '{propertyName}' value.");
}

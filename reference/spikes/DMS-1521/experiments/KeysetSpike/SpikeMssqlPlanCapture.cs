// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using Microsoft.Data.SqlClient;

namespace KeysetSpike;

public sealed record SpikeMssqlPlanCaptureResult(
    IReadOnlyList<string> ShowplanXmlDocuments,
    string StatisticsText,
    PerfDatabaseMetrics Metrics
);

/// <summary>
/// The harness <c>MssqlPlanCapture</c> method (SET STATISTICS XML, IO, and TIME over the whole
/// batch, on one connection), but binding every parameter from its captured specification. The
/// harness class binds only names and values, so SqlClient would infer the type and size and lose
/// production's <c>nvarchar(max)</c> JSON parameter; this class never infers.
/// </summary>
public static class SpikeMssqlPlanCapture
{
    private const int CommandTimeoutSeconds = 600;

    public static async Task<SpikeMssqlPlanCaptureResult> CaptureAsync(
        SqlConnection connection,
        string batch,
        IReadOnlyList<SpikeParameterSpec> parameters
    )
    {
        List<string> messages = [];
        SqlInfoMessageEventHandler handler = (_, args) =>
        {
            lock (messages)
            {
                messages.Add(args.Message);
            }
        };

        await ExecuteAsync(
            connection,
            "SET STATISTICS XML ON; SET STATISTICS IO ON; SET STATISTICS TIME ON;"
        );
        List<string> plans = [];
        try
        {
            connection.InfoMessage += handler;
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = CommandTimeoutSeconds;
            foreach (SpikeParameterSpec parameter in parameters)
            {
                parameter.Bind(command);
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(string))
                    {
                        string value = reader.GetString(0);
                        if (value.Contains("<ShowPlanXML", StringComparison.Ordinal))
                        {
                            plans.Add(value);
                        }
                    }
                }
            } while (await reader.NextResultAsync());
        }
        finally
        {
            connection.InfoMessage -= handler;
            await ExecuteAsync(
                connection,
                "SET STATISTICS XML OFF; SET STATISTICS IO OFF; SET STATISTICS TIME OFF;"
            );
        }

        if (plans.Count == 0)
        {
            throw new InvalidOperationException("The replay returned no actual XML plan.");
        }

        string statistics;
        lock (messages)
        {
            statistics = string.Join("\n", messages);
        }

        return new SpikeMssqlPlanCaptureResult(plans, statistics, MssqlStatisticsParser.Parse(statistics));
    }

    public static JsonObject Summary(SpikeMssqlPlanCaptureResult result, string filePrefix) =>
        new()
        {
            ["replay"] =
                "whole batch under SET STATISTICS XML, IO, TIME ON; parameters bound from captured specifications",
            ["planFiles"] = new JsonArray([
                .. result.ShowplanXmlDocuments.Select(
                    (_, index) => (JsonNode)$"{filePrefix}.plan{index + 1:D2}.sqlplan"
                ),
            ]),
            ["statisticsFile"] = $"{filePrefix}.stats.txt",
            ["totals"] = new JsonObject
            {
                ["logicalReads"] = result.Metrics.LogicalReads,
                ["physicalReads"] = result.Metrics.PhysicalReads,
                ["cpuMs"] = result.Metrics.DbCpuMs,
                ["elapsedMs"] = result.Metrics.DbElapsedMs,
            },
        };

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }
}

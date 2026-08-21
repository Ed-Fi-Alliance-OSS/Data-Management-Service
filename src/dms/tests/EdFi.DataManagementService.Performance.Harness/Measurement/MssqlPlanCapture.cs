// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Results;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// The captured SQL Server plan evidence for one cell: every actual XML plan the replayed
/// hydration batch produced (one per planned statement, in arrival order), the raw
/// STATISTICS IO/TIME message text for the whole batch, and the metrics parsed out of that
/// text for the results row.
/// </summary>
public sealed record MssqlPlanCaptureResult(
    IReadOnlyList<string> ShowplanXmlDocuments,
    string StatisticsText,
    PerfDatabaseMetrics Metrics
);

/// <summary>
/// Replays the full recorded hydration batch — the one DbCommand the measured request
/// executed — under SET STATISTICS XML, IO, TIME ON with the same bound parameter values.
/// Each planned statement's actual XML plan arrives as its own extra result set among the
/// batch's result sets; the IO/TIME counters arrive as InfoMessage text on the connection.
/// The SET options are session state, so the ON, replay, and OFF commands must share one
/// connection.
/// </summary>
public static class MssqlPlanCapture
{
    private const int CommandTimeoutSeconds = 600;

    private const string StatisticsOnSql =
        "SET STATISTICS XML ON; SET STATISTICS IO ON; SET STATISTICS TIME ON;";

    private const string StatisticsOffSql =
        "SET STATISTICS XML OFF; SET STATISTICS IO OFF; SET STATISTICS TIME OFF;";

    public static async Task<MssqlPlanCaptureResult> CaptureAsync(
        DbConnection connection,
        string hydrationBatchSql,
        IReadOnlyDictionary<string, object?> parameterValues
    )
    {
        if (connection is not SqlConnection sqlConnection)
        {
            throw new PerfObservationException(
                $"SQL Server plan capture requires a SqlConnection; got {connection.GetType().Name}."
            );
        }

        List<string> messages = [];
        SqlInfoMessageEventHandler handler = (_, args) =>
        {
            lock (messages)
            {
                messages.Add(args.Message);
            }
        };

        await ExecuteNonQueryAsync(connection, StatisticsOnSql);
        List<string> showplanXmlDocuments = [];
        try
        {
            sqlConnection.InfoMessage += handler;

            await using DbCommand command = connection.CreateCommand();
            command.CommandText = hydrationBatchSql;
            command.CommandTimeout = CommandTimeoutSeconds;
            foreach ((string name, object? value) in parameterValues)
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
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
                            showplanXmlDocuments.Add(value);
                        }
                    }
                }
            } while (await reader.NextResultAsync());
        }
        finally
        {
            sqlConnection.InfoMessage -= handler;
            await ExecuteNonQueryAsync(connection, StatisticsOffSql);
        }

        if (showplanXmlDocuments.Count == 0)
        {
            throw new PerfObservationException("The replay returned no actual XML plan result set.");
        }

        string statisticsText;
        lock (messages)
        {
            statisticsText = string.Join("\n", messages);
        }

        return new MssqlPlanCaptureResult(
            showplanXmlDocuments,
            statisticsText,
            MssqlStatisticsParser.Parse(statisticsText)
        );
    }

    /// <summary>
    /// Builds the cell's primary plan file: an index over the full-batch evidence, pointing
    /// at every per-statement .sqlplan (in arrival order) and the raw statistics text.
    /// </summary>
    public static string PlanIndexJson(IReadOnlyList<string> planFilePaths, string statisticsFilePath)
    {
        if (planFilePaths.Count == 0)
        {
            throw new PerfObservationException("A plan index requires at least one plan file.");
        }

        JsonObject index = new()
        {
            ["replay"] =
                "full hydration batch under SET STATISTICS XML, IO, TIME ON; one actual XML "
                + "plan per planned statement, in arrival order",
            ["planFiles"] = new JsonArray([.. planFilePaths.Select(path => (JsonNode)path)]),
            ["statisticsFile"] = statisticsFilePath,
        };

        return index.ToJsonString(
            // LF-only like every other artifact, so runs diff identically across platforms.
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }
        );
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Performance.Harness.Results;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// The captured SQL Server plan evidence for one cell: the actual XML plan retained as a
/// .sqlplan artifact, the raw STATISTICS IO/TIME message text retained beside it, and the
/// metrics parsed out of that text for the results row.
/// </summary>
public sealed record MssqlPlanCaptureResult(
    string ShowplanXml,
    string StatisticsText,
    PerfDatabaseMetrics Metrics
);

/// <summary>
/// Replays the recorded page-selection statement under SET STATISTICS XML, IO, TIME ON with
/// the same bound parameter values. The actual XML plan arrives as an extra result set after
/// the statement's rows; the IO/TIME counters arrive as InfoMessage text on the connection.
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
        PageSelectionQueryCapture capture
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
        string? showplanXml = null;
        try
        {
            sqlConnection.InfoMessage += handler;

            await using DbCommand command = connection.CreateCommand();
            command.CommandText = capture.PageDocumentIdSql;
            command.CommandTimeout = CommandTimeoutSeconds;
            foreach ((string name, object? value) in capture.ParameterValues)
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
                            showplanXml = value;
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

        if (showplanXml is null)
        {
            throw new PerfObservationException("The replay returned no actual XML plan result set.");
        }

        string statisticsText;
        lock (messages)
        {
            statisticsText = string.Join("\n", messages);
        }

        return new MssqlPlanCaptureResult(
            showplanXml,
            statisticsText,
            MssqlStatisticsParser.Parse(statisticsText)
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

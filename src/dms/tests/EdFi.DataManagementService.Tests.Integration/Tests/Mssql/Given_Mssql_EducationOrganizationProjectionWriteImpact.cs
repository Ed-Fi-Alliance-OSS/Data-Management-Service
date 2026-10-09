// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// The projection's provider read against production writes on SQL Server, with production write
/// isolation (RCSI and snapshot isolation enabled, as DMS provisioning configures new databases).
/// </summary>
public sealed class Given_Mssql_EducationOrganizationProjectionWriteImpact : MssqlConcurrentWriteLoadTestBase
{
    // Only the projection statement aliases a column this way. "[[]" matches a literal "[" in LIKE,
    // where a bare bracket would open a character class.
    private const string ReaderMarker = "%[[]StateEducationAgencyReference]%";

    [Test]
    [Explicit(
        "Projection provider measurement under production writes; run on demand and record the output."
    )]
    [Category("ProjectionMeasurement")]
    public Task It_measures_provider_reads_against_production_writes()
    {
        AllowRequestsToOutlastDefaultClientTimeout();

        return EducationOrganizationProjectionWriteImpactScenario.MeasureAsync(
            Harness,
            PrimaryConnectionString,
            new EducationOrganizationProjectionWriteImpactScenario.Engine(
                Name: "sqlserver",
                Dialect: SqlDialect.Mssql,
                EffectiveSchemaHashAsync: () =>
                    ScalarAsync<string>(
                        PrimaryConnectionString,
                        "SELECT [EffectiveSchemaHash] FROM [dms].[EffectiveSchema];"
                    ),
                DeadlocksAsync: () =>
                    ScalarAsync<long>(
                        MssqlTestDatabaseHelper.BuildConnectionString("master"),
                        """
                        SELECT cntr_value FROM sys.dm_os_performance_counters
                        WHERE counter_name LIKE N'Number of Deadlocks/sec%' AND instance_name = N'_Total';
                        """
                    ),
                LockWaitTotalsAsync: async () =>
                {
                    var totals = await CaptureLockWaitsAsync();
                    return (totals.WaitingTasks, totals.WaitTimeMs);
                },
                SampleLockWaitersAsync: SampleLockWaitersAsync,
                StartDeadlockGraphsAsync: StartDeadlockCaptureAsync,
                FinishDeadlockGraphsAsync: async () =>
                {
                    var capture = await CaptureDeadlockSignaturesAsync();
                    return capture.IsInconclusive
                        ? $"inconclusive({capture.InconclusiveReason})"
                        : $"{capture.AttributedGraphCount}[{string.Join(" | ", capture.Signatures)}]";
                }
            )
        );
    }

    private async Task<(int Writers, int Reader)> SampleLockWaitersAsync()
    {
        await using var connection = new SqlConnection(PrimaryConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT
                ISNULL(SUM(CASE WHEN t.[text] LIKE @reader THEN 0 ELSE 1 END), 0),
                ISNULL(SUM(CASE WHEN t.[text] LIKE @reader THEN 1 ELSE 0 END), 0)
            FROM sys.dm_exec_requests r
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.wait_type LIKE N'LCK[_]M[_]%' AND r.database_id = DB_ID();
            """,
            connection
        );
        command.Parameters.AddWithValue("@reader", ReaderMarker);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }
}

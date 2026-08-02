// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// Diagnostic reproduction of the populated-template bulk-load deadlock. Drives concurrent
/// ChartOfAccount creates through the real HTTP pipeline against a leased SQL Server database
/// configured the way production provisioning configures it, then reports any deadlock graphs
/// the always-on system_health session captured.
/// </summary>
public sealed class Given_Mssql_ConcurrentChartOfAccountCreates : MssqlApiIntegrationTestBase
{
    private const string Json = "application/json";
    private const int Workers = 16;
    private const int PerWorker = 25;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    [Explicit("Diagnostic reproduction; runs a concurrent write load.")]
    public async Task It_reproduces_the_bulk_load_deadlock()
    {
        await MatchProductionIsolationAsync();

        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string ns = $"uri://ed-fi.org/CoaDeadlock/{suffix}";

        await SeedDescriptorAsync(
            "/data/ed-fi/educationOrganizationCategoryDescriptors",
            $"{ns}/EducationOrganizationCategoryDescriptor",
            "School"
        );
        await SeedDescriptorAsync(
            "/data/ed-fi/gradeLevelDescriptors",
            $"{ns}/GradeLevelDescriptor",
            "Tenth grade"
        );
        await SeedDescriptorAsync(
            "/data/ed-fi/accountTypeDescriptors",
            $"{ns}/AccountTypeDescriptor",
            "Revenue"
        );
        await SeedDescriptorAsync(
            "/data/ed-fi/reportingTagDescriptors",
            $"{ns}/ReportingTagDescriptor",
            "ESSA"
        );

        var school = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = $"COA Deadlock School {suffix}",
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        $"{ns}/EducationOrganizationCategoryDescriptor#School",
                }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject { ["gradeLevelDescriptor"] = $"{ns}/GradeLevelDescriptor#Tenth grade" }
            ),
        };
        var (schoolStatus, schoolBody) = await PostAsync("/data/ed-fi/schools", school);
        await TestContext.Out.WriteLineAsync($"School create: {(int)schoolStatus} {schoolBody}");

        ConcurrentBag<(int Status, string Body)> failures = [];
        int created = 0;

        await Task.WhenAll(
            Enumerable
                .Range(0, Workers)
                .Select(worker =>
                    Task.Run(async () =>
                    {
                        for (int i = 0; i < PerWorker; i++)
                        {
                            var coa = new JsonObject
                            {
                                ["accountIdentifier"] = $"{suffix}-{worker:D2}-{i:D3}",
                                ["fiscalYear"] = 2024,
                                ["educationOrganizationReference"] = new JsonObject
                                {
                                    ["educationOrganizationId"] = schoolId,
                                },
                                ["accountTypeDescriptor"] = $"{ns}/AccountTypeDescriptor#Revenue",
                                ["accountName"] = $"Account {worker}-{i}",
                                ["reportingTags"] = new JsonArray(
                                    new JsonObject
                                    {
                                        ["reportingTagDescriptor"] = $"{ns}/ReportingTagDescriptor#ESSA",
                                        ["tagValue"] = $"tag-{worker}-{i}",
                                    }
                                ),
                            };

                            var (status, body) = await PostAsync("/data/ed-fi/chartOfAccounts", coa);
                            if (status is HttpStatusCode.Created or HttpStatusCode.OK)
                            {
                                Interlocked.Increment(ref created);
                            }
                            else
                            {
                                failures.Add(((int)status, body));
                            }
                        }
                    })
                )
        );

        await TestContext.Out.WriteLineAsync(
            $"=== Created {created} of {Workers * PerWorker}; {failures.Count} failed ==="
        );
        foreach (var group in failures.GroupBy(failure => failure.Status))
        {
            await TestContext.Out.WriteLineAsync($"--- status {group.Key} x{group.Count()} ---");
            await TestContext.Out.WriteLineAsync(group.First().Body);
        }

        await ReportDeadlockGraphsAsync();
    }

    /// <summary>
    /// Production provisioning enables RCSI and snapshot isolation; the leased test databases do
    /// not, so the branch's locking behavior would otherwise be exercised under an isolation
    /// configuration production never uses.
    /// </summary>
    private async Task MatchProductionIsolationAsync()
    {
        string databaseName = Harness.DbConnection.Database;
        string quoted = MssqlTestDatabaseHelper.QuoteIdentifier(databaseName);

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"ALTER DATABASE {quoted} SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;"
        );
        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"ALTER DATABASE {quoted} SET ALLOW_SNAPSHOT_ISOLATION ON;"
        );

        await using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString("master")
        );
        await connection.OpenAsync();
        await using var verify = connection.CreateCommand();
        verify.CommandText =
            "SELECT CONCAT(is_read_committed_snapshot_on, '/', snapshot_isolation_state_desc) "
            + $"FROM sys.databases WHERE name = '{MssqlTestDatabaseHelper.EscapeSqlLiteral(databaseName)}';";
        await TestContext.Out.WriteLineAsync(
            $"Isolation for {databaseName}: {await verify.ExecuteScalarAsync()}"
        );
    }

    private static async Task ReportDeadlockGraphsAsync()
    {
        await using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString("master")
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(target.event_data AS NVARCHAR(MAX))
            FROM (
                SELECT CAST(xet.target_data AS XML) AS target_data
                FROM sys.dm_xe_session_targets xet
                JOIN sys.dm_xe_sessions xes ON xes.address = xet.event_session_address
                WHERE xes.name = 'system_health' AND xet.target_name = 'ring_buffer'
            ) AS ring
            CROSS APPLY (
                SELECT node.query('.') AS event_data
                FROM ring.target_data.nodes('//RingBufferTarget/event[@name="xml_deadlock_report"]')
                    AS t(node)
            ) AS target;
            """;

        int count = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            count++;
            await TestContext.Out.WriteLineAsync($"=== DEADLOCK GRAPH {count} ===");
            await TestContext.Out.WriteLineAsync(reader.GetString(0));
        }

        await TestContext.Out.WriteLineAsync($"=== {count} deadlock graph(s) captured ===");
    }

    private Task SeedDescriptorAsync(string endpoint, string namespaceUri, string codeValue) =>
        PostAsync(
            endpoint,
            new JsonObject
            {
                ["namespace"] = namespaceUri,
                ["codeValue"] = codeValue,
                ["shortDescription"] = codeValue,
            }
        );

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string endpoint, JsonObject payload)
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, Json);
        using HttpResponseMessage response = await Harness.HttpClient.PostAsync(endpoint, content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}

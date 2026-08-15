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
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// Reproduction for the SQL Server deadlock misclassification: a transient, server-side
/// database failure must never be reported to the client as a 4xx client error, because
/// well-behaved clients do not retry 4xx and the document is then lost without a signal.
///
/// Drives concurrent ChartOfAccount creates through the real HTTP pipeline against a leased
/// SQL Server database configured the way production provisioning configures it, then
/// classifies every response and reports what the database actually persisted.
/// </summary>
public sealed class Given_Mssql_ConcurrentCreatesUnderDeadlockPressure : MssqlApiIntegrationTestBase
{
    private const string Json = "application/json";
    private const int Workers = 16;
    private const int PerWorker = 25;
    private const int TotalAttempts = Workers * PerWorker;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    [Explicit("Concurrency load reproduction; drives a deliberate deadlock storm.")]
    public async Task It_never_reports_a_transient_database_failure_as_a_client_error()
    {
        await MatchProductionIsolationAsync();

        // The load deliberately starves the server, so a single request can outlast the default
        // 100-second client timeout while it waits on locks and replays deadlocks. A client-side
        // cancellation would be indistinguishable from the failure under test.
        Harness.HttpClient.Timeout = TimeSpan.FromMinutes(10);

        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string ns = $"uri://ed-fi.org/Dms1400/{suffix}";

        await SeedFixtureAsync(ns, schoolId, suffix);

        // The system_health ring buffer is server-wide and survives across tests, so only the
        // growth over this load is attributable to it.
        int deadlockGraphsBefore = await CountDeadlockGraphsAsync();

        ConcurrentBag<(int Status, string Body)> responses = [];

        await Task.WhenAll(
            Enumerable
                .Range(0, Workers)
                .Select(worker =>
                    Task.Run(async () =>
                    {
                        for (int i = 0; i < PerWorker; i++)
                        {
                            var (status, body) = await PostAsync(
                                "/data/ed-fi/chartOfAccounts",
                                BuildChartOfAccount(ns, schoolId, suffix, worker, i)
                            );
                            responses.Add(((int)status, body));
                        }
                    })
                )
        );

        int accepted = responses.Count(response => response.Status is 200 or 201);
        var failures = responses.Where(response => response.Status is not (200 or 201)).ToArray();
        long persisted = await CountPersistedChartOfAccountsAsync();
        int deadlockGraphs = await CountDeadlockGraphsAsync() - deadlockGraphsBefore;

        await ReportAsync(accepted, persisted, deadlockGraphs, failures);

        var clientErrors = failures.Where(failure => failure.Status is >= 400 and < 500).ToArray();

        // A deadlock victim (SQL Server 1205) is retriable and server-side. Reporting it - or any
        // other transient database failure - as a 4xx tells the client its request was malformed,
        // so the client will not retry and the document is silently dropped.
        clientErrors
            .Should()
            .BeEmpty(
                "a transient database failure must not be reported as a client error, but "
                    + $"{clientErrors.Length} of {TotalAttempts} attempts returned 4xx: "
                    + string.Join(" | ", clientErrors.Select(SignatureOf).Distinct(StringComparer.Ordinal))
            );
    }

    private static async Task ReportAsync(
        int accepted,
        long persisted,
        int deadlockGraphs,
        IReadOnlyCollection<(int Status, string Body)> failures
    )
    {
        await TestContext.Out.WriteLineAsync(
            $"=== {TotalAttempts} attempts: {accepted} accepted, {failures.Count} failed; "
                + $"{persisted} rows in edfi.ChartOfAccount; {deadlockGraphs} deadlock graph(s) ==="
        );

        foreach (
            var group in failures
                .GroupBy(failure => (failure.Status, Signature: SignatureOf(failure)))
                .OrderByDescending(group => group.Count())
        )
        {
            await TestContext.Out.WriteLineAsync(
                $"--- status {group.Key.Status} x{group.Count()} --- {group.Key.Signature}"
            );
            await TestContext.Out.WriteLineAsync($"    {group.First().Body}");
        }
    }

    /// <summary>
    /// Reduces a problem-details body to its identifying fields so responses that differ only by
    /// correlationId group together.
    /// </summary>
    private static string SignatureOf((int Status, string Body) failure)
    {
        JsonNode? body = TryParse(failure.Body);
        if (body is null)
        {
            return failure.Body;
        }

        string type = body["type"]?.GetValue<string>() ?? "(no type)";
        string errors = string.Join(
            "; ",
            (body["validationErrors"] as JsonObject ?? []).Select(pair =>
                $"{pair.Key}={pair.Value?.ToJsonString()}"
            )
        );

        return errors.Length == 0 ? type : $"{type} {errors}";
    }

    private static JsonNode? TryParse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<long> CountPersistedChartOfAccountsAsync()
    {
        await using var command = Harness.DbConnection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(1) FROM [edfi].[ChartOfAccount];";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Production provisioning enables RCSI and snapshot isolation; the leased test databases do
    /// not, so the locking behavior would otherwise be exercised under an isolation configuration
    /// production never uses.
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
    }

    private static async Task<int> CountDeadlockGraphsAsync()
    {
        await using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString("master")
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(1)
            FROM (
                SELECT CAST(xet.target_data AS XML) AS target_data
                FROM sys.dm_xe_session_targets xet
                JOIN sys.dm_xe_sessions xes ON xes.address = xet.event_session_address
                WHERE xes.name = 'system_health' AND xet.target_name = 'ring_buffer'
            ) AS ring
            CROSS APPLY ring.target_data.nodes('//RingBufferTarget/event[@name="xml_deadlock_report"]')
                AS t(node);
            """;

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static JsonObject BuildChartOfAccount(
        string ns,
        long schoolId,
        string suffix,
        int worker,
        int index
    ) =>
        new()
        {
            ["accountIdentifier"] = $"{suffix}-{worker:D2}-{index:D3}",
            ["fiscalYear"] = 2024,
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
            ["accountTypeDescriptor"] = $"{ns}/AccountTypeDescriptor#Revenue",
            ["accountName"] = $"Account {worker}-{index}",
            ["reportingTags"] = new JsonArray(
                new JsonObject
                {
                    ["reportingTagDescriptor"] = $"{ns}/ReportingTagDescriptor#ESSA",
                    ["tagValue"] = $"tag-{worker}-{index}",
                }
            ),
        };

    private async Task SeedFixtureAsync(string ns, long schoolId, string suffix)
    {
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
            ["nameOfInstitution"] = $"DMS-1400 School {suffix}",
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

        var (status, body) = await PostAsync("/data/ed-fi/schools", school);
        status.Should().Be(HttpStatusCode.Created, $"the load needs a school to reference, but got: {body}");
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

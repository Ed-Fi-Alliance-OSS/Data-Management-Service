// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
/// Shared machinery for the concurrency reproductions that drive deliberate lock contention through
/// the real HTTP pipeline. Both run against a leased database configured the way production
/// provisioning configures it, classify every response, and report what the database actually
/// persisted alongside what the engine recorded.
/// </summary>
public abstract class MssqlConcurrentWriteLoadTestBase : MssqlApiIntegrationTestBase
{
    private const string Json = "application/json";

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    /// <summary>
    /// Production provisioning enables RCSI and snapshot isolation; without them the locking
    /// behavior under test would run under an isolation configuration production never uses. The
    /// base class reverts it when the lease is released.
    /// </summary>
    protected override bool MatchProductionWriteIsolation => true;

    /// <summary>
    /// The load deliberately starves the server, so a single request can outlast the default
    /// 100-second client timeout while it waits on locks and replays contention. A client-side
    /// cancellation would be indistinguishable from the failure under test.
    /// </summary>
    protected void AllowRequestsToOutlastDefaultClientTimeout() =>
        Harness.HttpClient.Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reduces a problem-details body to its identifying fields so responses that differ only by
    /// correlationId group together.
    /// </summary>
    protected static string SignatureOf((int Status, string Body) failure)
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

    protected static async Task ReportFailuresAsync(
        string header,
        IReadOnlyCollection<(int Status, string Body)> failures
    )
    {
        await TestContext.Out.WriteLineAsync(header);

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
    /// The system_health ring buffer is server-wide and survives across tests, so only the growth
    /// over a load is attributable to it.
    /// </summary>
    protected static async Task<int> CountDeadlockGraphsAsync()
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

    protected async Task SeedCoreDescriptorsAsync(string ns)
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
    }

    /// <summary>
    /// Asserted rather than fire-and-forget: a descriptor the load's payloads reference but the
    /// seed failed to create makes every request in the load fail validation with a 400, which is
    /// indistinguishable at the assertion from the defect these reproductions exist to detect.
    /// </summary>
    private async Task SeedDescriptorAsync(string endpoint, string namespaceUri, string codeValue)
    {
        var (status, body) = await PostAsync(
            endpoint,
            new JsonObject
            {
                ["namespace"] = namespaceUri,
                ["codeValue"] = codeValue,
                ["shortDescription"] = codeValue,
            }
        );

        status
            .Should()
            .Be(
                HttpStatusCode.Created,
                $"the load's payloads reference {namespaceUri}#{codeValue}, but seeding it returned: {body}"
            );
    }

    protected static JsonObject BuildSchool(string ns, long schoolId, string name, string? documentPath)
    {
        var school = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = $"DMS-1400 School {name}",
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

        AddIdFromPath(school, documentPath);
        return school;
    }

    protected static JsonObject BuildChartOfAccount(
        string ns,
        long schoolId,
        string accountIdentifier,
        int worker,
        int index,
        string? documentPath = null
    )
    {
        var chartOfAccount = new JsonObject
        {
            ["accountIdentifier"] = accountIdentifier,
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

        AddIdFromPath(chartOfAccount, documentPath);
        return chartOfAccount;
    }

    /// <summary>PUT requires the document id in the body; it is the last path segment.</summary>
    private static void AddIdFromPath(JsonObject document, string? documentPath)
    {
        if (documentPath is not null)
        {
            document["id"] = documentPath[(documentPath.LastIndexOf('/') + 1)..];
        }
    }

    protected async Task<(HttpStatusCode Status, string Body)> PostAsync(string endpoint, JsonObject payload)
    {
        var (status, body, _) = await PostWithLocationAsync(endpoint, payload);
        return (status, body);
    }

    protected async Task<(HttpStatusCode Status, string Body, string? Location)> PostWithLocationAsync(
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, Json);
        using HttpResponseMessage response = await Harness.HttpClient.PostAsync(endpoint, content);
        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Headers.Location?.ToString()
        );
    }

    protected async Task<(HttpStatusCode Status, string Body)> PutAsync(string path, JsonObject payload)
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, Json);
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        using HttpResponseMessage response = await Harness.HttpClient.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}

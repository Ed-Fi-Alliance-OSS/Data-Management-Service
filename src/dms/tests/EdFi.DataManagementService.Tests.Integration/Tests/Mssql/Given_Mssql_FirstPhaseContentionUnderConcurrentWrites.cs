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
using FluentAssertions.Execution;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// Targets the write executor's first phase specifically. The first phase captures and locks the
/// target, runs stored authorization, resolves references and hydrates state; a DbException raised
/// there is rethrown unmapped (RelationalWriteFirstPhase rethrows anything that is not an
/// authorization failure, and DefaultRelationalWriteExecutor rethrows it again because no
/// executionRequest is resolved yet), unlike a second-phase failure which maps to a write-conflict
/// result. This drives contention onto first-phase statements by mixing updates of a shared
/// referenced document with inserts that resolve a reference to it.
/// </summary>
public sealed class Given_Mssql_FirstPhaseContentionUnderConcurrentWrites : MssqlApiIntegrationTestBase
{
    private const string Json = "application/json";

    // Overridable so the contention shape can be tuned without a rebuild: a deep queue on one hot
    // row produces lock-wait timeouts, while a shallow one lets genuine cycles form.
    private static readonly int UpdateWorkers = Tunable("DMS1400_UPDATE_WORKERS", 6);
    private static readonly int InsertWorkers = Tunable("DMS1400_INSERT_WORKERS", 6);
    private static readonly int ChildUpdateWorkers = Tunable("DMS1400_CHILD_UPDATE_WORKERS", 4);
    private static readonly int Iterations = Tunable("DMS1400_ITERATIONS", 30);
    private static readonly int SharedChildCount = Tunable("DMS1400_SHARED_CHILDREN", 4);

    private static int Tunable(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    [Explicit("Concurrency load reproduction; drives a deliberate deadlock storm.")]
    public async Task It_never_reports_a_first_phase_database_failure_as_a_client_error()
    {
        await MatchProductionIsolationAsync();

        // The load deliberately starves the server, so a single request can outlast the default
        // 100-second client timeout while it waits on locks and replays deadlocks. A client-side
        // cancellation would be indistinguishable from the failure under test.
        Harness.HttpClient.Timeout = TimeSpan.FromMinutes(10);

        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string ns = $"uri://ed-fi.org/Dms1400First/{suffix}";

        await SeedDescriptorsAsync(ns);
        string schoolPath = await CreateSchoolAsync(ns, schoolId, suffix);
        string[] childPaths = await CreateSharedChildrenAsync(ns, schoolId, suffix);

        ConcurrentBag<(int Status, string Body)> responses = [];

        var updateSchool = Enumerable
            .Range(0, UpdateWorkers)
            .Select(worker =>
                Task.Run(async () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        responses.Add(
                            Tuple(
                                await PutAsync(
                                    schoolPath,
                                    BuildSchool(ns, schoolId, $"{suffix} rev {worker}-{i}", schoolPath)
                                )
                            )
                        );
                    }
                })
            );

        var insertChildren = Enumerable
            .Range(0, InsertWorkers)
            .Select(worker =>
                Task.Run(async () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        responses.Add(
                            Tuple(
                                await PostAsync(
                                    "/data/ed-fi/chartOfAccounts",
                                    BuildChartOfAccount(
                                        ns,
                                        schoolId,
                                        $"{suffix}-i{worker:D2}-{i:D3}",
                                        worker,
                                        i
                                    )
                                )
                            )
                        );
                    }
                })
            );

        var updateChildren = Enumerable
            .Range(0, ChildUpdateWorkers)
            .Select(worker =>
                Task.Run(async () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        string path = childPaths[(worker + i) % childPaths.Length];
                        responses.Add(
                            Tuple(
                                await PutAsync(
                                    path,
                                    BuildChartOfAccount(
                                        ns,
                                        schoolId,
                                        $"{suffix}-shared-{(worker + i) % childPaths.Length:D2}",
                                        worker,
                                        i,
                                        path
                                    )
                                )
                            )
                        );
                    }
                })
            );

        await Task.WhenAll(updateSchool.Concat(insertChildren).Concat(updateChildren));

        var failures = responses.Where(response => response.Status is not (200 or 201 or 204)).ToArray();

        await ReportAsync(responses.Count, failures);

        var engineTextLeaks = failures
            .Where(failure =>
                failure.Body.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
                || failure.Body.Contains("Rerun the transaction", StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        var clientErrors = failures.Where(failure => failure.Status is >= 400 and < 500).ToArray();

        using (new AssertionScope())
        {
            engineTextLeaks
                .Should()
                .BeEmpty("the raw database error text must never reach the client response body");

            clientErrors
                .Should()
                .BeEmpty(
                    "a transient database failure must not be reported as a client error, but "
                        + $"{clientErrors.Length} of {responses.Count} attempts returned 4xx: "
                        + string.Join(
                            " | ",
                            clientErrors.Select(SignatureOf).Distinct(StringComparer.Ordinal)
                        )
                );
        }
    }

    private static (int Status, string Body) Tuple((HttpStatusCode Status, string Body) response) =>
        ((int)response.Status, response.Body);

    private static async Task ReportAsync(
        int attempts,
        IReadOnlyCollection<(int Status, string Body)> failures
    )
    {
        await TestContext.Out.WriteLineAsync($"=== {attempts} attempts, {failures.Count} failed ===");

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

    private async Task MatchProductionIsolationAsync()
    {
        string quoted = MssqlTestDatabaseHelper.QuoteIdentifier(Harness.DbConnection.Database);

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"ALTER DATABASE {quoted} SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;"
        );
        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"ALTER DATABASE {quoted} SET ALLOW_SNAPSHOT_ISOLATION ON;"
        );
    }

    private async Task<string> CreateSchoolAsync(string ns, long schoolId, string suffix)
    {
        var (status, body, location) = await PostWithLocationAsync(
            "/data/ed-fi/schools",
            BuildSchool(ns, schoolId, suffix, documentPath: null)
        );

        status.Should().Be(HttpStatusCode.Created, body);
        return location!;
    }

    private async Task<string[]> CreateSharedChildrenAsync(string ns, long schoolId, string suffix)
    {
        List<string> paths = [];

        for (int i = 0; i < SharedChildCount; i++)
        {
            var (status, body, location) = await PostWithLocationAsync(
                "/data/ed-fi/chartOfAccounts",
                BuildChartOfAccount(ns, schoolId, $"{suffix}-shared-{i:D2}", 0, i)
            );

            status.Should().Be(HttpStatusCode.Created, body);
            paths.Add(location!);
        }

        return [.. paths];
    }

    private static JsonObject BuildSchool(string ns, long schoolId, string name, string? documentPath)
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

    private static JsonObject BuildChartOfAccount(
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

    private async Task SeedDescriptorsAsync(string ns)
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
        var (status, body, _) = await PostWithLocationAsync(endpoint, payload);
        return (status, body);
    }

    private async Task<(HttpStatusCode Status, string Body, string? Location)> PostWithLocationAsync(
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

    private async Task<(HttpStatusCode Status, string Body)> PutAsync(string path, JsonObject payload)
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, Json);
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        using HttpResponseMessage response = await Harness.HttpClient.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}

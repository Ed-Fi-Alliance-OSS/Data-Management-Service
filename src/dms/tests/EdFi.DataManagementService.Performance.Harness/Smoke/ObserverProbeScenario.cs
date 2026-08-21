// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// The hard instrumentation checkpoint: proves, per provider, that the query recorder yields
/// the compiled page-selection statement with its bound values and that the driver-level
/// command observer sees the one real hydration batch a traditional GET-many executes. Runs
/// against a live database; no evidence run is acceptable until this passes on both providers.
/// </summary>
internal static class ObserverProbeScenario
{
    public static async Task RunAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        await PostProbeStudentAsync(harness, "probe-000000001");
        await PostProbeStudentAsync(harness, "probe-000000002");

        using DriverCommandObserver observer = DriverCommandObserver.Start(provider);
        int observerBaseline = observer.Commands.Count;
        int recorderBaseline = harness.QueryRecorder!.HydrationKeysets.Count;

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            PerfFixtureDefinition.ResourceEndpoint + "?limit=1&offset=1"
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode.Parse(body)!.AsArray().Should().HaveCount(1);

        // Driver observer: the request must have executed exactly one database command, and
        // its text must be the hydration batch (recognizable by the keyset temp table).
        List<ObservedDbCommand> observed = [.. observer.Commands.Skip(observerBaseline)];
        observed.Should().ContainSingle("a traditional GET-many must execute exactly one database command");
        string expectedBatchMarker = provider == PerfProvider.Postgresql ? "page_ids" : "[#page]";
        observed[0].CommandText.Should().Contain(expectedBatchMarker);
        observed[0].ElapsedMs.Should().BeGreaterThan(0);

        // Query recorder: the same request must have produced exactly one Query keyset whose
        // plan carries the page-selection SQL and the bound offset/limit values.
        List<PageKeysetSpec> newKeysets = [.. harness.QueryRecorder!.HydrationKeysets.Skip(recorderBaseline)];
        PageSelectionQueryCapture capture = PageSelectionCapture.ExtractSingleQuery(newKeysets);
        capture.PageDocumentIdSql.Should().Contain("ORDER BY");
        capture.PageDocumentIdSql.Should().Contain("DocumentId");
        Convert.ToInt64(capture.ParameterValues["offset"], CultureInfo.InvariantCulture).Should().Be(1);
        Convert.ToInt64(capture.ParameterValues["limit"], CultureInfo.InvariantCulture).Should().Be(1);
        capture.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");

        // The observed batch embeds the compiled page-selection statement's shape.
        observed[0].CommandText.Should().Contain("ORDER BY");
    }

    private static async Task PostProbeStudentAsync(ApiIntegrationHarness harness, string studentUniqueId)
    {
        JsonObject payload = new()
        {
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = PerfFixtureDefinition.FirstName,
            ["lastSurname"] = PerfFixtureDefinition.LastSurname,
            ["birthDate"] = PerfFixtureDefinition.BirthDateIso,
        };
        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage created = await harness.HttpClient.PostAsync(
            PerfFixtureDefinition.ResourceEndpoint,
            content
        );
        string body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }
}

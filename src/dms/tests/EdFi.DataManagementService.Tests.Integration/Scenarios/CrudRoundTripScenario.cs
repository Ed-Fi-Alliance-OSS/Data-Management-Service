// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Basic CRUD smoke: POST a Student then GET-by-id from the Location header,
/// asserting the request fields round-trip through the real DMS HTTP pipeline
/// against the leased database. Hard-coded for the ProfileRootOnlyMerge fixture's
/// Student shape: project endpoint <c>ed-fi</c>, resource <c>students</c>, required
/// identity <c>studentUniqueId</c>, required non-identity <c>firstName</c>.
/// </summary>
internal static class CrudRoundTripScenario
{
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StudentUniqueId = "smoke-001";
    private const string FirstName = "Ada";

    public static async Task It_creates_and_reads_a_student(ApiIntegrationHarness harness)
    {
        var payload = new JsonObject { ["studentUniqueId"] = StudentUniqueId, ["firstName"] = FirstName };

        using var createContent = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage createResponse = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            createContent
        );
        string createBody = await createResponse.Content.ReadAsStringAsync();

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, createBody);
        createResponse.Headers.Location.Should().NotBeNull();

        string locationPath = createResponse.Headers.Location!.IsAbsoluteUri
            ? createResponse.Headers.Location!.AbsolutePath
            : createResponse.Headers.Location!.OriginalString;

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);

        JsonNode? returnedNode = JsonNode.Parse(getBody);
        returnedNode.Should().NotBeNull("GET response must be a JSON document");
        JsonObject returned = returnedNode!.AsObject();
        returned["studentUniqueId"]!.GetValue<string>().Should().Be(StudentUniqueId);
        returned["firstName"]!.GetValue<string>().Should().Be(FirstName);
    }
}

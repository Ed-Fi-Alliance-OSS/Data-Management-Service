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
/// Composed proof that a parameter an operation does not use changes nothing about the result it
/// serves, and is named in the warning. Every request here is compared with the same request without
/// the ignored parameters, over a nonempty result, so an ignored parameter that did filter or page would
/// show up as a different body.
/// </summary>
internal static class IgnoredQueryParameterScenario
{
    private const string StudentsEndpoint = DerivativeRoutingSupport.StudentsEndpoint;
    private const string DeletesEndpoint = $"{StudentsEndpoint}/deletes";
    private const string KeyChangesEndpoint = $"{StudentsEndpoint}/keyChanges";
    private const string DescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";

    private const int TrackedDeleteCount = 3;
    private const int TrackedKeyChangeCount = 2;

    /// <summary>
    /// Resource filters a Change Query does not bind, one of them with a value no date filter would
    /// accept, plus a name no operation defines. The cursor parameters are not among them: a Change
    /// Query rejects those by name.
    /// </summary>
    private const string IgnoredOnChangeQueries =
        "studentUniqueId=no-such-student&birthDate=notadate&notAParameter=1";

    private static readonly string[] IgnoredOnChangeQueriesNames =
    [
        "studentUniqueId",
        "birthDate",
        "notAParameter",
    ];

    /// <summary>
    /// Seeds tracked deletes and tracked key changes on Students, which also leaves the renamed Students
    /// on the collection, and one descriptor.
    /// </summary>
    public static async Task SeedAsync(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        await DerivativeTrackedChangeScenario.SeedTrackedDeletesAsync(harness, TrackedDeleteCount, "ignored");
        await DerivativeTrackedChangeScenario.SeedTrackedKeyChangesAsync(
            harness,
            TrackedKeyChangeCount,
            "ignored"
        );

        JsonObject descriptor = new()
        {
            ["namespace"] = "uri://ed-fi.org/GradeLevelDescriptor",
            ["codeValue"] = "Ignored Parameter Grade",
            ["shortDescription"] = "Ignored Parameter Grade",
        };
        using var content = new StringContent(descriptor.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage created = await harness.HttpClient.PostAsync(DescriptorsEndpoint, content);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    public static Task It_serves_deletes_unchanged_by_ignored_filters_and_unknown_names(
        ApiIntegrationHarness harness
    ) =>
        AssertIgnoredAsync(
            harness,
            DeletesEndpoint,
            IgnoredOnChangeQueries,
            TrackedDeleteCount,
            IgnoredOnChangeQueriesNames
        );

    public static Task It_serves_key_changes_unchanged_by_ignored_filters_and_unknown_names(
        ApiIntegrationHarness harness
    ) =>
        AssertIgnoredAsync(
            harness,
            KeyChangesEndpoint,
            IgnoredOnChangeQueries,
            TrackedKeyChangeCount,
            IgnoredOnChangeQueriesNames
        );

    /// <summary>
    /// limit and offset are still consumed alongside the ignored parameters: the page served is the
    /// second item of the unpaged result.
    /// </summary>
    public static async Task It_still_pages_deletes_by_limit_and_offset(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        JsonArray baseline = await GetArrayAsync(harness, DeletesEndpoint);
        baseline.Count.Should().BeGreaterThanOrEqualTo(TrackedDeleteCount);

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{DeletesEndpoint}?limit=1&offset=1&{IgnoredOnChangeQueries}"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode
            .DeepEquals(JsonNode.Parse(body), new JsonArray(baseline[1]!.DeepClone()))
            .Should()
            .BeTrue($"the page should be the second unpaged item, but was {body}");
        IgnoredParameterWarningAssertions.AssertWarnsOf(response, IgnoredOnChangeQueriesNames);
    }

    /// <summary>
    /// The ticket's own example: a mistyped filter is dropped, so the collection is served whole, and the
    /// warning is what tells the client.
    /// </summary>
    public static Task It_serves_the_whole_collection_for_a_mistyped_filter(ApiIntegrationHarness harness) =>
        AssertIgnoredAsync(
            harness,
            StudentsEndpoint,
            "studentUniqueld=no-such-student",
            TrackedKeyChangeCount,
            ["studentUniqueld"]
        );

    public static Task It_serves_descriptors_unchanged_by_an_unknown_parameter(
        ApiIntegrationHarness harness
    ) => AssertIgnoredAsync(harness, DescriptorsEndpoint, "useJoinAuth=true", 1, ["useJoinAuth"]);

    private static async Task AssertIgnoredAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string ignoredQuery,
        int minimumBaselineCount,
        string[] expectedIgnoredNames
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        JsonArray baseline = await GetArrayAsync(harness, endpoint);
        baseline
            .Count.Should()
            .BeGreaterThanOrEqualTo(minimumBaselineCount, "the comparison needs a nonempty result");

        using HttpResponseMessage response = await harness.HttpClient.GetAsync($"{endpoint}?{ignoredQuery}");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode
            .DeepEquals(JsonNode.Parse(body), baseline)
            .Should()
            .BeTrue($"ignored parameters must not change the result, but it was {body}");
        IgnoredParameterWarningAssertions.AssertWarnsOf(response, expectedIgnoredNames);
    }

    private static async Task<JsonArray> GetArrayAsync(ApiIntegrationHarness harness, string endpoint)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response
            .Headers.Contains(Core.Middleware.IgnoredQueryParameterWarning.HeaderName)
            .Should()
            .BeFalse("a request that ignores nothing carries no warning");

        return JsonNode.Parse(body)!.AsArray();
    }
}

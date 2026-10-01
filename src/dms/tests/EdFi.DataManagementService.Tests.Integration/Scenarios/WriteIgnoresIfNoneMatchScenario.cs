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
/// Write-side <c>If-None-Match</c> is ignored (DMS-1576): POSTs, PUTs, and DELETEs a Student against the
/// real DMS HTTP pipeline with various <c>If-None-Match</c> request headers, asserting that the header
/// has no effect on any write -- it is a conditional-read (GET-only) validator, matching the ODS/API.
/// Hard-coded for the ProfileRootOnlyMerge fixture's Student shape: project endpoint <c>ed-fi</c>,
/// resource <c>students</c>, required identity <c>studentUniqueId</c>, required non-identity
/// <c>firstName</c>. Each scenario uses a fresh, per-call unique <c>studentUniqueId</c> to avoid
/// cross-test collisions within a shared leased database.
/// </summary>
internal static class WriteIgnoresIfNoneMatchScenario
{
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string IfNoneMatchHeaderName = "If-None-Match";
    private const string IfMatchHeaderName = "If-Match";

    // A syntactically valid but never-created resource id; RFC 4122 version 4 shaped so it survives
    // any UUID-format validation while remaining guaranteed absent from a freshly leased database.
    private const string NonExistentResourceId = "00000000-0000-4000-a000-000000000000";

    public static async Task It_ignores_a_wildcard_if_none_match_on_a_post_of_a_new_document(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("ins-wc");

        using var request = new HttpRequestMessage(HttpMethod.Post, StudentsEndpoint)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada"),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                $"If-None-Match: * is ignored on a POST, so a brand-new resource still creates normally. Body: {body}"
            );
        response.Headers.Location.Should().NotBeNull();
        response.TryReadRawEtag(out _).Should().BeTrue("a successful POST create must emit an ETag header");
    }

    public static async Task It_ignores_a_wildcard_if_none_match_on_a_post_to_an_existing_document(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pup-wc");
        (string locationPath, _) = await CreateStudentAsync(harness, studentUniqueId, "Ada");

        using var request = new HttpRequestMessage(HttpMethod.Post, StudentsEndpoint)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-changed"),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.OK,
                $"If-None-Match: * is ignored on a POST, so an upsert to an existing document still 200s. Body: {body}"
            );

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonNode? returnedNode = JsonNode.Parse(getBody);
        returnedNode.Should().NotBeNull("GET response must be a JSON document");
        returnedNode!.AsObject()["firstName"]!
            .GetValue<string>()
            .Should()
            .Be("Ada-changed", $"the upsert must have taken effect. Body: {getBody}");
    }

    public static async Task It_ignores_a_matching_specific_if_none_match_on_a_post_to_an_existing_document(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pup-sp");
        (string locationPath, string etag) = await CreateStudentAsync(harness, studentUniqueId, "Ada");

        using var request = new HttpRequestMessage(HttpMethod.Post, StudentsEndpoint)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-changed-again"),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, $"\"{etag}\"");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.OK,
                $"a specific If-None-Match tag that matches the current ETag is ignored on a POST, so the upsert still 200s. Body: {body}"
            );

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonNode? returnedNode = JsonNode.Parse(getBody);
        returnedNode.Should().NotBeNull("GET response must be a JSON document");
        returnedNode!.AsObject()["firstName"]!
            .GetValue<string>()
            .Should()
            .Be("Ada-changed-again", $"the upsert must have taken effect. Body: {getBody}");
    }

    public static async Task It_ignores_a_wildcard_if_none_match_on_a_put_to_an_existing_document(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pwe-wc");
        (string locationPath, _) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"If-None-Match: * is ignored on a PUT, so an update to an existing target still succeeds. Body: {body}"
            );

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonNode? returnedNode = JsonNode.Parse(getBody);
        returnedNode.Should().NotBeNull("GET response must be a JSON document");
        returnedNode!.AsObject()["firstName"]!
            .GetValue<string>()
            .Should()
            .Be("Ada-renamed", $"the PUT must have taken effect. Body: {getBody}");
    }

    public static async Task It_returns_not_found_for_a_put_to_a_missing_target_under_a_wildcard_if_none_match(
        ApiIntegrationHarness harness
    )
    {
        string putPath = $"{StudentsEndpoint}/{NonExistentResourceId}";
        string studentUniqueId = UniqueStudentId("pwm-wc");

        using var request = new HttpRequestMessage(HttpMethod.Put, putPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada", NonExistentResourceId),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NotFound,
                $"If-None-Match: * is ignored on a PUT, so a genuinely missing target still 404s rather than taking any create-guard path. Body: {body}"
            );
    }

    public static async Task It_ignores_a_matching_specific_if_none_match_on_a_put_to_an_existing_document(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pws-sp");
        (string locationPath, string etag) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, $"\"{etag}\"");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"a specific If-None-Match tag that matches the current ETag is ignored on a PUT, so the update still succeeds. Body: {body}"
            );

        await AssertFirstNameAsync(harness, locationPath, "Ada-renamed", "the PUT must have taken effect.");
    }

    public static async Task It_ignores_a_matching_tag_in_an_if_none_match_list_on_a_put_to_an_existing_document(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pws-list");
        (string locationPath, string etag) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        // Whether or not a list member matches the current representation no longer matters for a
        // write: If-None-Match is ignored either way. The matching tag is placed among stale tags to
        // prove list parsing doesn't somehow resurrect the old create-guard behavior for this shape.
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, $"\"1-00000000.j._.n.i\", \"{etag}\"");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"a list containing the current tag is ignored on a PUT, so the update still succeeds. Body: {body}"
            );

        await AssertFirstNameAsync(harness, locationPath, "Ada-renamed", "the PUT must have taken effect.");
    }

    public static async Task It_ignores_a_wildcard_if_none_match_on_a_delete_of_an_existing_document(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("del-wc");
        (string locationPath, _) = await CreateStudentAsync(harness, studentUniqueId, "Ada");

        using var request = new HttpRequestMessage(HttpMethod.Delete, locationPath);
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"If-None-Match: * is ignored on a DELETE, so an existing target still deletes normally. Body: {body}"
            );

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        getResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound, "the ignored If-None-Match must not have prevented the delete");
    }

    public static async Task It_honors_a_matching_if_match_and_ignores_if_none_match_when_both_are_present(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("ifm-ok");
        (string locationPath, string etag) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        request.Headers.TryAddWithoutValidation(IfMatchHeaderName, $"\"{etag}\"");
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"a matching If-Match passes and the accompanying If-None-Match: * is ignored. Body: {body}"
            );

        await AssertFirstNameAsync(harness, locationPath, "Ada-renamed", "the PUT must have taken effect.");
    }

    public static async Task It_rejects_a_stale_if_match_even_when_if_none_match_is_present(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("ifm-stl");
        (string locationPath, _) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        // If-Match is stale, so it must still decide the outcome; an ignored If-None-Match: * must not
        // mask the failure. This is the case that fails if If-Match were also ignored.
        request.Headers.TryAddWithoutValidation(IfMatchHeaderName, "\"1-00000000.j._.n.i\"");
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.PreconditionFailed,
                $"a stale If-Match must still fail the PUT when If-None-Match is also present. Body: {body}"
            );

        await AssertFirstNameAsync(
            harness,
            locationPath,
            "Ada",
            "the rejected PUT must not have changed the document."
        );
    }

    private static async Task<(string locationPath, string etag)> CreateStudentAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        string firstName
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            CreateStudentContent(studentUniqueId, firstName)
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.TryReadRawEtag(out string etag).Should().BeTrue("the initial POST must emit an ETag header");

        string locationPath = response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location!.AbsolutePath
            : response.Headers.Location!.OriginalString;

        return (locationPath, etag);
    }

    private static async Task AssertFirstNameAsync(
        ApiIntegrationHarness harness,
        string locationPath,
        string expectedFirstName,
        string because
    )
    {
        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonNode? returnedNode = JsonNode.Parse(getBody);
        returnedNode.Should().NotBeNull("GET response must be a JSON document");
        returnedNode!.AsObject()["firstName"]!
            .GetValue<string>()
            .Should()
            .Be(expectedFirstName, $"{because} Body: {getBody}");
    }

    private static StringContent CreateStudentContent(
        string studentUniqueId,
        string firstName,
        string? id = null
    )
    {
        var payload = new JsonObject { ["studentUniqueId"] = studentUniqueId, ["firstName"] = firstName };
        if (id is not null)
        {
            payload["id"] = id;
        }
        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private static string UniqueStudentId(string scenario) => $"wim-{scenario}-{Guid.NewGuid():N}"[..24];

    private static string GetResourceId(string locationPath) =>
        locationPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
}

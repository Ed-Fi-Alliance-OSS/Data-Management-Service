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
/// Conditional GET support for <c>If-None-Match</c> on GET-by-id: POSTs a Student, then
/// re-GETs it from the Location header with various <c>If-None-Match</c> header values,
/// asserting the <c>304 Not Modified</c> / <c>200 OK</c> outcome. The 304 decision lives in
/// the HTTP pipeline (Core <c>GetByIdHandler</c>), not the backend repository, so these
/// scenarios exercise the real in-process DMS HTTP pipeline rather than a repository directly.
/// Hard-coded for the ProfileRootOnlyMerge fixture's Student shape: project endpoint
/// <c>ed-fi</c>, resource <c>students</c>, required identity <c>studentUniqueId</c>,
/// required non-identity <c>firstName</c>.
/// </summary>
internal static class ConditionalGetIfNoneMatchScenario
{
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string IfNoneMatchHeaderName = "If-None-Match";

    public static async Task It_returns_304_for_a_matching_quoted_if_none_match(ApiIntegrationHarness harness)
    {
        (string locationPath, string etag) = await CreateStudentAsync(harness, "cgm-quoted-001");

        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, $"\"{etag}\"");

        using HttpResponseMessage getResponse = await harness.HttpClient.SendAsync(request);

        getResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getBody.Should().BeEmpty("a 304 response must not carry a body");
        getResponse.TryReadRawEtag(out _).Should().BeTrue("a 304 response must still carry the ETag header");
    }

    public static async Task It_returns_304_for_a_matching_unquoted_if_none_match(
        ApiIntegrationHarness harness
    )
    {
        (string locationPath, string etag) = await CreateStudentAsync(harness, "cgm-unquoted-001");

        // Unquoted (no surrounding double quotes) -- proves ODS-6853 legacy compatibility with
        // non-conforming clients that send a bare opaque value instead of an RFC 9110 §8.8.3 quoted string.
        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, etag);

        using HttpResponseMessage getResponse = await harness.HttpClient.SendAsync(request);

        getResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    public static async Task It_returns_304_for_a_wildcard_if_none_match(ApiIntegrationHarness harness)
    {
        (string locationPath, _) = await CreateStudentAsync(harness, "cgm-wildcard-001");

        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage getResponse = await harness.HttpClient.SendAsync(request);

        getResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.NotModified, "the resource exists, so If-None-Match: * must be satisfied");
    }

    public static async Task It_returns_200_for_a_stale_if_none_match(ApiIntegrationHarness harness)
    {
        (string locationPath, _) = await CreateStudentAsync(harness, "cgm-stale-001");

        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "\"1-00000000.j._.n\"");

        using HttpResponseMessage getResponse = await harness.HttpClient.SendAsync(request);
        string getBody = await getResponse.Content.ReadAsStringAsync();

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonObject returned = JsonNode.Parse(getBody)!.AsObject();
        returned["studentUniqueId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    public static async Task It_returns_200_when_only_the_variant_key_differs(ApiIntegrationHarness harness)
    {
        (string locationPath, string etag) = await CreateStudentAsync(harness, "cgm-variant-001");

        // Flip only the final character of the etag tail (the variantKey's trailing segment, e.g. a
        // link flag such as .l/.n) to a different valid [a-z0-9_] character, leaving the rest of the
        // served etag -- including its ContentVersion -- untouched.
        char lastChar = etag[^1];
        char flippedChar = lastChar == 'n' ? 'l' : 'n';
        string mutatedEtag = string.Concat(etag.AsSpan(0, etag.Length - 1), flippedChar.ToString());

        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, $"\"{mutatedEtag}\"");

        using HttpResponseMessage getResponse = await harness.HttpClient.SendAsync(request);
        string getBody = await getResponse.Content.ReadAsStringAsync();

        // This proves the read path compares the FULL served tag (representation-sensitive), not the
        // write-side state-significant projection (EtagMatchProjection), which would ignore the
        // variantKey tail and wrongly yield 304 here.
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonObject returned = JsonNode.Parse(getBody)!.AsObject();
        returned["studentUniqueId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    public static async Task It_returns_304_when_a_matching_tag_is_in_a_list(ApiIntegrationHarness harness)
    {
        (string locationPath, string etag) = await CreateStudentAsync(harness, "cgm-list-hit-001");

        // RFC 9110 §13.1.2: If-None-Match may carry a comma-separated list; the precondition is
        // satisfied (304) when ANY member matches the current representation. The matching tag is
        // placed among non-matching (stale) tags to prove list iteration, not just first-element match.
        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.TryAddWithoutValidation(
            IfNoneMatchHeaderName,
            $"\"1-00000000.j._.n\", \"{etag}\", \"2-11111111.j._.n\""
        );

        using HttpResponseMessage getResponse = await harness.HttpClient.SendAsync(request);

        getResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NotModified,
                "a list containing the current tag must 304, even when other list members are stale"
            );
        getResponse.TryReadRawEtag(out _).Should().BeTrue("a 304 response must still carry the ETag header");
    }

    public static async Task It_returns_304_for_a_weak_tag_in_a_list(ApiIntegrationHarness harness)
    {
        (string locationPath, string etag) = await CreateStudentAsync(harness, "cgm-list-weak-001");

        // Weak comparison (RFC 9110 §8.8.3.2): a W/-prefixed member is compared by opaque value only, so
        // W/"<etag>" matches the strong served "<etag>". Combined with the list form here.
        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, $"\"1-00000000.j._.n\", W/\"{etag}\"");

        using HttpResponseMessage getResponse = await harness.HttpClient.SendAsync(request);

        getResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.NotModified, "a weak tag in the list whose value matches must 304");
    }

    public static async Task It_returns_200_when_no_tag_in_a_list_matches(ApiIntegrationHarness harness)
    {
        (string locationPath, _) = await CreateStudentAsync(harness, "cgm-list-miss-001");

        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.TryAddWithoutValidation(
            IfNoneMatchHeaderName,
            "\"1-00000000.j._.n\", \"2-11111111.j._.n\""
        );

        using HttpResponseMessage getResponse = await harness.HttpClient.SendAsync(request);
        string getBody = await getResponse.Content.ReadAsStringAsync();

        getResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.OK,
                $"a list in which no member matches must return the full 200 body. {getBody}"
            );
        JsonObject returned = JsonNode.Parse(getBody)!.AsObject();
        returned["studentUniqueId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    private static async Task<(string locationPath, string etag)> CreateStudentAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId
    )
    {
        var payload = new JsonObject { ["studentUniqueId"] = studentUniqueId, ["firstName"] = "Ada" };
        using var createContent = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage createResponse = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            createContent
        );
        createResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        createResponse
            .TryReadRawEtag(out string etag)
            .Should()
            .BeTrue("the initial POST must emit an ETag header");

        string locationPath = createResponse.Headers.Location!.IsAbsoluteUri
            ? createResponse.Headers.Location!.AbsolutePath
            : createResponse.Headers.Location!.OriginalString;

        return (locationPath, etag);
    }
}

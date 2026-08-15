// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Composed proof of which operations page by cursor, and of where a cursor request that is accepted
/// actually lands.
///
/// <para>
/// Whether the cursor parameters are recognized at all is a property of pipeline composition: the
/// live collection read recognizes <c>pageToken</c> and <c>pageSize</c>, and the Change Query
/// endpoints do not. Middleware tests construct the step they exercise and choose recognition
/// themselves, so they cannot show which operation was composed with which choice, and a handler test
/// that supplies its own repository cannot show what the real read path answers. Each request below
/// observes the assembled host instead.
/// </para>
///
/// <para>
/// The Change Query requests seed nothing: they pass through query validation, which excludes the
/// cursor parameter names, and are answered by the Change Query validation step that follows it,
/// before any candidate selection. The accepted cursor request seeds one school through the same
/// pipeline, because what it pins is the page the read path actually serves. The leased database is
/// required either way, since these pipelines resolve the database fingerprint, resource key seed, and
/// mapping set before query validation runs.
/// </para>
/// </summary>
internal static class CursorPagingOperationScopeScenario
{
    private const string SchoolsEndpoint = "/data/ed-fi/schools";

    private const string BadRequestType = "urn:ed-fi:api:bad-request";

    /// <summary>
    /// Not base64url, so cursor validation would reject it as an undecodable token. An operation that
    /// does not page by cursor must instead report the parameter name, which is what distinguishes the
    /// two answers.
    /// </summary>
    private const string UndecodablePageToken = "!!!";

    /// <summary>
    /// A well-formed page size, within any configured maximum, so nothing about the value is at issue.
    /// </summary>
    private const string WellFormedPageSize = "25";

    /// <summary>
    /// A deletes request carrying <c>pageToken</c> is told the field is not valid for a Change Query
    /// endpoint. The token is deliberately one cursor validation would reject, so the answer separates
    /// an operation that does not recognize the name from one that recognizes it and found the value
    /// malformed.
    /// </summary>
    public static async Task It_rejects_a_page_token_on_a_deletes_request(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var response = await harness.HttpClient.GetAsync(
            $"{SchoolsEndpoint}/deletes?pageToken={UndecodablePageToken}"
        );

        await AssertSingleBadRequestError(
            response,
            "The query field 'pageToken' is not valid for this Change Query endpoint."
        );
    }

    /// <summary>
    /// A keyChanges request carrying <c>pageSize</c> is told the field is not valid for a Change Query
    /// endpoint. The parameter is reported rather than ignored, so a client cannot believe it asked
    /// for a page size that was honored.
    /// </summary>
    public static async Task It_rejects_a_page_size_on_a_key_changes_request(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var response = await harness.HttpClient.GetAsync(
            $"{SchoolsEndpoint}/keyChanges?pageSize={WellFormedPageSize}"
        );

        await AssertSingleBadRequestError(
            response,
            "The query field 'pageSize' is not valid for this Change Query endpoint."
        );
    }

    /// <summary>
    /// A collection read carrying a decodable token and a page size is not rejected during query
    /// validation: it reaches the read path, which selects the page and hands back a continuation. This
    /// is where the accepted cursor request lands, observed on the assembled host rather than inferred
    /// from a handler double.
    /// </summary>
    public static async Task It_carries_an_accepted_cursor_request_to_the_read_path(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        await SeedSchoolAsync(harness);

        // Encoded by the codec that decodes it, so the token is decodable by construction rather than
        // by a transcription of the transport encoding happening to stay in step with it. The upper
        // bound is open, so the seeded school is inside the range whatever identity it received.
        string pageToken = PageTokenCodec.Encode(CursorRange.From(1));

        var response = await harness.HttpClient.GetAsync(
            $"{SchoolsEndpoint}?pageToken={Uri.EscapeDataString(pageToken)}&pageSize={WellFormedPageSize}"
        );

        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        JsonNode.Parse(content)!.AsArray().Should().NotBeEmpty("the seeded school is inside the range");

        response
            .Headers.TryGetValues("Next-Page-Token", out var nextPageTokenValues)
            .Should()
            .BeTrue("a page that selected keys must carry a continuation");

        PageTokenCodec
            .TryDecode(nextPageTokenValues!.Single(), out var nextRange)
            .Should()
            .BeTrue("the emitted continuation must decode through the codec that produced it");

        // The request carried no upper bound, so the walk it starts is unbounded above, and it resumes
        // after the keys this page already delivered.
        nextRange!.InclusiveMaximum.Should().Be(long.MaxValue);
        nextRange.InclusiveMinimum.Should().BePositive();
    }

    /// <summary>
    /// Seeds one school through the same HTTP pipeline the read under test uses, with per-run unique
    /// values so the scenario stays isolated from anything else bound to this fixture.
    /// </summary>
    private static async Task SeedSchoolAsync(ApiIntegrationHarness harness)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string namespaceUri = $"uri://ed-fi.org/CursorPaging/{suffix}";
        long schoolId = 1_386_000L + Math.Abs(suffix.GetHashCode(StringComparison.Ordinal) % 100_000);

        await SeedDescriptorAsync(
            harness,
            "/data/ed-fi/educationOrganizationCategoryDescriptors",
            $"{namespaceUri}/EducationOrganizationCategoryDescriptor",
            "School"
        );
        await SeedDescriptorAsync(
            harness,
            "/data/ed-fi/gradeLevelDescriptors",
            $"{namespaceUri}/GradeLevelDescriptor",
            "Tenth grade"
        );

        var schoolPayload = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = $"CursorPaging School {suffix}",
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        $"{namespaceUri}/EducationOrganizationCategoryDescriptor#School",
                }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject
                {
                    ["gradeLevelDescriptor"] = $"{namespaceUri}/GradeLevelDescriptor#Tenth grade",
                }
            ),
        };

        await PostJsonAsync(harness, SchoolsEndpoint, schoolPayload);
    }

    private static async Task SeedDescriptorAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string namespaceUri,
        string codeValue
    ) =>
        await PostJsonAsync(
            harness,
            endpoint,
            new JsonObject
            {
                ["namespace"] = namespaceUri,
                ["codeValue"] = codeValue,
                ["shortDescription"] = codeValue,
            }
        );

    private static async Task PostJsonAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {endpoint} body: {body}");
    }

    /// <summary>
    /// Asserts the generic bad-request shell, which is the shell an unrecognized query field is
    /// reported in. A cursor request that was recognized and rejected would answer with the parameter
    /// validation shell instead, so the shell is part of what is being asserted.
    /// </summary>
    private static async Task AssertSingleBadRequestError(HttpResponseMessage response, string expectedError)
    {
        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);

        JsonNode body = JsonNode.Parse(content)!;

        // The reported message first, so a wrong answer names itself rather than being described only
        // by the shell it arrived in.
        body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).Should().Equal(expectedError);
        body["detail"]!
            .GetValue<string>()
            .Should()
            .Be("The request could not be processed. See 'errors' for details.");
        body["type"]!.GetValue<string>().Should().Be(BadRequestType);
        body["title"]!.GetValue<string>().Should().Be("Bad Request");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        body["validationErrors"]!.AsObject().Should().BeEmpty();
    }
}

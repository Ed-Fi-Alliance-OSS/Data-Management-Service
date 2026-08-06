// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
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
/// No documents are seeded. The Change Query requests pass through query validation, which excludes
/// the cursor parameter names, and are answered by the Change Query validation step that follows it;
/// the accepted cursor request is answered by the read path's cursor guard. All of that is before any
/// candidate selection. The leased database is still required because these pipelines resolve the
/// database fingerprint, resource key seed, and mapping set before query validation runs.
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
    /// validation. It passes through to the read path, which does not yet select cursor pages and says
    /// so. HTTP 501 with that message is the current boundary of cursor paging support, and the
    /// request having reached it is the fact this pins: the request was accepted as a cursor request,
    /// carried typed cursor paging to the repository contract, and was answered there.
    /// </summary>
    public static async Task It_carries_an_accepted_cursor_request_to_the_read_path(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        // Encoded by the codec that decodes it, so the token is decodable by construction rather than
        // by a transcription of the transport encoding happening to stay in step with it.
        string pageToken = PageTokenCodec.Encode(new CursorRange(1, 100));

        var response = await harness.HttpClient.GetAsync(
            $"{SchoolsEndpoint}?pageToken={pageToken}&pageSize={WellFormedPageSize}"
        );

        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, content);

        JsonNode body = JsonNode.Parse(content)!;

        body["error"]!
            .GetValue<string>()
            .Should()
            .Be("Cursor paging is not yet supported for relational queries.");
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
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

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The general bad-request ProblemDetails shell, which is what the unknown-query-field rule answers
/// with. Separate from the parameter-validation shell on purpose: several of the recorded partition
/// ordering consequences turn on <em>which</em> of the two shells answers a query string that is faulty
/// in more than one way, so a test that could not tell them apart would pass either way.
/// </summary>
internal static class BadRequestProblemDetails
{
    internal const string ProblemType = "urn:ed-fi:api:bad-request";
    internal const string ProblemTitle = "Bad Request";
    internal const string ProblemDetail = "The request could not be processed. See 'errors' for details.";

    private const string StandardJsonContentType = "application/json";

    internal static string UnknownQueryField(string queryFieldName) =>
        $"The query field '{queryFieldName}' is not valid for this resource.";

    /// <summary>
    /// Asserts that <paramref name="response"/> is the bad-request shell carrying exactly
    /// <paramref name="expectedErrors"/>, in that order.
    /// </summary>
    internal static async Task AssertShellAsync(HttpResponseMessage response, params string[] expectedErrors)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(expectedErrors);

        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);

        // Bound before asserting. Reaching through a null-conditional would short-circuit the whole
        // chain, so an absent header would satisfy the media-type assertion instead of failing it.
        MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;

        contentType.Should().NotBeNull("a bad request must declare its media type");
        contentType!
            .MediaType.Should()
            .Be(StandardJsonContentType, "a bad request is answered in the current DMS response media type");

        JsonNode body =
            JsonNode.Parse(content)
            ?? throw new InvalidOperationException($"A bad request returned no JSON body: '{content}'.");

        body["detail"]!.GetValue<string>().Should().Be(ProblemDetail);
        body["type"]!.GetValue<string>().Should().Be(ProblemType);
        body["title"]!.GetValue<string>().Should().Be(ProblemTitle);
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        body["validationErrors"]!.AsObject().Should().BeEmpty();
        body["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(expectedErrors);
    }
}

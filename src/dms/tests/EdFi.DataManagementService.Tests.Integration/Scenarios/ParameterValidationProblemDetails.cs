// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The parameter-validation ProblemDetails shell as a client receives it, asserted field by field
/// together with the exact ordered error list.
///
/// <para>
/// Cursor, partition, and traditional paging faults all answer in this one shell, and they do not
/// share one cardinality: a cursor fault carries exactly one message while a partition fault may
/// carry several in a fixed order. Asserting the shell separately from the message list is what keeps
/// a cardinality claim from being smuggled into a shell assertion, so a caller states how many
/// messages it expects by passing them.
/// </para>
/// </summary>
internal static class ParameterValidationProblemDetails
{
    internal const string ProblemType = "urn:ed-fi:api:bad-request:parameter-validation-failed";
    internal const string ProblemTitle = "Parameter Validation Failed";
    internal const string ProblemDetail = "Parameters supplied to the request were invalid.";

    private const string StandardJsonContentType = "application/json";

    /// <summary>
    /// Asserts that <paramref name="response"/> is the parameter-validation shell carrying exactly
    /// <paramref name="expectedErrors"/>, in that order.
    /// </summary>
    internal static async Task AssertShellAsync(HttpResponseMessage response, params string[] expectedErrors)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(expectedErrors);

        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response
            .Content.Headers.ContentType?.MediaType.Should()
            .Be(
                StandardJsonContentType,
                "a parameter fault is answered in the current DMS response media type"
            );

        JsonNode body =
            JsonNode.Parse(content)
            ?? throw new InvalidOperationException($"A parameter fault returned no JSON body: '{content}'.");

        body["detail"]!.GetValue<string>().Should().Be(ProblemDetail);
        body["type"]!.GetValue<string>().Should().Be(ProblemType);
        body["title"]!.GetValue<string>().Should().Be(ProblemTitle);
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        body["validationErrors"]!
            .AsObject()
            .Should()
            .BeEmpty("a parameter fault reports through errors, never through the per-property map");
        body["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(expectedErrors);
    }
}

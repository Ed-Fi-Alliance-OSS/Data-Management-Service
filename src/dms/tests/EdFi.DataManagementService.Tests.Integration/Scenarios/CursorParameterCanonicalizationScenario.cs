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
/// Cross-boundary proof that the query-parameter value the HTTP boundary selects is the value cursor
/// validation actually reasons about.
///
/// <para>
/// Frontend capture tests can only show which spelling and which value reach Core's request
/// dictionary. They cannot show that the surviving value drives validation, because they replace the
/// API service with a fake. Query validation sits behind database fingerprint, resource-key seed, and
/// mapping-set resolution, so no database-free host reaches it; this is the lightest host that
/// exercises the real frontend and the real validator together.
/// </para>
///
/// <para>
/// Each request below is chosen so that the first and the last value of a repeated, case-variant
/// parameter select <em>different</em> outcomes. Asserting which outcome is returned is therefore a
/// direct observation of which value won, rather than a restatement of the capture tests.
/// </para>
///
/// <para>
/// No documents are seeded: both requests are rejected during parameter validation, before any
/// candidate selection. The leased database is still required because the pipeline reads the database
/// fingerprint before query validation runs.
/// </para>
/// </summary>
internal static class CursorParameterCanonicalizationScenario
{
    private const string SchoolsEndpoint = "/data/ed-fi/schools";

    private const string ParameterValidationType = "urn:ed-fi:api:bad-request:parameter-validation-failed";

    private const string OffsetWithPageToken =
        "Both offset and pageToken parameters were provided, but they support alternative paging "
        + "approaches and cannot be used together.";

    /// <summary>
    /// Encoded by the codec that decodes it, so the token is decodable by construction rather than by
    /// a transcription of the transport encoding happening to stay in step with it. Both requests
    /// below need a token that survives token decode, because that is what leaves the later phase free
    /// to answer.
    /// </summary>
    private static readonly string _decodablePageToken = PageTokenCodec.Encode(
        new CursorRange(1, 100),
        PageOrderingMode.DocumentId
    );

    /// <summary>
    /// An undecodable token first, a valid token last. If the first value won, phase 0 would answer
    /// that the token is invalid. The phase-1 conflict proves the last value across a case variant is
    /// what selected the phase.
    /// </summary>
    public static async Task It_selects_the_validation_phase_from_the_last_case_variant_value(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var response = await harness.HttpClient.GetAsync(
            $"{SchoolsEndpoint}?PAGETOKEN=!!!&pageToken={_decodablePageToken}&offset=1"
        );

        await AssertSingleParameterValidationError(response, OffsetWithPageToken);
    }

    /// <summary>
    /// A valid page size first, a malformed one last. If the first value won, the request would have
    /// been accepted. The range error proves the last value within one phase is what was parsed.
    /// </summary>
    public static async Task It_validates_the_last_case_variant_value_within_a_phase(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var response = await harness.HttpClient.GetAsync(
            $"{SchoolsEndpoint}?pageToken={_decodablePageToken}&pageSize=5&PAGESIZE=abc"
        );

        await AssertSingleParameterValidationError(response, "PageSize must be a value between 0 and 500.");
    }

    private static async Task AssertSingleParameterValidationError(
        HttpResponseMessage response,
        string expectedError
    )
    {
        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);

        JsonNode body = JsonNode.Parse(content)!;

        body["detail"]!.GetValue<string>().Should().Be("Parameters supplied to the request were invalid.");
        body["type"]!.GetValue<string>().Should().Be(ParameterValidationType);
        body["title"]!.GetValue<string>().Should().Be("Parameter Validation Failed");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        body["validationErrors"]!.AsObject().Should().BeEmpty();
        body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).Should().Equal(expectedError);
    }
}

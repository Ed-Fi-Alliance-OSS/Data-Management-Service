// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.TimingEvidence;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// The endpoint timing collector aborts a run on any response other than the single
/// <c>invalid_client</c> answer to form credentials, so an error-code regression cannot pass as timing
/// evidence.
/// </summary>
[TestFixture]
public class Given_the_token_endpoint_timing_contract
{
    private const string Invalid = TokenEndpointTimingContract.InvalidClientError;
    private const string Description = TokenEndpointTimingContract.InvalidClientDescription;

    private static string Body(string error, string description) =>
        new JsonObject { ["error"] = error, ["error_description"] = description }.ToJsonString();

    private static bool Expected(int status, string body) =>
        TokenEndpointTimingContract.IsExpected((HttpStatusCode)status, body);

    [TestCase(400, Invalid, true)]
    [TestCase(400, "unauthorized_client", false)]
    [TestCase(200, Invalid, false)]
    [TestCase(401, Invalid, false)]
    public void It_accepts_only_the_invalid_client_400(int status, string error, bool expected) =>
        Expected(status, Body(error, Description)).Should().Be(expected);

    [Test]
    public void It_refuses_another_description() =>
        Expected(400, Body(Invalid, "Client authentication is required.")).Should().BeFalse();

    [Test]
    public void It_refuses_a_body_with_another_member() =>
        Expected(
                400,
                new JsonObject
                {
                    ["error"] = Invalid,
                    ["error_description"] = Description,
                    ["correlationId"] = "id",
                }.ToJsonString()
            )
            .Should()
            .BeFalse();

    [Test]
    public void It_refuses_a_body_without_an_error() =>
        Expected(400, new JsonObject { ["error_description"] = Description }.ToJsonString()).Should().BeFalse();

    [Test]
    public void It_refuses_an_error_that_is_not_a_string() =>
        Expected(400, new JsonObject { ["error"] = 1, ["error_description"] = Description }.ToJsonString())
            .Should()
            .BeFalse();

    [Test]
    public void It_refuses_a_body_that_is_not_an_object() =>
        Expected(400, $"""["{Invalid}"]""").Should().BeFalse();

    [Test]
    public void It_refuses_a_body_that_is_not_json() => Expected(400, "invalid_client").Should().BeFalse();
}

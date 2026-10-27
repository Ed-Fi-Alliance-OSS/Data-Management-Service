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
/// <c>invalid_client</c> 401, so an error-code regression cannot pass as timing evidence.
/// </summary>
[TestFixture]
public class Given_the_token_endpoint_timing_contract
{
    private const string Invalid = TokenEndpointTimingContract.InvalidClientError;
    private const string Unauthorized = "unauthorized_client. Invalid client or Invalid client credentials";

    private static string Body(params string[] errors) =>
        new JsonObject
        {
            ["status"] = 401,
            ["errors"] = new JsonArray([.. errors.Select(error => (JsonNode?)JsonValue.Create(error))]),
        }.ToJsonString();

    private static bool Expected(int status, string body) =>
        TokenEndpointTimingContract.IsExpected((HttpStatusCode)status, body);

    [TestCase(401, Invalid, true)]
    [TestCase(401, Unauthorized, false)]
    [TestCase(200, Invalid, false)]
    [TestCase(400, Invalid, false)]
    public void It_accepts_only_the_invalid_client_401(int status, string error, bool expected) =>
        Expected(status, Body(error)).Should().Be(expected);

    [Test]
    public void It_refuses_a_body_with_more_than_one_error() =>
        Expected(401, Body(Invalid, Invalid)).Should().BeFalse();

    [Test]
    public void It_refuses_a_body_without_errors() => Expected(401, """{"status":401}""").Should().BeFalse();

    [Test]
    public void It_refuses_a_body_that_is_not_an_object() =>
        Expected(401, $"""["{Invalid}"]""").Should().BeFalse();

    [Test]
    public void It_refuses_a_body_that_is_not_json() => Expected(401, "invalid_client").Should().BeFalse();
}

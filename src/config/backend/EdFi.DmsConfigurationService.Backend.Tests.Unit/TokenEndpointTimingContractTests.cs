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
/// The endpoint timing collector aborts a run on any response its arm does not stand for. Only the
/// pre-fix contract's wrong-secret arm may receive <c>unauthorized_client</c>; the current contract
/// never accepts it, so an error-code regression cannot pass as timing evidence.
/// </summary>
[TestFixture]
public class Given_the_token_endpoint_timing_contract
{
    private const string Invalid = TokenEndpointTimingContract.InvalidClientError;
    private const string PreFix = TokenEndpointTimingContract.PreFixWrongSecretError;

    private static string Body(params string[] errors) =>
        new JsonObject
        {
            ["status"] = 401,
            ["errors"] = new JsonArray([.. errors.Select(error => (JsonNode?)JsonValue.Create(error))]),
        }.ToJsonString();

    private static bool Expected(string contract, string arm, int status, string body) =>
        TokenEndpointTimingContract.IsExpected(
            TokenEndpointTimingContract.Parse(contract),
            arm,
            (HttpStatusCode)status,
            body
        );

    [TestCase("current", "absent", 401, Invalid, true)]
    [TestCase("current", "wrong_secret", 401, Invalid, true)]
    [TestCase("current", "unapproved_correct_secret", 401, Invalid, true)]
    [TestCase("current", "wrong_secret", 401, PreFix, false)]
    [TestCase("current", "absent", 401, PreFix, false)]
    [TestCase("current", "absent", 200, Invalid, false)]
    [TestCase("pre-fix", "wrong_secret", 401, PreFix, true)]
    [TestCase("pre-fix", "wrong_secret", 401, Invalid, false)]
    [TestCase("pre-fix", "absent", 401, Invalid, true)]
    [TestCase("pre-fix", "absent", 401, PreFix, false)]
    [TestCase("pre-fix", "unapproved_correct_secret", 401, Invalid, true)]
    [TestCase("pre-fix", "unapproved_correct_secret", 401, PreFix, false)]
    [TestCase("pre-fix", "wrong_secret", 400, PreFix, false)]
    public void It_accepts_only_the_response_the_arm_stands_for(
        string contract,
        string arm,
        int status,
        string error,
        bool expected
    ) => Expected(contract, arm, status, Body(error)).Should().Be(expected);

    [Test]
    public void It_refuses_a_body_with_more_than_one_error() =>
        Expected("current", "absent", 401, Body(Invalid, Invalid)).Should().BeFalse();

    [Test]
    public void It_refuses_a_body_without_errors() =>
        Expected("current", "absent", 401, """{"status":401}""").Should().BeFalse();

    [Test]
    public void It_refuses_a_body_that_is_not_an_object() =>
        Expected("current", "absent", 401, $"""["{Invalid}"]""").Should().BeFalse();

    [Test]
    public void It_refuses_a_body_that_is_not_json() =>
        Expected("current", "absent", 401, "invalid_client").Should().BeFalse();

    [TestCase(null)]
    [TestCase("")]
    [TestCase("current")]
    public void It_reads_an_unset_or_current_setting_as_the_current_contract(string? setting) =>
        TokenEndpointTimingContract.Parse(setting).Should().Be(TokenEndpointContract.Current);

    [Test]
    public void It_reads_pre_fix_as_the_pre_fix_contract() =>
        TokenEndpointTimingContract.Parse("pre-fix").Should().Be(TokenEndpointContract.PreFix);

    [TestCase("prefix")]
    [TestCase("Current")]
    [TestCase("either")]
    public void It_refuses_any_other_setting(string setting) =>
        FluentActions
            .Invoking(() => TokenEndpointTimingContract.Parse(setting))
            .Should()
            .Throw<InvalidOperationException>();
}

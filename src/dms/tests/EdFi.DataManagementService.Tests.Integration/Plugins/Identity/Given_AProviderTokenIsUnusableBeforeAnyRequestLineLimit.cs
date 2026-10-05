// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A request token that is exactly <c>.</c> or <c>..</c>, or that escapes to more than 1024
/// characters, is unusable provider contract misuse for find and for search: <c>502</c>
/// provider-contract-violation with no <c>Location</c>.
/// </summary>
/// <remarks>
/// <para>
/// The negative controls sit next to each refusal: longer dotted values are ordinary data, and a token
/// that escapes to exactly 1024 characters is accepted while one that escapes to 1025 is refused, so
/// the length rule is shown at its boundary and not only far past it.
/// </para>
/// <para>
/// Every <c>Location</c> an accepted case produces is followed as returned and must answer
/// <c>200</c>, and the fixture's own report of the token each poll received must be the token it
/// issued, so an accepted token is shown to reach the results route intact and not only to be
/// accepted.
/// </para>
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_AProviderTokenIsUnusableBeforeAnyRequestLineLimit
{
    private static readonly string ExactlyAtTheEscapedLimit = new string('é', 170) + "aaaa";
    private static readonly string OneOverTheEscapedLimit = new string('é', 170) + "aaaaa";

    private IdentityHttpRun? _run;
    private readonly Dictionary<string, IdentityHttpOutcome> _outcomes = [];
    private readonly Dictionary<string, IdentityHttpOutcome> _followed = [];

    private static IReadOnlyDictionary<string, string> Cases { get; } =
        new Dictionary<string, string>
        {
            ["dot"] = ".",
            ["dotdot"] = "..",
            ["escaped-over-1024"] = new string('é', 600),
            ["escaped-1025"] = OneOverTheEscapedLimit,
            ["escaped-1024"] = ExactlyAtTheEscapedLimit,
            ["three-dots"] = "...",
            ["dotted-name"] = "a.b",
            ["leading-dot"] = ".hidden",
        };

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();

        foreach ((string name, string token) in Cases)
        {
            await SubmitAndFollowAsync(
                $"find:{name}",
                "identities/find",
                $"[{JsonSerializer.Serialize($"~fixture:token:{token}")}]"
            );
            await SubmitAndFollowAsync(
                $"search:{name}",
                "identities/search",
                $$"""[{ "~FixtureToken": {{JsonSerializer.Serialize(token)}} }]"""
            );
        }
    }

    // The Location is followed before the next submission, because find and search reuse one
    // token and a later job under that token replaces the earlier one.
    private async Task SubmitAndFollowAsync(string key, string path, string body)
    {
        _outcomes[key] = await _run!.PostAsync(path, body);

        if (_outcomes[key].Location is { } location)
        {
            _followed[key] = await _run.GetAsync(location);
        }
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_run is not null)
        {
            await _run.DisposeAsync();
            _run = null;
        }
    }

    [TestCase("find", "dot")]
    [TestCase("search", "dot")]
    [TestCase("find", "dotdot")]
    [TestCase("search", "dotdot")]
    [TestCase("find", "escaped-over-1024")]
    [TestCase("search", "escaped-over-1024")]
    [TestCase("find", "escaped-1025")]
    [TestCase("search", "escaped-1025")]
    public void It_refuses_the_unusable_token_with_502(string operation, string name)
    {
        _outcomes[$"{operation}:{name}"].Status.Should().Be(HttpStatusCode.BadGateway);
    }

    [TestCase("find", "dot")]
    [TestCase("search", "dot")]
    [TestCase("find", "dotdot")]
    [TestCase("search", "dotdot")]
    [TestCase("find", "escaped-over-1024")]
    [TestCase("search", "escaped-over-1024")]
    [TestCase("find", "escaped-1025")]
    [TestCase("search", "escaped-1025")]
    public void It_names_the_refusal_provider_contract_violation(string operation, string name)
    {
        _outcomes[$"{operation}:{name}"]
            .ProblemType.Should()
            .Be(IdentityFailureResponse.ProviderContractViolationType);
    }

    [TestCase("find", "dot")]
    [TestCase("search", "dot")]
    [TestCase("find", "dotdot")]
    [TestCase("search", "dotdot")]
    [TestCase("find", "escaped-over-1024")]
    [TestCase("search", "escaped-over-1024")]
    [TestCase("find", "escaped-1025")]
    [TestCase("search", "escaped-1025")]
    public void It_returns_no_location_for_the_unusable_token(string operation, string name)
    {
        _outcomes[$"{operation}:{name}"].Location.Should().BeNull();
    }

    [TestCase("find", "three-dots")]
    [TestCase("search", "three-dots")]
    [TestCase("find", "dotted-name")]
    [TestCase("search", "dotted-name")]
    [TestCase("find", "leading-dot")]
    [TestCase("search", "leading-dot")]
    [TestCase("find", "escaped-1024")]
    [TestCase("search", "escaped-1024")]
    public void It_accepts_the_neighbouring_usable_token_with_202(string operation, string name)
    {
        _outcomes[$"{operation}:{name}"].Status.Should().Be(HttpStatusCode.Accepted);
    }

    [TestCase("find", "three-dots")]
    [TestCase("search", "three-dots")]
    [TestCase("find", "dotted-name")]
    [TestCase("search", "dotted-name")]
    [TestCase("find", "leading-dot")]
    [TestCase("search", "leading-dot")]
    [TestCase("find", "escaped-1024")]
    [TestCase("search", "escaped-1024")]
    public void It_follows_each_accepted_location_to_the_results_route(string operation, string name)
    {
        _followed[$"{operation}:{name}"].Status.Should().Be(HttpStatusCode.OK);
    }

    [TestCase("three-dots")]
    [TestCase("dotted-name")]
    [TestCase("leading-dot")]
    [TestCase("escaped-1024")]
    public void It_hands_the_provider_the_accepted_token_unchanged_for_find_and_search(string name)
    {
        _run!.Stub.ReceivedResultTokens.Count(received => received == Cases[name]).Should().Be(2);
    }

    [Test]
    public void It_polls_exactly_the_accepted_tokens_in_submission_order_and_nothing_else()
    {
        // The accepted cases in the order Cases lists them, each followed once for find and once for
        // search; a refused case has no Location, so it is never polled.
        string[] expected =
        [
            .. new[] { "escaped-1024", "three-dots", "dotted-name", "leading-dot" }.SelectMany(name =>
                new[] { Cases[name], Cases[name] }
            ),
        ];

        _run!.Stub.ReceivedResultTokens.Should().Equal(expected);
    }

    [Test]
    public void It_builds_the_boundary_tokens_one_character_apart()
    {
        Uri.EscapeDataString(ExactlyAtTheEscapedLimit).Length.Should().Be(1024);
        Uri.EscapeDataString(OneOverTheEscapedLimit).Length.Should().Be(1025);
    }
}

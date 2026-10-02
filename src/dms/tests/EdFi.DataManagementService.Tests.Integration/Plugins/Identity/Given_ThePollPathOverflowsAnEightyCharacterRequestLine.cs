// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// F15: a token that is well under 1024 characters but whose composed poll request line (tenant, two
/// route qualifiers and the results route) would exceed the deployment's request-line limit is refused
/// with the provider-contract-violation <c>502</c> and no <c>Location</c>, for find and for search.
/// </summary>
/// <remarks>
/// Kestrel's request-line limit is set to 80 through the harness hook, because the default 8192 cannot
/// be overflowed by a token that already passed the 1024 rule. Under tenant-one, district 255901 and
/// school year 2026 the poll request line is 70 characters plus the escaped token, so a 10-character
/// escaped token fits exactly and an 11-character one does not. The accepted neighbours are the
/// negative controls, and the escaped cases (the non-ASCII letter escapes to 6 characters) show the
/// measurement is of the escaped token, not the raw one.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_ThePollPathOverflowsAnEightyCharacterRequestLine
{
    private const int RequestLineLimit = 80;

    private IdentityHttpRun? _run;
    private readonly Dictionary<string, IdentityHttpOutcome> _outcomes = [];

    private static IReadOnlyDictionary<string, string> Cases { get; } =
        new Dictionary<string, string>
        {
            ["fits-10"] = new string('a', 10),
            ["over-11"] = new string('a', 11),
            ["escaped-fits-10"] = "é" + "aaaa",
            ["escaped-over-11"] = "é" + "aaaaa",
            ["escaped-two-letters-12"] = "éé",
            ["short-ordinary"] = "tok-1",
        };

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync(services =>
            services.Configure<KestrelServerOptions>(options =>
                options.Limits.MaxRequestLineSize = RequestLineLimit
            )
        );

        foreach ((string name, string token) in Cases)
        {
            _outcomes[$"find:{name}"] = await _run.PostAsync(
                "identities/find",
                $"[{JsonSerializer.Serialize($"~fixture:token:{token}")}]"
            );
            _outcomes[$"search:{name}"] = await _run.PostAsync(
                "identities/search",
                $$"""[{ "~FixtureToken": {{JsonSerializer.Serialize(token)}} }]"""
            );
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

    [TestCase("find", "over-11")]
    [TestCase("search", "over-11")]
    [TestCase("find", "escaped-over-11")]
    [TestCase("search", "escaped-over-11")]
    [TestCase("find", "escaped-two-letters-12")]
    [TestCase("search", "escaped-two-letters-12")]
    public void It_refuses_a_token_whose_poll_path_overflows_with_502(string operation, string name)
    {
        _outcomes[$"{operation}:{name}"].Status.Should().Be(HttpStatusCode.BadGateway);
    }

    [TestCase("find", "over-11")]
    [TestCase("search", "over-11")]
    [TestCase("find", "escaped-over-11")]
    [TestCase("search", "escaped-over-11")]
    [TestCase("find", "escaped-two-letters-12")]
    [TestCase("search", "escaped-two-letters-12")]
    public void It_names_the_refusal_provider_contract_violation(string operation, string name)
    {
        _outcomes[$"{operation}:{name}"]
            .ProblemType.Should()
            .Be(IdentityFailureResponse.ProviderContractViolationType);
    }

    [TestCase("find", "over-11")]
    [TestCase("search", "over-11")]
    [TestCase("find", "escaped-over-11")]
    [TestCase("search", "escaped-over-11")]
    [TestCase("find", "escaped-two-letters-12")]
    [TestCase("search", "escaped-two-letters-12")]
    public void It_returns_no_location_for_an_overflowing_token(string operation, string name)
    {
        _outcomes[$"{operation}:{name}"].Location.Should().BeNull();
    }

    [TestCase("find", "fits-10")]
    [TestCase("search", "fits-10")]
    [TestCase("find", "escaped-fits-10")]
    [TestCase("search", "escaped-fits-10")]
    [TestCase("find", "short-ordinary")]
    [TestCase("search", "short-ordinary")]
    public void It_accepts_a_token_that_fits_the_request_line_with_202(string operation, string name)
    {
        _outcomes[$"{operation}:{name}"].Status.Should().Be(HttpStatusCode.Accepted);
    }

    [TestCase("find", "fits-10")]
    [TestCase("search", "escaped-fits-10")]
    public void It_composes_a_location_that_exactly_fills_the_request_line(string operation, string name)
    {
        string location = _outcomes[$"{operation}:{name}"].Location!;
        string pathAndQuery = new Uri(location).PathAndQuery;

        // "GET " + path + " HTTP/1.1" + CRLF is what Kestrel measures.
        (4 + pathAndQuery.Length + " HTTP/1.1".Length + 2)
            .Should()
            .Be(RequestLineLimit);
    }
}

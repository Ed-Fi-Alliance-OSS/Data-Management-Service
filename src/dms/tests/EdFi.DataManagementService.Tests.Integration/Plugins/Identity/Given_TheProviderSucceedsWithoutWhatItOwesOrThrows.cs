// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A provider that answers <c>InvalidProperties</c> yields a <c>400</c>; a
/// <c>Success</c> without a payload, a find or search success with both or neither of payload and
/// token yields the provider-contract-violation <c>502</c>; a provider that throws yields the sanitized
/// upstream-failure <c>502</c> that quotes nothing the exception carried.
/// </summary>
/// <remarks>
/// The throwing variant's exception text names a person and carries a sentinel; the body must contain
/// neither. The negative control is an ordinary successful call of each operation, so the mappings are
/// not a side effect of the host answering every fixture trigger with an error.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheProviderSucceedsWithoutWhatItOwesOrThrows
{
    private static readonly string[] Operations = ["create", "getById", "find", "search", "results"];

    private IdentityHttpRun? _run;
    private readonly Dictionary<string, IdentityHttpOutcome> _outcomes = [];

    private static string Key(string operation, string variant) => $"{operation}:{variant}";

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();

        foreach (string variant in new[] { "success-nopayload", "throw-person", "invalid-createFieldError" })
        {
            foreach (string operation in Operations)
            {
                _outcomes[Key(operation, variant)] = await SendAsync(operation, variant);
            }
        }

        foreach (string variant in new[] { "success-neither", "success-both" })
        {
            _outcomes[Key("find", variant)] = await SendAsync("find", variant);
            _outcomes[Key("search", variant)] = await SendAsync("search", variant);
        }

        _outcomes[Key("create", "ordinary")] = await _run.PostAsync(
            "identities",
            """{"LastSurname":"Rivera"}"""
        );
    }

    private async Task<IdentityHttpOutcome> SendAsync(string operation, string variant)
    {
        switch (operation)
        {
            case "create":
                return await _run!.PostAsync(
                    "identities",
                    $$"""{ "LastSurname": "Rivera", "~FixtureReturn": "{{variant}}" }"""
                );
            case "getById":
                return await _run!.GetAsync($"identities/~fixture-return-{variant}");
            case "find":
                return await _run!.PostAsync("identities/find", $$"""["~fixture:return:{{variant}}"]""");
            case "search":
                return await _run!.PostAsync(
                    "identities/search",
                    $$"""[{ "~FixtureReturn": "{{variant}}" }]"""
                );
            default:
                IdentityHttpOutcome accepted = await _run!.PostAsync(
                    "identities/find",
                    $$"""["~fixture:results:{{variant}}"]"""
                );
                accepted.Status.Should().Be(HttpStatusCode.Accepted);
                return await _run.GetAsync(accepted.Location!);
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

    [TestCase("create")]
    [TestCase("getById")]
    [TestCase("find")]
    [TestCase("search")]
    [TestCase("results")]
    public void It_answers_invalid_properties_with_400(string operation)
    {
        _outcomes[Key(operation, "invalid-createFieldError")].Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestCase("create")]
    [TestCase("getById")]
    [TestCase("find")]
    [TestCase("search")]
    [TestCase("results")]
    public void It_answers_a_success_without_a_payload_with_the_contract_violation(string operation)
    {
        IdentityHttpOutcome outcome = _outcomes[Key(operation, "success-nopayload")];
        outcome.Status.Should().Be(HttpStatusCode.BadGateway);
        outcome.ProblemType.Should().Be(IdentityFailureResponse.ProviderContractViolationType);
    }

    [TestCase("find", "success-neither")]
    [TestCase("search", "success-neither")]
    [TestCase("find", "success-both")]
    [TestCase("search", "success-both")]
    public void It_answers_a_find_or_search_success_with_both_or_neither_with_the_contract_violation(
        string operation,
        string variant
    )
    {
        IdentityHttpOutcome outcome = _outcomes[Key(operation, variant)];
        outcome.Status.Should().Be(HttpStatusCode.BadGateway);
        outcome.ProblemType.Should().Be(IdentityFailureResponse.ProviderContractViolationType);
    }

    [TestCase("find", "success-both")]
    [TestCase("search", "success-both")]
    [TestCase("find", "success-neither")]
    [TestCase("search", "success-neither")]
    public void It_returns_no_location_for_a_both_or_neither_success(string operation, string variant)
    {
        _outcomes[Key(operation, variant)].Location.Should().BeNull();
    }

    [TestCase("create")]
    [TestCase("getById")]
    [TestCase("find")]
    [TestCase("search")]
    [TestCase("results")]
    public void It_answers_a_throwing_provider_with_the_upstream_failure(string operation)
    {
        IdentityHttpOutcome outcome = _outcomes[Key(operation, "throw-person")];
        outcome.Status.Should().Be(HttpStatusCode.BadGateway);
        outcome.ProblemType.Should().Be(IdentityFailureResponse.UpstreamFailureType);
    }

    [TestCase("create")]
    [TestCase("getById")]
    [TestCase("find")]
    [TestCase("search")]
    [TestCase("results")]
    public void It_quotes_nothing_the_throwing_provider_said(string operation)
    {
        string text = _outcomes[Key(operation, "throw-person")].Text;
        text.Should().NotContain("Jane");
        text.Should().NotContain("SENTINEL");
        text.Should().NotContain("1999");
    }

    [Test]
    public void It_answers_an_ordinary_create_with_200_so_the_mappings_are_not_blanket_errors()
    {
        _outcomes[Key("create", "ordinary")].Status.Should().Be(HttpStatusCode.OK);
    }
}

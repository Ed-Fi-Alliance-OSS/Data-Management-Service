// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// Every identity answer, success or failure, carries exactly one <c>Cache-Control</c> header
/// containing <c>no-store</c>, including the answers produced before the provider is reached (an
/// unknown bearer token through the real authentication middleware, a missing credential) and the
/// capability rejection. Metadata and discovery keep the caching headers they had.
/// </summary>
/// <remarks>
/// The metadata and discovery answers are the negative control: the same host and the same
/// assertion helper find no <c>Cache-Control</c> there, so a header added to every response would
/// fail those cases.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_EveryIdentityAnswerIsMarkedNoStore
{
    private IdentityHttpRun? _run;
    private IdentityHttpRun? _withoutSearch;
    private readonly Dictionary<string, IdentityHttpOutcome> _answers = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();
        _withoutSearch = await IdentityHttpRun.StartAsync(
            fixtureSettings: new Dictionary<string, string>
            {
                ["IdentityFixture:Capabilities"] = "Create,GetById,Find,Results",
            }
        );

        IdentityHttpOutcome created = await _run.PostAsync("identities", """{ "LastSurname": "Rivera" }""");
        _answers["create"] = created;
        string uniqueId = created.Body!.GetValue<string>();

        _answers["getById"] = await _run.GetAsync($"identities/{uniqueId}");
        _answers["find"] = await _run.PostAsync("identities/find", $"""["{uniqueId}"]""");
        _answers["search"] = await _run.PostAsync("identities/search", """[{ "LastSurname": "Rivera" }]""");

        IdentityHttpOutcome accepted = await _run.PostAsync("identities/find", """["~fixture:async"]""");
        _answers["async"] = accepted;
        string pending = ResultsRoute(accepted);
        _answers["resultsPending"] = await _run.GetAsync(pending);
        _answers["resultsComplete"] = await _run.GetAsync(pending);

        string failedToken = TokenOf(await _run.PostAsync("identities/find", """["~fixture:async"]"""));
        _run.Stub.FailJob(failedToken);
        _answers["resultsTerminal"] = await _run.GetAsync($"identities/results/{failedToken}");

        string expiredToken = TokenOf(await _run.PostAsync("identities/find", """["~fixture:async"]"""));
        _run.Stub.ExpireJob(expiredToken);
        _answers["resultsExpired"] = await _run.GetAsync($"identities/results/{expiredToken}");

        _answers["resultsMissing"] = await _run.GetAsync(
            "identities/results/0123456789abcdef0123456789abcdef"
        );

        _answers["capabilityRejection"] = await _withoutSearch.PostAsync(
            "identities/search",
            """[{ "LastSurname": "Rivera" }]"""
        );

        using (HttpClient unknown = _run.Host.CreateClient(IdentityTestClients.UnknownToken))
        {
            _answers["unknownBearerToken"] = await _run.GetAsync($"identities/{uniqueId}", unknown);
        }

        using (HttpClient anonymous = _run.Host.CreateClient())
        {
            _answers["noAuthorization"] = await _run.GetAsync($"identities/{uniqueId}", anonymous);
        }

        using (HttpClient anonymous = _run.Host.CreateClient())
        {
            _answers["swagger"] = await _run.GetAsync(
                "http://localhost/metadata/identity/v2/swagger.json",
                anonymous
            );
            _answers["discovery"] = await _run.GetAsync("http://localhost/", anonymous);
        }
    }

    private static string TokenOf(IdentityHttpOutcome accepted) => accepted.Location!.Split('/')[^1];

    private static string ResultsRoute(IdentityHttpOutcome accepted) =>
        $"identities/results/{TokenOf(accepted)}";

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_run is not null)
        {
            await _run.DisposeAsync();
        }

        if (_withoutSearch is not null)
        {
            await _withoutSearch.DisposeAsync();
        }
    }

    [TestCase("create", HttpStatusCode.OK)]
    [TestCase("getById", HttpStatusCode.OK)]
    [TestCase("find", HttpStatusCode.OK)]
    [TestCase("search", HttpStatusCode.OK)]
    [TestCase("async", HttpStatusCode.Accepted)]
    [TestCase("resultsPending", HttpStatusCode.OK)]
    [TestCase("resultsComplete", HttpStatusCode.OK)]
    [TestCase("resultsTerminal", HttpStatusCode.BadGateway)]
    [TestCase("resultsMissing", HttpStatusCode.NotFound)]
    [TestCase("resultsExpired", HttpStatusCode.NotFound)]
    [TestCase("capabilityRejection", HttpStatusCode.NotFound)]
    [TestCase("unknownBearerToken", HttpStatusCode.Unauthorized)]
    [TestCase("noAuthorization", HttpStatusCode.Unauthorized)]
    public void It_marks_the_answer_no_store_exactly_once(string answer, HttpStatusCode expectedStatus)
    {
        // The status is asserted first so a case that did not reach the path it names cannot pass.
        _answers[answer].Status.Should().Be(expectedStatus);
        _answers[answer]
            .HeaderValues("Cache-Control")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("no-store");
    }

    [Test]
    public void It_leaves_the_pending_and_complete_polls_distinguishable()
    {
        _answers["resultsPending"].Text.Should().Contain("Incomplete");
        _answers["resultsComplete"].Text.Should().Contain("Complete").And.NotContain("Incomplete");
    }

    [TestCase("swagger")]
    [TestCase("discovery")]
    public void It_adds_no_cache_control_to_metadata_or_discovery(string answer)
    {
        _answers[answer].Status.Should().Be(HttpStatusCode.OK);
        _answers[answer].HeaderValues("Cache-Control").Should().BeEmpty();
    }
}

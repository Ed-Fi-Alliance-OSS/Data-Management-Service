// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A scored demographic match is not proof of the issuance, even one that does return the
/// issued person. The client's own rule refuses it; the lookup is enabled and answers a match.
/// </summary>
/// <remarks>
/// The negative control is the exact upstream-key lookup in the same host, which the client accepts.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_ACreateIsLostAndOnlyAScoredDemographicMatchIsOffered
{
    private const string CreateBody =
        """{ "LastSurname": "Rivera", "FirstName": "Ana", "upstreamKey": "upstream-key-34b", "~FixtureReturn": "lost-create" }""";

    private IdentityHttpRun? _run;
    private IdentityExampleClient? _fullMatchClient;
    private IdentityCreateOutcome? _fullMatch;
    private IdentityExampleClient? _partialMatchClient;
    private IdentityCreateOutcome? _partialMatch;
    private IdentityExampleClient? _fractionalMatchClient;
    private IdentityCreateOutcome? _fractionalMatch;
    private IdentityCreateOutcome? _exact;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();
        string create = _run.Route("identities");
        string search = _run.Route("identities/search");

        _fullMatchClient = new IdentityExampleClient(_run.Client);
        _fullMatch = await _fullMatchClient.CreateWithReconciliationAsync(
            create,
            search,
            CreateBody,
            IdentityReconciliationLookup.Demographic("""{ "LastSurname": "Rivera", "FirstName": "Ana" }""")
        );

        _partialMatchClient = new IdentityExampleClient(_run.Client);
        _partialMatch = await _partialMatchClient.CreateWithReconciliationAsync(
            create,
            search,
            CreateBody,
            IdentityReconciliationLookup.Demographic("""{ "LastSurname": "Rivera", "FirstName": "Zed" }""")
        );

        _fractionalMatchClient = new IdentityExampleClient(_run.Client);
        _fractionalMatch = await _fractionalMatchClient.CreateWithReconciliationAsync(
            create,
            search,
            CreateBody,
            IdentityReconciliationLookup.Demographic(
                """{ "LastSurname": "Rivera", "FirstName": "Ana", "MiddleName": "Zed" }"""
            )
        );

        _exact = await new IdentityExampleClient(_run.Client).CreateWithReconciliationAsync(
            create,
            search,
            CreateBody,
            IdentityReconciliationLookup.ExactUpstreamKey("upstream-key-34b")
        );
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_run is not null)
        {
            await _run.DisposeAsync();
        }
    }

    [Test]
    public void It_refuses_a_demographic_match_that_returns_the_issued_person_at_score_100()
    {
        _fullMatch!.LookupStatus.Should().Be(HttpStatusCode.OK);
        _fullMatch.LookupScores.Should().Equal(100d);
        _fullMatch.Ending.Should().Be(IdentityCreateEnding.OperatorReconciliationRequired);
        _fullMatch.UniqueId.Should().BeNull();
    }

    [Test]
    public void It_refuses_several_partial_score_matches()
    {
        _partialMatch!.LookupStatus.Should().Be(HttpStatusCode.OK);
        // The first lost create issued a person too, so two persons agree on half of the attributes.
        _partialMatch.LookupScores.Should().Equal(50d, 50d);
        _partialMatch.Ending.Should().Be(IdentityCreateEnding.OperatorReconciliationRequired);
        _partialMatch.UniqueId.Should().BeNull();
    }

    [Test]
    public void It_reads_a_fractional_score_and_refuses_that_match_too()
    {
        _fractionalMatch!.LookupStatus.Should().Be(HttpStatusCode.OK);
        // Each of the three persons issued so far agrees on two of the three supplied attributes.
        _fractionalMatch.LookupScores.Should().Equal(66.67d, 66.67d, 66.67d);
        _fractionalMatch.Ending.Should().Be(IdentityCreateEnding.OperatorReconciliationRequired);
        _fractionalMatch.UniqueId.Should().BeNull();
    }

    [Test]
    public void It_makes_no_create_retry_after_refusing()
    {
        foreach (
            IdentityExampleClient client in new[]
            {
                _fullMatchClient!,
                _partialMatchClient!,
                _fractionalMatchClient!,
            }
        )
        {
            client.Requests.Should().HaveCount(2);
            client.Requests.Count(request => request.PathAndQuery.EndsWith("/identities")).Should().Be(1);
        }
    }

    [Test]
    public void It_accepts_the_exact_upstream_key_lookup_in_the_same_host()
    {
        _exact!.Ending.Should().Be(IdentityCreateEnding.Recovered);
        _exact.LookupScores.Should().Equal(100d);
    }

    [Test]
    public void It_leaves_one_issuance_per_create_the_lost_creates_were_never_repeated()
    {
        _run!.Stub.IssuanceCount.Should().Be(4);
        _run.Stub.InvocationCount("create").Should().Be(4);
    }
}

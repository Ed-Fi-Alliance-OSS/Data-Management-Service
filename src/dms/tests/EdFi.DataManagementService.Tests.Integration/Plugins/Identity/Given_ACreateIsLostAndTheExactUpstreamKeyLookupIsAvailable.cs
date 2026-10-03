// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// F33: the fixture issues an identity and then throws, DMS answers 502, and the example client
/// recovers the original id through the exact upstream-key lookup without creating a second time.
/// </summary>
/// <remarks>
/// The negative control is a create that is not lost: the same client adopts the id the create
/// answered and never searches.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_ACreateIsLostAndTheExactUpstreamKeyLookupIsAvailable
{
    private const string UpstreamKey = "upstream-key-33";

    private IdentityHttpRun? _run;
    private IdentityExampleClient? _client;
    private IdentityCreateOutcome? _recovered;
    private IdentityHttpOutcome? _recoveredPerson;
    private int _issuancesAfterRecovery;
    private int _createInvocationsAfterRecovery;
    private IdentityExampleClient? _controlClient;
    private IdentityCreateOutcome? _control;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();

        _client = new IdentityExampleClient(_run.Client);
        _recovered = await _client.CreateWithReconciliationAsync(
            _run.Route("identities"),
            _run.Route("identities/search"),
            """{ "LastSurname": "Rivera", "FirstName": "Ana", "upstreamKey": "upstream-key-33", "~FixtureReturn": "lost-create" }""",
            IdentityReconciliationLookup.ExactUpstreamKey(UpstreamKey)
        );
        _issuancesAfterRecovery = _run.Stub.IssuanceCount;
        _createInvocationsAfterRecovery = _run.Stub.InvocationCount("create");
        _recoveredPerson = await _run.GetAsync($"identities/{_recovered.UniqueId}");

        _controlClient = new IdentityExampleClient(_run.Client);
        _control = await _controlClient.CreateWithReconciliationAsync(
            _run.Route("identities"),
            _run.Route("identities/search"),
            """{ "LastSurname": "Smith", "FirstName": "Bob", "upstreamKey": "upstream-key-control" }""",
            IdentityReconciliationLookup.ExactUpstreamKey("upstream-key-control")
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
    public void It_treats_the_502_from_create_as_an_unknown_outcome_and_recovers()
    {
        _recovered!.CreateStatus.Should().Be(HttpStatusCode.BadGateway);
        _recovered.Ending.Should().Be(IdentityCreateEnding.Recovered);
        _recovered.LookupScores.Should().Equal(100d);
    }

    [Test]
    public void It_recovers_the_id_the_fixture_issued_for_that_upstream_key()
    {
        _recovered!.UniqueId.Should().MatchRegex("^[0-9a-f]{32}$");
        _recoveredPerson!.Status.Should().Be(HttpStatusCode.OK);
        _recoveredPerson.Body!["UniqueId"]!.GetValue<string>().Should().Be(_recovered.UniqueId);
        _recoveredPerson.Body["upstreamKey"]!.GetValue<string>().Should().Be(UpstreamKey);
        _recoveredPerson.Body["LastSurname"]!.GetValue<string>().Should().Be("Rivera");
    }

    [Test]
    public void It_sends_one_create_and_one_lookup_and_no_second_create()
    {
        _client!
            .Requests.Select(request =>
                (request.Method, request.PathAndQuery.EndsWith("/identities"), request.Status)
            )
            .Should()
            .Equal(
                (HttpMethod.Post, true, HttpStatusCode.BadGateway),
                (HttpMethod.Post, false, HttpStatusCode.OK)
            );
        _client.Requests[1].PathAndQuery.Should().EndWith("/identities/search");
    }

    [Test]
    public void It_leaves_exactly_one_issuance_and_one_create_for_the_lost_create()
    {
        _issuancesAfterRecovery.Should().Be(1);
        _createInvocationsAfterRecovery.Should().Be(1);
    }

    [Test]
    public void It_adopts_the_answered_id_of_a_create_that_was_not_lost_without_searching()
    {
        _control!.Ending.Should().Be(IdentityCreateEnding.Created);
        _control.UniqueId.Should().MatchRegex("^[0-9a-f]{32}$").And.NotBe(_recovered!.UniqueId);
        _controlClient!.Requests.Should().HaveCount(1);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// With the reconciliation lookup disabled an empty answer cannot establish absence, so the
/// example client stops for operator reconciliation and never retries the create.
/// </summary>
/// <remarks>
/// The fixture issued the identity, so a client that read the empty answer as "nothing issued" and
/// retried would leave a second issuance; the issuance count and the request log both show it did not.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_ACreateIsLostAndTheReconciliationLookupIsDisabled
{
    private IdentityHttpRun? _run;
    private IdentityExampleClient? _client;
    private IdentityCreateOutcome? _outcome;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync(
            fixtureSettings: new Dictionary<string, string>
            {
                ["IdentityFixture:ReconciliationLookup"] = "false",
            }
        );
        _client = new IdentityExampleClient(_run.Client);
        _outcome = await _client.CreateWithReconciliationAsync(
            _run.Route("identities"),
            _run.Route("identities/search"),
            """{ "LastSurname": "Rivera", "FirstName": "Ana", "upstreamKey": "upstream-key-34a", "~FixtureReturn": "lost-create" }""",
            IdentityReconciliationLookup.ExactUpstreamKey("upstream-key-34a")
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
    public void It_stops_and_reports_that_operator_reconciliation_is_required()
    {
        _outcome!.Ending.Should().Be(IdentityCreateEnding.OperatorReconciliationRequired);
        _outcome.Reason.Should().StartWith("operator reconciliation required");
        _outcome.UniqueId.Should().BeNull();
    }

    [Test]
    public void It_saw_an_empty_lookup_answer_and_did_not_read_it_as_absence()
    {
        _outcome!.CreateStatus.Should().Be(HttpStatusCode.BadGateway);
        _outcome.LookupStatus.Should().Be(HttpStatusCode.OK);
        _outcome.LookupScores.Should().BeEmpty();
    }

    [Test]
    public void It_makes_no_create_retry()
    {
        _client!.Requests.Count(request => request.PathAndQuery.EndsWith("/identities")).Should().Be(1);
        _client.Requests.Should().HaveCount(2);
        _run!.Stub.InvocationCount("create").Should().Be(1);
    }

    [Test]
    public void It_leaves_the_single_issuance_of_the_lost_create_untouched()
    {
        _run!.Stub.IssuanceCount.Should().Be(1);
    }
}

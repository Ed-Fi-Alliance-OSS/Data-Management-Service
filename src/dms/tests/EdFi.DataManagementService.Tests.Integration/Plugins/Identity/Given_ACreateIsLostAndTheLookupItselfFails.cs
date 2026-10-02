// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// F34: when the reconciliation lookup is unavailable the client cannot tell whether the identity was
/// issued, so it stops for operator reconciliation and does not retry the create.
/// </summary>
/// <remarks>
/// Every operation throws, so both the create and the lookup answer 502. The negative control is the
/// healthy host in the sibling classes, where the same lookup recovers.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_ACreateIsLostAndTheLookupItselfFails
{
    private IdentityHttpRun? _run;
    private IdentityExampleClient? _client;
    private IdentityCreateOutcome? _outcome;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync(
            fixtureSettings: new Dictionary<string, string> { ["IdentityFixture:ThrowAt"] = "Operation" }
        );
        _client = new IdentityExampleClient(_run.Client);
        _outcome = await _client.CreateWithReconciliationAsync(
            _run.Route("identities"),
            _run.Route("identities/search"),
            """{ "LastSurname": "Rivera", "FirstName": "Ana", "upstreamKey": "upstream-key-34c" }""",
            IdentityReconciliationLookup.ExactUpstreamKey("upstream-key-34c")
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
    public void It_stops_for_operator_reconciliation_when_the_lookup_answers_502()
    {
        _outcome!.CreateStatus.Should().Be(HttpStatusCode.BadGateway);
        _outcome.LookupStatus.Should().Be(HttpStatusCode.BadGateway);
        _outcome.Ending.Should().Be(IdentityCreateEnding.OperatorReconciliationRequired);
        _outcome.Reason.Should().StartWith("operator reconciliation required");
    }

    [Test]
    public void It_makes_no_create_retry()
    {
        _client!.Requests.Count(request => request.PathAndQuery.EndsWith("/identities")).Should().Be(1);
        _client.Requests.Should().HaveCount(2);
        _run!.Stub.InvocationCount("create").Should().Be(1);
    }
}

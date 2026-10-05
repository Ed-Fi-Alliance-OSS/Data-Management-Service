// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// The example client follows <c>Location</c>, polls to completion, and on the terminal
/// <c>job-failed</c> problem stops - no further poll and no resubmission of the original request.
/// </summary>
/// <remarks>
/// The negative control is the same client against a job that completes, which polls past the
/// incomplete answers and ends with the payload. The provider's own event log is read as well, so
/// "never resubmitted" is not judged from the client's word alone.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheExampleClientMeetsAJobThatFailed
{
    private const string FixturePlugin = "Acme.IdentityFixture";

    private IdentityFixtureControlStub? _stub;
    private IdentityPluginHost? _host;
    private IdentityExampleClient? _failedClient;
    private IdentityClientOutcome? _failed;
    private int _findInvocationsAfterFailed;
    private int _resultsInvocationsAfterFailed;
    private IdentityExampleClient? _completingClient;
    private IdentityClientOutcome? _completing;

    [OneTimeSetUp]
    public async Task Setup()
    {
        string district = IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)[0];

        _stub = await IdentityFixtureControlStub.StartAsync();
        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .WithControlStub(_stub)
            .Namespace("ns-a", (IdentityTestClients.TenantOne, district))
            .With("IdentityFixture:PollsUntilComplete", "2");
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings
        );
        _stub.Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "ns-a");

        string findRoute = IdentityTestClients.Route(
            IdentityTestClients.TenantOne,
            district,
            "identities/find"
        );

        using HttpClient httpFailed = _host.CreateClient(IdentityTestClients.ClientA.Token);
        _failedClient = new IdentityExampleClient(httpFailed);
        _failed = await _failedClient.SubmitAndFollowAsync(findRoute, """["~fixture:results:jobfailed"]""");
        _findInvocationsAfterFailed = _stub.InvocationCount("find");
        _resultsInvocationsAfterFailed = _stub.InvocationCount("results");

        using HttpClient httpCompleting = _host.CreateClient(IdentityTestClients.ClientA.Token);
        _completingClient = new IdentityExampleClient(httpCompleting);
        _completing = await _completingClient.SubmitAndFollowAsync(findRoute, """["~fixture:async"]""");
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }

        if (_stub is not null)
        {
            await _stub.DisposeAsync();
        }
    }

    [Test]
    public void It_reports_a_terminal_failure_on_job_failed()
    {
        _failed!.Ending.Should().Be(IdentityClientEnding.JobFailed);
        _failed.LastStatus.Should().Be(HttpStatusCode.BadGateway);
        _failed.ProblemType.Should().Be("urn:ed-fi:api:identities:job-failed");
    }

    [Test]
    public void It_sends_the_submission_and_exactly_one_poll_before_it_stops()
    {
        _failedClient!
            .Requests.Select(request => (request.Method, request.Status))
            .Should()
            .Equal((HttpMethod.Post, HttpStatusCode.Accepted), (HttpMethod.Get, HttpStatusCode.BadGateway));
    }

    [Test]
    public void It_never_resubmits_the_original_request()
    {
        _failedClient!.Requests.Count(request => request.Method == HttpMethod.Post).Should().Be(1);
        _findInvocationsAfterFailed.Should().Be(1);
    }

    [Test]
    public void It_sends_no_further_poll_after_the_terminal_answer()
    {
        _resultsInvocationsAfterFailed.Should().Be(1);
    }

    [Test]
    public void It_polls_the_location_it_was_given_byte_for_byte()
    {
        _failedClient!.Requests[1].PathAndQuery.Should().Contain("/identities/results/");
    }

    [Test]
    public void It_polls_past_the_incomplete_answers_of_a_job_that_completes()
    {
        _completing!.Ending.Should().Be(IdentityClientEnding.Completed);
        _completingClient!
            .Requests.Select(request => request.Method)
            .Should()
            .Equal(HttpMethod.Post, HttpMethod.Get, HttpMethod.Get, HttpMethod.Get);
    }
}

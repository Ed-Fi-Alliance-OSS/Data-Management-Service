// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// F8: a transient poll failure answers upstream-failure <c>502</c> and the same token then answers; a
/// terminally failed job answers <c>502</c> job-failed with no <c>Location</c> on every authorized
/// poll, whatever the trace id; an ownership mismatch and an expired job answer <c>404</c>.
/// </summary>
/// <remarks>
/// The correlation header is enabled, and each poll of the failed job carries a different value, which
/// the problem body echoes, so the repeated answers are shown to come from different requests. The
/// transient case asserts nothing beyond what the contract guarantees: the failed poll says nothing
/// about the job, and the next poll answers normally.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_JobPollsFailTransientlyTerminallyOrExpire
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string CorrelationHeader = "correlationid";

    private sealed record Poll(
        HttpStatusCode Status,
        string? ProblemType,
        bool HasLocation,
        string? CorrelationId
    );

    private IdentityFixtureControlStub? _stub;
    private IdentityPluginHost? _host;

    private Poll _transientFailed = new(HttpStatusCode.Unused, null, false, null);
    private Poll _afterTransient = new(HttpStatusCode.Unused, null, false, null);
    private Poll _afterTransientAgain = new(HttpStatusCode.Unused, null, false, null);
    private readonly List<(string Sent, Poll Answer)> _failedPolls = [];
    private Poll _failedPollByOtherClient = new(HttpStatusCode.Unused, null, false, null);
    private Poll _expiredPoll = new(HttpStatusCode.Unused, null, false, null);
    private Poll _unexpiredControl = new(HttpStatusCode.Unused, null, false, null);

    [OneTimeSetUp]
    public async Task Setup()
    {
        string district = IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)[0];

        _stub = await IdentityFixtureControlStub.StartAsync();
        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .WithControlStub(_stub)
            .Namespace("ns-a", (IdentityTestClients.TenantOne, district))
            .With("IdentityFixture:PollsUntilComplete", "1");
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings,
            settings: new Dictionary<string, string>
            {
                ["AppSettings:CorrelationIdHeader"] = CorrelationHeader,
            }
        );

        _stub.Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, "ns-a");
        _stub.Grant(IdentityTestClients.ClientB.ClientId, IdentityTestClients.TenantOne, "ns-a");

        using HttpClient clientA = _host.CreateClient(IdentityTestClients.ClientA.Token);
        using HttpClient clientB = _host.CreateClient(IdentityTestClients.ClientB.Token);

        string Results(string token) =>
            IdentityTestClients.Route(IdentityTestClients.TenantOne, district, $"identities/results/{token}");

        // Transient: one failed poll, then the same token answers again.
        string transientToken = await StartJobAsync(clientA, district);
        _stub.FailNextPoll(transientToken);
        _transientFailed = await PollAsync(clientA, Results(transientToken), "transient-1");
        _afterTransient = await PollAsync(clientA, Results(transientToken), "transient-2");
        _afterTransientAgain = await PollAsync(clientA, Results(transientToken), "transient-3");

        // Terminal: every authorized poll answers job-failed, whatever correlation value it carries.
        string failedToken = await StartJobAsync(clientA, district);
        _stub.FailJob(failedToken);
        foreach (string sent in new[] { "failed-poll-1", "failed-poll-2", "failed-poll-3" })
        {
            _failedPolls.Add((sent, await PollAsync(clientA, Results(failedToken), sent)));
        }

        // Ownership is checked before the failure: another client is not told the job failed.
        _failedPollByOtherClient = await PollAsync(clientB, Results(failedToken), "other-client");

        // Expiry: the same kind of job, expired on the control channel, answers not found.
        string expiredToken = await StartJobAsync(clientA, district);
        string unexpiredToken = await StartJobAsync(clientA, district);
        _stub.ExpireJob(expiredToken);
        _expiredPoll = await PollAsync(clientA, Results(expiredToken), "expired");
        _unexpiredControl = await PollAsync(clientA, Results(unexpiredToken), "unexpired");
    }

    private static async Task<string> StartJobAsync(HttpClient client, string district)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            IdentityTestClients.Route(IdentityTestClients.TenantOne, district, "identities/find")
        )
        {
            Content = new StringContent("""["~fixture:async"]""", Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage accepted = await client.SendAsync(request);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return accepted.Headers.Location!.OriginalString.Split('/')[^1];
    }

    private static async Task<Poll> PollAsync(HttpClient client, string route, string correlationId)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, route);
        request.Headers.Add(CorrelationHeader, correlationId);
        using HttpResponseMessage response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        JsonNode? body = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
        return new Poll(
            response.StatusCode,
            body?["type"]?.GetValue<string>(),
            response.Headers.Location is not null,
            body?["correlationId"]?.GetValue<string>()
        );
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
    public void It_answers_upstream_failure_to_a_transiently_failing_poll()
    {
        _transientFailed.Status.Should().Be(HttpStatusCode.BadGateway);
        _transientFailed.ProblemType.Should().Be(IdentityFailureResponse.UpstreamFailureType);
        _transientFailed.HasLocation.Should().BeFalse();
    }

    [Test]
    public void It_answers_the_same_token_after_the_transient_failure()
    {
        _afterTransient.Status.Should().Be(HttpStatusCode.OK);
        _afterTransientAgain.Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_does_not_conclude_the_job_failed_from_the_transient_failure()
    {
        _transientFailed.ProblemType.Should().NotBe(IdentityFailureResponse.JobFailedType);
        _afterTransient.ProblemType.Should().BeNull();
    }

    [Test]
    public void It_answers_job_failed_on_every_authorized_poll_of_a_failed_job()
    {
        _failedPolls.Should().HaveCount(3);
        _failedPolls
            .Select(poll => poll.Answer.Status)
            .Should()
            .OnlyContain(status => status == HttpStatusCode.BadGateway);
        _failedPolls
            .Select(poll => poll.Answer.ProblemType)
            .Should()
            .OnlyContain(type => type == IdentityFailureResponse.JobFailedType);
    }

    [Test]
    public void It_sends_no_location_with_the_job_failed_answers()
    {
        _failedPolls.Select(poll => poll.Answer.HasLocation).Should().OnlyContain(has => !has);
    }

    [Test]
    public void It_answers_job_failed_across_different_trace_ids()
    {
        // Each answer carries the correlation value its own request sent, so the three are three requests.
        _failedPolls
            .Select(poll => poll.Answer.CorrelationId)
            .Should()
            .Equal(_failedPolls.Select(poll => poll.Sent));
        _failedPolls.Select(poll => poll.Answer.CorrelationId).Distinct().Should().HaveCount(3);
    }

    [Test]
    public void It_answers_identity_not_found_rather_than_job_failed_to_a_client_that_does_not_own_the_job()
    {
        _failedPollByOtherClient.Status.Should().Be(HttpStatusCode.NotFound);
        _failedPollByOtherClient.ProblemType.Should().Be(IdentityFailureResponse.NotFoundType);
    }

    [Test]
    public void It_answers_identity_not_found_for_an_expired_job()
    {
        _expiredPoll.Status.Should().Be(HttpStatusCode.NotFound);
        _expiredPoll.ProblemType.Should().Be(IdentityFailureResponse.NotFoundType);
    }

    [Test]
    public void It_answers_a_job_that_was_not_expired()
    {
        _unexpiredControl.Status.Should().Be(HttpStatusCode.OK);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using Serilog.Events;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// F32: for each stage at which the fixture throws a nested, person-shaped exception (factory,
/// constructor, capabilities getter, operation) the host still boots <c>Ready</c>; the first three
/// answer the provider-configuration <c>500</c> without any operation running, and an operation
/// failure answers the upstream-failure <c>502</c>. The sentinel appears in no response and in no log
/// entry above Debug, and does appear at Debug.
/// </summary>
/// <remarks>
/// <para>
/// The host resolves the provider itself and these hosts never resolve it from a test scope, because
/// that would be an extra activation. The control stub's invocation count is the evidence that no
/// operation ran: the fixture reports an invocation before anything else it does.
/// </para>
/// <para>
/// Capabilities are read once per request by the host, which the earlier get-by-id cases show through
/// the fixture's read-count probe; a throwing stage cannot return that probe, so here the single
/// provider invocation of the operation variant is the observable. Request cancellation is not
/// exercised here: forcing a cancellation at a known point needs a timing-dependent hook, and the
/// boundary's cancellation logging is covered by the Core boundary unit tests.
/// </para>
/// </remarks>
[Category("PluginIntegration")]
[TestFixture("Factory", HttpStatusCode.InternalServerError, false)]
[TestFixture("Constructor", HttpStatusCode.InternalServerError, false)]
[TestFixture("Capabilities", HttpStatusCode.InternalServerError, false)]
[TestFixture("Operation", HttpStatusCode.BadGateway, true)]
public sealed class Given_TheProviderFailsAtAStage(
    string throwAt,
    HttpStatusCode expectedStatus,
    bool operationReachesTheProvider
)
{
    private const string Sentinel = "SENTINEL-Jane-Doe-1999-01-01";

    private IdentityHttpRun? _run;
    private Exception? _bootFailure;
    private IReadOnlyList<LogEvent> _startupEvents = [];
    private IdentityHttpOutcome _answer = null!;
    private IReadOnlyList<LogEvent> _events = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync(
            fixtureSettings: new Dictionary<string, string> { ["IdentityFixture:ThrowAt"] = throwAt }
        );

        _bootFailure = _run.Host.TryBoot();
        _startupEvents = _run.Host.Capture.Events;
        _answer = await _run.GetAsync("identities/0123456789abcdef0123456789abcdef");
        _events = _run.Host.Capture.Events;
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
    public void It_boots_ready()
    {
        _bootFailure.Should().BeNull();
        PluginHostProbe
            .ReadStartupStatus(_run!.Host.StartupStatusFilePath)["State"]
            ?.GetValue<string>()
            .Should()
            .Be("Ready");
    }

    [Test]
    public void It_logged_no_sentinel_during_startup()
    {
        IdentityLogAssertions.EventsCarrying(_startupEvents, Sentinel).Should().BeEmpty();
    }

    [Test]
    public void It_answers_the_status_and_problem_type_of_the_failed_stage()
    {
        _answer.Status.Should().Be(expectedStatus);
        _answer
            .ProblemType.Should()
            .Be(
                operationReachesTheProvider
                    ? IdentityFailureResponse.UpstreamFailureType
                    : IdentityFailureResponse.ProviderConfigurationType
            );
    }

    [Test]
    public void It_invokes_an_operation_only_when_the_failing_stage_is_the_operation()
    {
        _run!.Stub.InvocationCount().Should().Be(operationReachesTheProvider ? 1 : 0);
    }

    [Test]
    public void It_leaves_the_sentinel_out_of_the_response()
    {
        _answer.Text.Should().NotContain(Sentinel).And.NotContain("Jane Doe");
    }

    [Test]
    public void It_leaves_the_sentinel_out_of_every_log_entry_above_debug()
    {
        IdentityLogAssertions
            .EventsCarrying(_events.Where(logEvent => logEvent.Level > LogEventLevel.Debug), Sentinel)
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_logs_the_sentinel_at_debug_where_the_capture_can_see_it()
    {
        IdentityLogAssertions
            .EventsCarrying(_events.Where(logEvent => logEvent.Level == LogEventLevel.Debug), Sentinel)
            .Should()
            .NotBeEmpty();
    }
}

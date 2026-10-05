// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;
using Serilog.Events;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A get-by-id and a successful results poll leave neither the UniqueId nor the job token in any
/// captured event - not the structured <c>Path</c> and <c>RequestPath</c> properties, not the rendered
/// message, of the frontend and Core completion events, the framework's hosting-diagnostics events or
/// anything else the host logged.
/// </summary>
/// <remarks>
/// <para>
/// A leak scan over a capture that never held the events proves nothing, so each request's slice of
/// the capture is first shown to hold both layers' completion events, and a find request, whose route
/// carries no identifier and is not redacted, is shown to reach the capture as hosting-diagnostics
/// events with its path intact. The sanitizer strips braces, so the logged identity path has no
/// template literal; the cases assert the identifier is absent rather than matching a template.
/// </para>
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_IdentityRequestsCarryAnIdentifierInTheirPath
{
    private IdentityHttpRun? _run;
    private string _uniqueId = "";
    private string _token = "";
    private IdentityHttpOutcome _getById = null!;
    private IdentityHttpOutcome _resultsComplete = null!;
    private IReadOnlyList<LogEvent> _getByIdEvents = [];
    private IReadOnlyList<LogEvent> _resultsEvents = [];
    private IReadOnlyList<LogEvent> _findEvents = [];
    private IReadOnlyList<LogEvent> _allEvents = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();

        IdentityHttpOutcome created = await _run.PostAsync("identities", """{ "LastSurname": "Rivera" }""");
        _uniqueId = created.Body!.GetValue<string>();

        (_getById, _getByIdEvents) = await Capturing(() => _run.GetAsync($"identities/{_uniqueId}"));

        (_, _findEvents) = await Capturing(() => _run.PostAsync("identities/find", $"""["{_uniqueId}"]"""));

        IdentityHttpOutcome accepted = await _run.PostAsync("identities/find", """["~fixture:async"]""");
        _token = accepted.Location!.Split('/')[^1];
        _ = await _run.GetAsync($"identities/results/{_token}");
        (_resultsComplete, _resultsEvents) = await Capturing(() =>
            _run.GetAsync($"identities/results/{_token}")
        );

        _allEvents = _run.Host.Capture.Events;
    }

    private async Task<(IdentityHttpOutcome Outcome, IReadOnlyList<LogEvent> Events)> Capturing(
        Func<Task<IdentityHttpOutcome>> request
    )
    {
        int before = _run!.Host.Capture.Events.Count;
        IdentityHttpOutcome outcome = await request();
        return (outcome, [.. _run.Host.Capture.Events.Skip(before)]);
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
    public void It_answered_the_get_by_id_and_the_complete_poll()
    {
        _getById.Status.Should().Be(HttpStatusCode.OK);
        _getById.Text.Should().Contain(_uniqueId);
        _resultsComplete.Status.Should().Be(HttpStatusCode.OK);
        _resultsComplete.Text.Should().Contain("Complete").And.NotContain("Incomplete");
    }

    [Test]
    public void It_logged_the_frontend_and_core_completion_events_for_the_get_by_id()
    {
        _getByIdEvents.Where(IdentityLogAssertions.IsFrontendCompletionEvent).Should().NotBeEmpty();
        _getByIdEvents.Where(IdentityLogAssertions.IsCoreCompletionEvent).Should().NotBeEmpty();
    }

    [Test]
    public void It_logged_the_frontend_and_core_completion_events_for_the_complete_poll()
    {
        _resultsEvents.Where(IdentityLogAssertions.IsFrontendCompletionEvent).Should().NotBeEmpty();
        _resultsEvents.Where(IdentityLogAssertions.IsCoreCompletionEvent).Should().NotBeEmpty();
    }

    [Test]
    public void It_captures_the_hosting_diagnostics_events_of_a_route_that_is_not_redacted()
    {
        string findPath = _run!.Route("identities/find");

        IdentityLogAssertions
            .HostingDiagnosticsEventsForPath(_findEvents, findPath)
            .Should()
            .NotBeEmpty("the scan below is only meaningful if hosting events reach the capture");
    }

    [Test]
    public void It_can_see_a_route_segment_in_the_captured_events()
    {
        IdentityLogAssertions.EventsLeaking(_findEvents, "identities/find").Should().NotBeEmpty();
    }

    [Test]
    public void It_logged_no_hosting_diagnostics_event_for_the_identifier_bearing_routes()
    {
        _getByIdEvents
            .Concat(_resultsEvents)
            .Where(logEvent =>
                IdentityLogAssertions.ScalarProperty(logEvent, "SourceContext")
                == IdentityLogAssertions.HostingDiagnosticsSourceContext
            )
            .Where(logEvent => IdentityLogAssertions.ScalarProperty(logEvent, "Path") is not null)
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_leaves_the_unique_id_out_of_every_captured_event()
    {
        IdentityLogAssertions.EventsLeaking(_allEvents, _uniqueId).Should().BeEmpty();
    }

    [Test]
    public void It_leaves_the_job_token_out_of_every_captured_event()
    {
        IdentityLogAssertions.EventsLeaking(_allEvents, _token).Should().BeEmpty();
    }
}

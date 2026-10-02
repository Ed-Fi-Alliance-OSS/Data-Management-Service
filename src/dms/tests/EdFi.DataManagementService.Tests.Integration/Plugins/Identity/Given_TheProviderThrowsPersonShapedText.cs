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
/// F31: an operation that throws an exception whose message is person-shaped text answers the
/// sanitized upstream-failure <c>502</c>, and neither the response nor any log entry above Debug
/// carries that text.
/// </summary>
/// <remarks>
/// The Debug log is the positive control: the host logs the full exception there, so the capture is
/// shown able to see the text. The same host's ordinary create is the negative control that the
/// failure, not the route, is what produced the <c>502</c>.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheProviderThrowsPersonShapedText
{
    private const string PersonText = "Jane Doe born 1999-01-01";
    private const string Sentinel = "SENTINEL-Jane-Doe-1999-01-01";

    private IdentityHttpRun? _run;
    private IdentityHttpOutcome _thrown = null!;
    private IdentityHttpOutcome _ordinary = null!;
    private IReadOnlyList<LogEvent> _events = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();

        _ordinary = await _run.PostAsync("identities", """{ "LastSurname": "Rivera" }""");
        _thrown = await _run.PostAsync("identities", """{ "~FixtureReturn": "throw-person" }""");
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

    private IEnumerable<LogEvent> EventsAboveDebug =>
        _events.Where(logEvent => logEvent.Level > LogEventLevel.Debug);

    [Test]
    public void It_answers_the_sanitized_upstream_failure()
    {
        _thrown.Status.Should().Be(HttpStatusCode.BadGateway);
        _thrown.ProblemType.Should().Be(IdentityFailureResponse.UpstreamFailureType);
    }

    [Test]
    public void It_answers_the_same_host_an_ordinary_create()
    {
        _ordinary.Status.Should().Be(HttpStatusCode.OK);
    }

    [TestCase(PersonText)]
    [TestCase(Sentinel)]
    [TestCase("Jane Doe")]
    public void It_leaves_the_person_text_out_of_the_response(string text)
    {
        _thrown.Text.Should().NotContain(text);
    }

    [TestCase(PersonText)]
    [TestCase(Sentinel)]
    [TestCase("Jane Doe")]
    public void It_leaves_the_person_text_out_of_every_log_entry_above_debug(string text)
    {
        IdentityLogAssertions.EventsCarrying(EventsAboveDebug, text).Should().BeEmpty();
    }

    [Test]
    public void It_logged_a_failure_level_entry_for_the_request()
    {
        EventsAboveDebug.Should().Contain(logEvent => logEvent.Level >= LogEventLevel.Error);
    }

    [Test]
    public void It_logs_the_person_text_only_at_debug_where_the_capture_can_see_it()
    {
        IReadOnlyList<LogEvent> debugEvents =
        [
            .. _events.Where(logEvent => logEvent.Level == LogEventLevel.Debug),
        ];

        IdentityLogAssertions.EventsCarrying(debugEvents, Sentinel).Should().NotBeEmpty();
    }
}

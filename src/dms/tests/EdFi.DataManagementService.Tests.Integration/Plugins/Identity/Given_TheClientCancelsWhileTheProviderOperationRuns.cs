// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using FluentAssertions;
using Serilog.Events;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A client cancels a create while the loaded fixture's operation is
/// running, and the fixture answers the cancellation with an <see cref="OperationCanceledException"/>
/// whose message and inner exception carry person-shaped text. That text reaches no log entry above
/// Debug, through the boundary's cancellation handling and every outer layer's exception logging, and
/// does appear at Debug.
/// </summary>
/// <remarks>
/// <para>
/// Each step waits on a signal, never on time. The fixture reports on the control channel that it is
/// waiting for its cancellation token, and the client cancels only once that report has arrived, so
/// the cancellation always lands inside the operation. The capture is read only after the hosting
/// layer's request-finished event for the create, which the host writes once every middleware has
/// unwound, so the capture holds everything the host logged for the request.
/// </para>
/// <para>
/// The Debug event is the positive control: the boundary logs the fixture's exception there, so the
/// capture is shown able to see the text it is scanned for.
/// </para>
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheClientCancelsWhileTheProviderOperationRuns
{
    private const string PersonText = "Jane Doe born 1999-01-01";
    private const string Sentinel = "SENTINEL-Jane-Doe-1999-01-01";
    private const string BoundaryCancellationTemplate =
        "Identity provider observed request cancellation during {Stage} for {Operation} - TraceId: {TraceId}";

    // Only bounds a broken run; no step is ordered by it.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private IdentityHttpRun? _run;
    private Exception? _clientException;
    private IReadOnlyList<LogEvent> _events = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();
        string path = _run.Route("identities");

        Task awaitingCancellation = _run.Stub.WaitForEventAsync("create", "awaiting-cancellation");
        Task requestFinished = _run.Host.Capture.WaitForEventAsync(logEvent =>
            IsRequestFinished(logEvent, path)
        );

        using CancellationTokenSource cancellation = new();
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(
                """{ "~FixtureReturn": "cancel-person" }""",
                Encoding.UTF8,
                "application/json"
            ),
        };
        Task<HttpResponseMessage> sent = _run.Client.SendAsync(request, cancellation.Token);

        await awaitingCancellation.WaitAsync(HangGuard);
        await cancellation.CancelAsync();

        try
        {
            using HttpResponseMessage unexpected = await sent.WaitAsync(HangGuard);
        }
        catch (Exception exception)
        {
            _clientException = exception;
        }

        await requestFinished.WaitAsync(HangGuard);
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

    private static bool IsRequestFinished(LogEvent logEvent, string path) =>
        IdentityLogAssertions.ScalarProperty(logEvent, "SourceContext")
            == IdentityLogAssertions.HostingDiagnosticsSourceContext
        && IdentityLogAssertions.ScalarProperty(logEvent, "Path") == path
        && logEvent.MessageTemplate.Text.StartsWith("Request finished", StringComparison.Ordinal);

    private IEnumerable<LogEvent> EventsAboveDebug =>
        _events.Where(logEvent => logEvent.Level > LogEventLevel.Debug);

    private IReadOnlyList<LogEvent> DebugEvents =>
        [.. _events.Where(logEvent => logEvent.Level == LogEventLevel.Debug)];

    [Test]
    public void It_cancels_the_client_request()
    {
        _clientException.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Test]
    public void It_invokes_the_fixture_create_exactly_once()
    {
        _run!.Stub.InvocationCount("create").Should().Be(1);
        _run.Stub.InvocationCount().Should().Be(1);
    }

    [TestCase(PersonText)]
    [TestCase(Sentinel)]
    [TestCase("Jane Doe")]
    public void It_leaves_the_person_text_out_of_every_log_entry_above_debug(string text)
    {
        IdentityLogAssertions.EventsCarrying(EventsAboveDebug, text).Should().BeEmpty();
    }

    [TestCase(PersonText)]
    [TestCase(Sentinel)]
    [TestCase("Jane Doe")]
    public void It_leaves_the_person_text_out_of_the_exception_the_client_observes(string text)
    {
        _clientException!.ToString().Should().NotContain(text);
    }

    [Test]
    public void It_logs_the_person_text_at_debug_where_the_capture_can_see_it()
    {
        IdentityLogAssertions.EventsCarrying(DebugEvents, Sentinel).Should().NotBeEmpty();
    }

    [Test]
    public void It_logs_the_fixture_exception_as_a_cancellation_of_the_operation_stage()
    {
        LogEvent[] cancellations =
        [
            .. DebugEvents.Where(logEvent =>
                logEvent.MessageTemplate.Text == BoundaryCancellationTemplate
                && IdentityLogAssertions.ScalarProperty(logEvent, "Stage") == "Invoke"
            ),
        ];

        cancellations.Should().ContainSingle();
        cancellations[0].Exception!.ToString().Should().Contain(Sentinel);
    }
}

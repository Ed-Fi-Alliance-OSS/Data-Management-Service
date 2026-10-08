// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// The correlation ID modification notice: an Information-level event emitted by
/// <see cref="LoggingMiddleware"/> when the correlation ID a request is logged and answered under
/// is not the value the client sent.
/// </summary>
/// <remarks>
/// The two things these tests exist to hold are in tension with each other, which is why both are
/// asserted exactly rather than by presence: the event has to carry enough to diagnose the
/// adjustment, and it must carry no part of the client-supplied value. The hostile inputs below
/// therefore each hide a sentinel that normalization removes, so "the original is not logged" is
/// an assertion about a specific string rather than about a general shape.
/// </remarks>
[TestFixture]
[Parallelizable]
public class Given_A_Client_Supplied_Correlation_Id_That_Normalization_Changes
{
    private const string CorrelationHeader = "x-correlation-id";
    private const int ConfiguredMaxLength = AppSettings.MinimumCorrelationIdMaxLength;
    private const string ServerGeneratedIdentifier = "0HNCTN1IRQMDG:00000001";

    /// <summary>
    /// The full message template, pinned here so a change to the shape a collector parses is a
    /// deliberate act rather than a side effect of editing the line.
    /// </summary>
    private const string MessageTemplate =
        "{EventName}: Client-supplied correlation ID was modified by normalization; the original value is deliberately not logged. "
        + "SuppliedLength {SuppliedLength}, NormalizedLength {NormalizedLength}, CharactersRemoved {CharactersRemoved}, "
        + "Truncated {Truncated}, FellBackToServerIdentifier {FellBackToServerIdentifier}, TraceId {TraceId}";

    /// <summary>
    /// The tail of the hostile value, placed past the 64-character cap so truncation removes it.
    /// It is what makes "the original value is not logged" checkable: unlike the control
    /// characters, it is printable, so a log line that reflected the original in any form -
    /// raw, sanitized, or truncated to a different length - would contain it.
    /// </summary>
    private const string DroppedTailSentinel = "-TAIL-SENTINEL-MUST-NOT-BE-LOGGED";

    /// <summary>
    /// 97 characters: "front", CR, LF, 57 'x', then the sentinel. Truncating to 64 keeps
    /// "front", CR, LF and the 57 'x'; removing the two control characters then leaves 62. Both
    /// adjustments therefore apply to one value, and the sentinel survives neither.
    /// </summary>
    private static readonly string HostileCorrelationId =
        "front\r\n" + new string('x', 57) + DroppedTailSentinel;

    private static readonly string NormalizedHostileCorrelationId = "front" + new string('x', 57);

    [Test]
    public async Task It_reports_the_modification_once_at_information_level()
    {
        TestLogger<LoggingMiddleware> logger = await InvokeWithCorrelationHeader(HostileCorrelationId);

        TestLogger<LoggingMiddleware>.LogEntry modification = SingleModificationEntry(logger);

        modification.Level.Should().Be(LogLevel.Information);
        modification.EventId.Id.Should().Be(1228003);
        modification.EventId.Name.Should().Be("CorrelationIdModified");
        modification.State.GetStructuredProperty("{OriginalFormat}").Should().Be(MessageTemplate);
        modification.State.ContainStructuredProperty("EventName", "CorrelationIdModified");
    }

    [Test]
    public async Task It_reports_only_derived_facts_about_the_adjustment()
    {
        TestLogger<LoggingMiddleware> logger = await InvokeWithCorrelationHeader(HostileCorrelationId);

        TestLogger<LoggingMiddleware>.LogEntry modification = SingleModificationEntry(logger);

        modification.State.ContainStructuredProperty("SuppliedLength", HostileCorrelationId.Length);
        modification.State.ContainStructuredProperty(
            "NormalizedLength",
            NormalizedHostileCorrelationId.Length
        );
        modification.State.ContainStructuredProperty("CharactersRemoved", true);
        modification.State.ContainStructuredProperty("Truncated", true);

        // The distinguishing flag. This value was adjusted, not discarded, so an operator reading
        // it knows the client's own identifier scheme is still recognizable in the TraceId.
        modification.State.ContainStructuredProperty("FellBackToServerIdentifier", false);

        // The one value on the line that is searchable, and the key the same request's
        // HttpRequestCompleted event and error response body carry.
        modification.State.ContainStructuredProperty("TraceId", NormalizedHostileCorrelationId);
    }

    [Test]
    public async Task It_never_writes_any_form_of_the_original_value()
    {
        TestLogger<LoggingMiddleware> logger = await InvokeWithCorrelationHeader(HostileCorrelationId);

        string[] logged = [.. AllLoggedValues(logger)];

        logged
            .Should()
            .NotContain(
                value => value.Contains(DroppedTailSentinel, StringComparison.Ordinal),
                "the client-supplied correlation ID is the hostile input normalization exists to "
                    + "defang, so no part of it may reach a log sink. Logged values were: {0}",
                string.Join(" | ", logged)
            );

        logged
            .Should()
            .NotContain(
                value => value.Contains(HostileCorrelationId, StringComparison.Ordinal),
                "the original value must not be reflected whole either. Logged values were: {0}",
                string.Join(" | ", logged)
            );

        logged
            .Should()
            .NotContain(
                value => value.Any(char.IsControl),
                "reflecting the original's control characters would reopen the log forging vector "
                    + "this notice reports on. Logged values were: {0}",
                string.Join(" | ", logged)
            );
    }

    [Test]
    public async Task It_distinguishes_a_discarded_value_from_an_adjusted_one()
    {
        // A value made up only of whitespace that the allowlist keeps - U+00A0 NO-BREAK SPACE -
        // so nothing is truncated and nothing is removed, and the *only* reason the client's
        // value is not the correlation ID is the blank-after-normalization fallback. That
        // isolates the fallback flag from the other two.
        TestLogger<LoggingMiddleware> logger = await InvokeWithCorrelationHeader("\u00A0\u00A0\u00A0");

        TestLogger<LoggingMiddleware>.LogEntry modification = SingleModificationEntry(logger);

        modification.State.ContainStructuredProperty("FellBackToServerIdentifier", true);
        modification.State.ContainStructuredProperty("Truncated", false);
        modification.State.ContainStructuredProperty("CharactersRemoved", false);
        modification.State.ContainStructuredProperty("SuppliedLength", 3);
        modification.State.ContainStructuredProperty("NormalizedLength", ServerGeneratedIdentifier.Length);

        // The server-generated identifier replaced the client's value in full, which is the
        // outcome an integrator is most likely to be confused by.
        modification.State.ContainStructuredProperty("TraceId", ServerGeneratedIdentifier);
    }

    [Test]
    public async Task It_reports_a_discarded_value_that_was_also_filtered_as_a_fallback()
    {
        // Control and format characters only, so the allowlist empties the value and the fallback
        // then applies. Both flags are true, and the fallback flag is what tells an operator the
        // client's identifier is gone rather than merely shortened.
        TestLogger<LoggingMiddleware> logger = await InvokeWithCorrelationHeader("\t\u200B\u0000");

        TestLogger<LoggingMiddleware>.LogEntry modification = SingleModificationEntry(logger);

        modification.State.ContainStructuredProperty("CharactersRemoved", true);
        modification.State.ContainStructuredProperty("FellBackToServerIdentifier", true);
        modification.State.ContainStructuredProperty("TraceId", ServerGeneratedIdentifier);
    }

    [Test]
    public async Task It_stays_silent_for_a_value_normalization_leaves_alone()
    {
        const string CleanCorrelationId = "clean-correlation-id";

        TestLogger<LoggingMiddleware> logger = await InvokeWithCorrelationHeader(CleanCorrelationId);

        // The arrange-step guard: without it, a middleware that never ran at all would satisfy
        // "no modification notice" while proving nothing.
        logger
            .Entries.Should()
            .Contain(
                entry => entry.EventId.Name == "HttpRequestCompleted",
                "the request must actually have been logged for the silence below to mean anything"
            );
        CompletedEntry(logger).State.ContainStructuredProperty("TraceId", CleanCorrelationId);

        AssertNoModificationNotice(
            logger,
            "a correlation ID that survives normalization unchanged is the normal path, and a "
                + "per-request notice on it would be unacceptable log volume"
        );
    }

    [Test]
    public async Task It_stays_silent_when_the_request_supplies_no_correlation_header()
    {
        TestLogger<LoggingMiddleware> logger = await InvokeWithCorrelationHeader(headerValue: null);

        CompletedEntry(logger).State.ContainStructuredProperty("TraceId", ServerGeneratedIdentifier);

        AssertNoModificationNotice(
            logger,
            "falling back to the server-generated identifier because the client sent nothing is "
                + "not a modification of anything the client supplied"
        );
    }

    [Test]
    public async Task It_stays_silent_when_the_host_has_disabled_client_supplied_correlation_ids()
    {
        // AppSettings:CorrelationIdHeader empty. The client's value is never read, so there is
        // nothing to have modified - even though the correlation ID is not what the client sent.
        TestLogger<LoggingMiddleware> logger = await InvokeWithCorrelationHeader(
            HostileCorrelationId,
            configuredHeaderName: string.Empty
        );

        CompletedEntry(logger).State.ContainStructuredProperty("TraceId", ServerGeneratedIdentifier);

        AssertNoModificationNotice(
            logger,
            "a host that configures no correlation header never reads the client's value, so no "
                + "value of its was modified"
        );
    }

    [Test]
    public async Task It_stays_silent_when_the_correlation_header_is_present_but_empty()
    {
        // HeaderDictionary's indexer *removes* the key when assigned a value StringValues
        // considers empty, so Headers[name] = "" would silently reproduce the header-absent case
        // and this test would be vacuous while appearing green. The backing store is seeded
        // directly instead, and the presence guard below keeps the distinction honest.
        HeaderDictionary headers = new(
            new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
            {
                [CorrelationHeader] = new StringValues(string.Empty),
            }
        );
        FeatureCollection features = new();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature { Headers = headers });

        // Seeding the feature collection replaces DefaultHttpContext's own defaults wholesale, so
        // the response feature has to be supplied too or reading Response.StatusCode throws.
        features.Set<IHttpResponseFeature>(new HttpResponseFeature { StatusCode = 200 });
        DefaultHttpContext httpContext = new(features) { TraceIdentifier = ServerGeneratedIdentifier };
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/ed-fi/students";

        httpContext
            .Request.Headers.ContainsKey(CorrelationHeader)
            .Should()
            .BeTrue("the header has to genuinely be present for the silence below to mean anything");

        TestLogger<LoggingMiddleware> logger = await Invoke(httpContext, CorrelationHeader);

        CompletedEntry(logger).State.ContainStructuredProperty("TraceId", ServerGeneratedIdentifier);

        AssertNoModificationNotice(
            logger,
            "an empty header value is the same starting point as an omitted header, so it carries "
                + "nothing that could have been modified"
        );
    }

    [Test]
    public async Task It_normalizes_the_correlation_id_exactly_once_per_request()
    {
        // docs/LOGGING.md claims the value is normalized once rather than merely identically, so
        // this asserts the cache itself, not the agreement a pure function would produce anyway.
        // The second extraction is handed settings naming a *different* header, which would
        // otherwise yield the server-generated identifier; getting the middleware's value back is
        // only possible if the cached result was read rather than recomputed.
        DefaultHttpContext httpContext = new() { TraceIdentifier = ServerGeneratedIdentifier };
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/ed-fi/students";
        httpContext.Request.Headers[CorrelationHeader] = HostileCorrelationId;

        await Invoke(httpContext, CorrelationHeader);

        Core.External.Model.TraceId laterCallSite = AspNetCoreFrontend.ExtractTraceIdFrom(
            httpContext.Request,
            Options.Create(
                new AppSettings
                {
                    AuthenticationService = "http://localhost/connect/token",
                    Datastore = "postgresql",
                    CorrelationIdHeader = "some-other-header",
                    CorrelationIdMaxLength = ConfiguredMaxLength,
                }
            )
        );

        laterCallSite
            .Value.Should()
            .Be(
                NormalizedHostileCorrelationId,
                "every call site after the request-logging middleware must read the one value it "
                    + "cached on HttpContext.Items rather than re-deriving its own"
            );
    }

    private static void AssertNoModificationNotice(TestLogger<LoggingMiddleware> logger, string because)
    {
        string[] notices = [.. ModificationEntries(logger).Select(Describe)];

        notices.Should().BeEmpty(because + ". Notices emitted: {0}", string.Join(" | ", notices));
    }

    private static TestLogger<LoggingMiddleware>.LogEntry SingleModificationEntry(
        TestLogger<LoggingMiddleware> logger
    ) =>
        ModificationEntries(logger)
            .Should()
            .ContainSingle(
                "a modified correlation ID must be reported exactly once per request, but the "
                    + "middleware emitted: {0}",
                string.Join(" | ", logger.Entries.Select(Describe))
            )
            .Subject;

    private static TestLogger<LoggingMiddleware>.LogEntry CompletedEntry(
        TestLogger<LoggingMiddleware> logger
    ) => logger.Entries.Single(entry => entry.EventId.Name == "HttpRequestCompleted");

    private static IEnumerable<TestLogger<LoggingMiddleware>.LogEntry> ModificationEntries(
        TestLogger<LoggingMiddleware> logger
    ) => logger.Entries.Where(entry => entry.EventId == CorrelationIdLoggingEventIds.CorrelationIdModified);

    private static string Describe(TestLogger<LoggingMiddleware>.LogEntry entry) =>
        $"[{entry.Level} {entry.EventId.Id}/{entry.EventId.Name}] {entry.State}";

    /// <summary>
    /// Every structured value the middleware wrote, across event states and scopes alike. A log
    /// line that leaked the original through a scope property rather than through the notice
    /// itself would still be a leak.
    /// </summary>
    private static IEnumerable<string> AllLoggedValues(TestLogger<LoggingMiddleware> logger) =>
        logger
            .Entries.SelectMany(entry => new object?[] { entry.State }.Concat(entry.ActiveScopes))
            .Concat(logger.Scopes)
            .StructuredValues();

    private static async Task<TestLogger<LoggingMiddleware>> InvokeWithCorrelationHeader(
        string? headerValue,
        string configuredHeaderName = CorrelationHeader
    )
    {
        DefaultHttpContext httpContext = new() { TraceIdentifier = ServerGeneratedIdentifier };
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/ed-fi/students";
        httpContext.Response.StatusCode = 200;
        if (headerValue is not null)
        {
            httpContext.Request.Headers[CorrelationHeader] = headerValue;
        }

        return await Invoke(httpContext, configuredHeaderName);
    }

    private static async Task<TestLogger<LoggingMiddleware>> Invoke(
        DefaultHttpContext httpContext,
        string configuredHeaderName
    )
    {
        TestLogger<LoggingMiddleware> logger = new();
        LoggingMiddleware middleware = new(
            _ => Task.CompletedTask,
            Options.Create(
                new AppSettings
                {
                    AuthenticationService = "http://localhost/connect/token",
                    Datastore = "postgresql",
                    CorrelationIdHeader = configuredHeaderName,
                    CorrelationIdMaxLength = ConfiguredMaxLength,
                }
            )
        );

        await middleware.Invoke(httpContext, logger);

        return logger;
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Covers the single ingestion point for the correlation ID. Because every production
/// TraceId reachable from an HTTP request is built here, these assertions are what make the
/// normalization guarantee hold for every downstream log event and error response body.
/// </summary>
/// <remarks>
/// Several fixtures below deliberately re-pin normalizer behavior with the same literals as
/// CorrelationIdNormalizerTests — control characters, upstream punctuation, and both truncation
/// bounds — because at this seam they are ingestion-point guarantees, not just normalizer unit
/// tests. A change to the allowlist or to the cap will therefore surface here too, not only in
/// CorrelationIdNormalizerTests. The one thing not repeated here is the truncate-then-filter
/// *order*, which is pinned once in CorrelationIdNormalizerTests, the single definition of that
/// policy, and again end to end in CorrelationIdParityTests.
/// </remarks>
[TestFixture]
[Parallelizable]
public class ExtractTraceIdFromTests
{
    private const string CorrelationHeader = "x-correlation-id";

    private static IOptions<AppSettings> AppSettingsFor(
        string correlationHeader,
        int correlationIdMaxLength = AppSettings.DefaultCorrelationIdMaxLength
    ) =>
        Options.Create(
            new AppSettings
            {
                AuthenticationService = "http://localhost/connect/token",
                Datastore = "postgresql",
                CorrelationIdHeader = correlationHeader,
                CorrelationIdMaxLength = correlationIdMaxLength,
            }
        );

    private static HttpRequest RequestWith(string? headerName, string? headerValue, string traceIdentifier)
    {
        DefaultHttpContext httpContext = new() { TraceIdentifier = traceIdentifier };
        if (headerName is not null && headerValue is not null)
        {
            httpContext.Request.Headers[headerName] = headerValue;
        }

        return httpContext.Request;
    }

    [TestFixture]
    public class Given_A_Clean_Client_Supplied_Correlation_Id : ExtractTraceIdFromTests
    {
        private TraceId _traceId;

        [SetUp]
        public void Setup()
        {
            _traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(CorrelationHeader, "test-correlationId", "host-trace-identifier"),
                AppSettingsFor(CorrelationHeader)
            );
        }

        [Test]
        public void It_returns_the_client_value_unchanged()
        {
            _traceId.Value.Should().Be("test-correlationId");
        }
    }

    [TestFixture]
    public class Given_A_Client_Supplied_Correlation_Id_From_An_Upstream_Scheme : ExtractTraceIdFromTests
    {
        private const string UpstreamId = "a+b=c{d}e@f|g,h#i(j)k[l]m<n>o";

        private TraceId _traceId;

        [SetUp]
        public void Setup()
        {
            _traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(CorrelationHeader, UpstreamId, "host-trace-identifier"),
                AppSettingsFor(CorrelationHeader)
            );
        }

        [Test]
        public void It_preserves_printable_punctuation()
        {
            _traceId.Value.Should().Be(UpstreamId);
        }
    }

    [TestFixture]
    public class Given_A_Client_Supplied_Correlation_Id_With_Control_Characters : ExtractTraceIdFromTests
    {
        private TraceId _traceId;

        [SetUp]
        public void Setup()
        {
            // HTTP header values cannot literally carry a bare CR or LF through a real socket,
            // but a DefaultHttpContext can, and so can any in-process caller. The filter is the
            // control that keeps the value from forging a log line regardless of how it arrived.
            _traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(CorrelationHeader, "trace\r\nid\twith\0control", "host-trace-identifier"),
                AppSettingsFor(CorrelationHeader)
            );
        }

        [Test]
        public void It_removes_every_control_character()
        {
            _traceId.Value.Should().Be("traceidwithcontrol");
        }
    }

    [TestFixture]
    public class Given_An_Over_Length_Client_Supplied_Correlation_Id : ExtractTraceIdFromTests
    {
        private TraceId _traceIdAtDefaultBound;
        private TraceId _traceIdAtConfiguredBound;

        [SetUp]
        public void Setup()
        {
            _traceIdAtDefaultBound = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(
                    CorrelationHeader,
                    new string('a', AppSettings.DefaultCorrelationIdMaxLength + 45),
                    "host-trace-identifier"
                ),
                AppSettingsFor(CorrelationHeader)
            );

            _traceIdAtConfiguredBound = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(CorrelationHeader, new string('b', 100), "host-trace-identifier"),
                AppSettingsFor(CorrelationHeader, correlationIdMaxLength: 16)
            );
        }

        [Test]
        public void It_caps_at_the_default_maximum_length()
        {
            _traceIdAtDefaultBound
                .Value.Should()
                .Be(new string('a', AppSettings.DefaultCorrelationIdMaxLength));
        }

        [Test]
        public void It_caps_at_the_host_configured_maximum_length()
        {
            _traceIdAtConfiguredBound.Value.Should().Be(new string('b', 16));
        }
    }

    [TestFixture]
    public class Given_No_Correlation_Header_Is_Supplied : ExtractTraceIdFromTests
    {
        private TraceId _traceId;

        [SetUp]
        public void Setup()
        {
            _traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(null, null, "0HNCTN1IRQMDG:00000001"),
                AppSettingsFor(CorrelationHeader)
            );
        }

        [Test]
        public void It_falls_back_to_the_server_generated_trace_identifier_unchanged()
        {
            _traceId.Value.Should().Be("0HNCTN1IRQMDG:00000001");
        }
    }

    [TestFixture]
    public class Given_An_Empty_Correlation_Header_Setting : ExtractTraceIdFromTests
    {
        private TraceId _traceId;

        [SetUp]
        public void Setup()
        {
            _traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith("correlationid", "client-supplied-value", "0HNCTN1IRQMDG:00000002"),
                AppSettingsFor(string.Empty)
            );
        }

        [Test]
        public void It_ignores_the_client_value_because_the_feature_is_disabled()
        {
            _traceId.Value.Should().Be("0HNCTN1IRQMDG:00000002");
        }
    }

    [TestFixture]
    public class Given_An_Over_Length_Server_Generated_Trace_Identifier : ExtractTraceIdFromTests
    {
        private TraceId _traceId;

        [SetUp]
        public void Setup()
        {
            // FR-LOG-3 and FR-LOG-4 apply to a system-generated correlation ID as well.
            _traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(null, null, new string('z', 40)),
                AppSettingsFor(CorrelationHeader, correlationIdMaxLength: 12)
            );
        }

        [Test]
        public void It_normalizes_the_fallback_branch_too()
        {
            _traceId.Value.Should().Be(new string('z', 12));
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Header_Present_With_An_Empty_Value : ExtractTraceIdFromTests
    {
        private TraceId _traceId;
        private bool _headerWasActuallyPresent;

        [SetUp]
        public void Setup()
        {
            // The header must genuinely be present for this fixture to mean anything.
            // HeaderDictionary's indexer *removes* the key when assigned a StringValues that
            // StringValues.IsNullOrEmpty considers empty, so Headers[name] = "" would silently
            // reproduce the header-absent case above and this test would be vacuous while
            // appearing green. Seeding the backing store bypasses that setter, and the
            // presence assertion below is what keeps the distinction honest.
            HeaderDictionary headers = new(
                new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
                {
                    [CorrelationHeader] = new StringValues(string.Empty),
                }
            );
            FeatureCollection features = new();
            features.Set<IHttpRequestFeature>(new HttpRequestFeature { Headers = headers });
            DefaultHttpContext httpContext = new(features) { TraceIdentifier = "0HNCTN1IRQMDG:00000004" };

            _headerWasActuallyPresent = httpContext.Request.Headers.ContainsKey(CorrelationHeader);
            _traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                httpContext.Request,
                AppSettingsFor(CorrelationHeader)
            );
        }

        [Test]
        public void It_really_did_send_the_header()
        {
            _headerWasActuallyPresent.Should().BeTrue();
        }

        [Test]
        public void It_falls_back_to_the_server_generated_trace_identifier()
        {
            _traceId.Value.Should().Be("0HNCTN1IRQMDG:00000004");
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Header_Holding_Only_Control_Characters : ExtractTraceIdFromTests
    {
        private TraceId _traceId;

        [SetUp]
        public void Setup()
        {
            // A lone horizontal tab is a legal HTTP field value, so this arrives over a real
            // socket. It normalizes to an empty string, which must not become the identifier:
            // a client would otherwise be able to blank the TraceId on its own log line and on
            // every error response body for the request.
            _traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(CorrelationHeader, "\t", "0HNCTN1IRQMDG:00000003"),
                AppSettingsFor(CorrelationHeader)
            );
        }

        [Test]
        public void It_falls_back_to_the_server_generated_trace_identifier()
        {
            _traceId.Value.Should().Be("0HNCTN1IRQMDG:00000003");
        }
    }
}

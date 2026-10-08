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

    [TestFixture]
    public class Given_A_Correlation_Header_Holding_Only_Whitespace : ExtractTraceIdFromTests
    {
        /// <summary>
        /// U+00A0 NO-BREAK SPACE, not an ASCII space. Kestrel strips leading and trailing ASCII
        /// optional whitespace from a header value, so a header of plain spaces never reaches
        /// the ingestion point over a real socket - but Kestrel decodes header bytes as Latin-1
        /// by default, so byte 0xA0 arrives as U+00A0 and is neither OWS nor a control character.
        /// It therefore survives the allowlist intact and would become the correlation ID.
        /// </summary>
        private const string NoBreakSpaceOnly = "   ";

        private TraceId _traceId;

        [SetUp]
        public void Setup()
        {
            _traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(CorrelationHeader, NoBreakSpaceOnly, "0HNCTN1IRQMDG:00000005"),
                AppSettingsFor(CorrelationHeader)
            );
        }

        [Test]
        public void It_falls_back_to_the_server_generated_trace_identifier()
        {
            // The documented promise is that a client cannot blank the operational identifier.
            // A blank-looking identifier on every log line and every error response body for the
            // request defeats that just as completely as an empty one.
            _traceId.Value.Should().Be("0HNCTN1IRQMDG:00000005");
        }

        [Test]
        public void It_does_not_narrow_the_allowlist_for_a_value_that_is_not_all_blank()
        {
            // Only the all-blank case falls back. Internal whitespace - including the same
            // U+00A0 - is still part of a legitimate upstream identifier scheme and is kept.
            TraceId traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                RequestWith(CorrelationHeader, $"upstream{NoBreakSpaceOnly}id", "0HNCTN1IRQMDG:00000006"),
                AppSettingsFor(CorrelationHeader)
            );

            traceId.Value.Should().Be($"upstream{NoBreakSpaceOnly}id");
        }
    }

    /// <summary>
    /// A host whose <c>AppSettings</c> failed validation - the one condition under which the
    /// ingestion point cannot read its own configuration, because reading it is what throws.
    /// Ingestion is total across that condition and owns the fallback itself, so neither
    /// <c>LoggingMiddleware</c> nor <c>ReportInvalidConfigurationMiddleware</c> carries a
    /// <c>catch</c> expressing the same policy a second time.
    /// </summary>
    [TestFixture]
    public class Given_App_Settings_That_Cannot_Be_Read : ExtractTraceIdFromTests
    {
        /// <summary>
        /// Both over-length and hostile, so the assertions below distinguish "normalized against
        /// the documented default" from "handed back whole": the cap is what removes the trailing
        /// padding, and the allowlist is what removes the CRLF.
        /// </summary>
        private const string RawPrefix = "0HN\r\nTRACE";

        private static readonly string _rawTraceIdentifier = RawPrefix + new string('z', 300);

        /// <summary>
        /// Truncated to <see cref="AppSettings.DefaultCorrelationIdMaxLength"/> first and filtered
        /// second, which is the one normalization order the pipeline has. Spelled out rather than
        /// produced by calling the normalizer, so this pins a value rather than restating the
        /// implementation.
        /// </summary>
        private static readonly string _expectedTraceId =
            "0HNTRACE" + new string('z', AppSettings.DefaultCorrelationIdMaxLength - RawPrefix.Length);

        private DefaultHttpContext _httpContext = null!;
        private ThrowingOptions _options = null!;
        private AspNetCoreFrontend.CorrelationIdIngestion _ingestion;

        [SetUp]
        public void Setup()
        {
            _httpContext = new DefaultHttpContext { TraceIdentifier = _rawTraceIdentifier };

            // A client-supplied header is genuinely present and must still be disregarded: the
            // setting naming the header lives in the configuration that failed to validate, so no
            // client value was ever considered as a candidate.
            _httpContext.Request.Headers[CorrelationHeader] = "client-supplied-value";
            _options = new ThrowingOptions();

            _ingestion = AspNetCoreFrontend.IngestCorrelationIdFrom(_httpContext.Request, _options);
        }

        [Test]
        public void It_answers_rather_than_surfacing_the_validation_failure()
        {
            _options
                .Reads.Should()
                .Be(1, "the fallback is reached by catching the failed read, not by skipping it");
            _ingestion.TraceId.Value.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void It_normalizes_the_server_generated_identifier_against_the_documented_default()
        {
            _ingestion.TraceId.Value.Should().Be(_expectedTraceId);
        }

        [Test]
        public void It_reports_no_client_supplied_value_so_the_modification_notice_stays_silent()
        {
            // What keeps a host stuck in this mode from adding a CorrelationIdModified line to
            // every short-circuited request it answers.
            _ingestion.ClientSuppliedAValue.Should().BeFalse();
            _ingestion.SuppliedLength.Should().Be(0);
            _ingestion.WasModified.Should().BeFalse();
        }

        [Test]
        public void It_caches_the_fallback_so_a_later_call_site_reads_one_value_rather_than_two()
        {
            TraceId laterCallSite = AspNetCoreFrontend.ExtractTraceIdFrom(_httpContext.Request, _options);

            laterCallSite.Value.Should().Be(_expectedTraceId);
            _options
                .Reads.Should()
                .Be(1, "the second call site reads the cached ingestion rather than deriving it again");
            _httpContext
                .Items[AspNetCoreFrontend.CorrelationIdItemsKey]
                .Should()
                .BeOfType<AspNetCoreFrontend.CorrelationIdIngestion>()
                .Which.TraceId.Value.Should()
                .Be(_expectedTraceId);
        }

        [Test]
        public void It_serves_the_trace_id_entry_point_on_a_request_nothing_ingested_first()
        {
            // ExtractTraceIdFrom is what ReportInvalidConfigurationMiddleware calls. On a request
            // no earlier middleware ingested - a pipeline reordering, or this middleware answering
            // alone - it inherits the same fallback instead of needing one of its own.
            DefaultHttpContext uningested = new() { TraceIdentifier = _rawTraceIdentifier };

            TraceId traceId = AspNetCoreFrontend.ExtractTraceIdFrom(
                uningested.Request,
                new ThrowingOptions()
            );

            traceId.Value.Should().Be(_expectedTraceId);
        }

        /// <summary>
        /// What the framework leaves a host with when options validation fails: <c>OptionsManager</c>
        /// caches nothing in that case, so every read runs the validator and every read throws.
        /// Counted, because "how many times the configuration was read" is what separates a cache
        /// hit from a second derivation.
        /// </summary>
        private sealed class ThrowingOptions : IOptions<AppSettings>
        {
            private int _reads;

            public int Reads => _reads;

            public AppSettings Value
            {
                get
                {
                    _reads++;
                    throw new OptionsValidationException(
                        nameof(AppSettings),
                        typeof(AppSettings),
                        [$"{nameof(AppSettings.CorrelationIdMaxLength)} failed validation."]
                    );
                }
            }
        }
    }
}

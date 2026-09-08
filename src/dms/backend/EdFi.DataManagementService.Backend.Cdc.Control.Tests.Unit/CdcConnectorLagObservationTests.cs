// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Mime;
using System.Text;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Debezium's <c>MilliSecondsBehindSource</c> current value and its P50/P95/P99 quantiles, read over
/// the Jolokia bridge and mapped onto the shared lag observation. The shared contract requires all
/// five values whenever the lag state is not unknown, so evidence that is absent, partial, or
/// internally inconsistent reports unknown with null values rather than a synthesized quantile — an
/// unknown lag state keeps combined readiness false.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcConnectorLagObservation")]
public class Given_CdcConnectorLagObservationMapping
{
    private const string OperationId = "operation-1";
    private const string TopicPrefix = "edfi-dms-source";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LagThreshold = TimeSpan.FromSeconds(30);

    [Test]
    public void It_reports_lag_within_the_threshold_with_every_quantile_populated()
    {
        CdcConnectorLagObservation observation = Map(Reading(1_000, 400, 900, 1_500));

        using var _ = new AssertionScope();
        observation.LagState.Should().Be(CdcConnectorLagState.WithinThreshold);
        observation.CurrentLagMilliseconds.Should().Be(1_000);
        observation.ThresholdMilliseconds.Should().Be(30_000);
        observation.P50LagMilliseconds.Should().Be(400);
        observation.P95LagMilliseconds.Should().Be(900);
        observation.P99LagMilliseconds.Should().Be(1_500);
        observation.Diagnostics.Should().BeEmpty();
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_lag_at_the_threshold_as_within_it()
    {
        CdcConnectorLagObservation observation = Map(Reading(30_000, 1, 2, 3));

        using var _ = new AssertionScope();
        observation.LagState.Should().Be(CdcConnectorLagState.WithinThreshold);
        observation.CurrentLagMilliseconds.Should().Be(30_000);
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_lag_beyond_the_threshold_as_exceeded_with_every_quantile_populated()
    {
        CdcConnectorLagObservation observation = Map(Reading(45_000, 10_000, 40_000, 60_000));

        using var _ = new AssertionScope();
        observation.LagState.Should().Be(CdcConnectorLagState.Exceeded);
        observation.CurrentLagMilliseconds.Should().Be(45_000);
        observation.ThresholdMilliseconds.Should().Be(30_000);
        observation.P50LagMilliseconds.Should().Be(10_000);
        observation.P95LagMilliseconds.Should().Be(40_000);
        observation.P99LagMilliseconds.Should().Be(60_000);
        Diagnostic(observation, "connectorLagExceeded").Should().NotBeNull();
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [TestCase(CdcConnectorLagReadOutcome.Unavailable, "connectorLagUnavailable")]
    [TestCase(CdcConnectorLagReadOutcome.MetricsAbsent, "connectorLagMetricsAbsent")]
    [TestCase(CdcConnectorLagReadOutcome.MalformedResponse, "connectorLagMalformedResponse")]
    public void It_reports_absent_evidence_as_unknown_with_null_values(
        CdcConnectorLagReadOutcome outcome,
        string diagnosticCode
    )
    {
        CdcConnectorLagObservation observation = Map(new(outcome, null, "summary"));

        using var _ = new AssertionScope();
        AssertUnknownWithNoValues(observation);
        Diagnostic(observation, diagnosticCode).Should().NotBeNull();
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_a_reading_whose_quantiles_are_out_of_order_as_unknown()
    {
        CdcConnectorLagObservation observation = Map(Reading(1_000, 900, 400, 1_500));

        using var _ = new AssertionScope();
        AssertUnknownWithNoValues(observation);
        Diagnostic(observation, "connectorLagQuantilesOutOfOrder").Should().NotBeNull();
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_a_reading_with_a_negative_value_as_unknown()
    {
        CdcConnectorLagObservation observation = Map(Reading(-1, 400, 900, 1_500));

        using var _ = new AssertionScope();
        AssertUnknownWithNoValues(observation);
        Diagnostic(observation, "connectorLagUnusableReading").Should().NotBeNull();
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_a_successful_outcome_that_carries_no_reading_as_unknown()
    {
        CdcConnectorLagObservation observation = Map(new(CdcConnectorLagReadOutcome.Succeeded, null, null));

        AssertUnknownWithNoValues(observation);
    }

    /// <summary>
    /// The shared validator requires all five values whenever the lag state is not unknown, so a
    /// mapping that ever emitted a verdict with a missing quantile would produce an observation the
    /// readiness evaluators reject as malformed instead of the fail-closed unknown it means.
    /// </summary>
    [TestCaseSource(nameof(EveryReadResult))]
    public void It_never_reports_a_lag_verdict_with_a_missing_quantile(CdcConnectorLagReadResult reading)
    {
        CdcConnectorLagObservation observation = Map(reading);

        using var _ = new AssertionScope();
        if (observation.LagState != CdcConnectorLagState.Unknown)
        {
            observation.CurrentLagMilliseconds.Should().NotBeNull();
            observation.ThresholdMilliseconds.Should().NotBeNull();
            observation.P50LagMilliseconds.Should().NotBeNull();
            observation.P95LagMilliseconds.Should().NotBeNull();
            observation.P99LagMilliseconds.Should().NotBeNull();
        }

        Validate(observation).Succeeded.Should().BeTrue();
    }

    private static IEnumerable<TestCaseData> EveryReadResult()
    {
        yield return new TestCaseData(Reading(1_000, 400, 900, 1_500)).SetArgDisplayNames("withinThreshold");
        yield return new TestCaseData(Reading(45_000, 400, 900, 1_500)).SetArgDisplayNames("exceeded");
        yield return new TestCaseData(Reading(0, 0, 0, 0)).SetArgDisplayNames("zero");
        yield return new TestCaseData(Reading(1_000, 900, 400, 1_500)).SetArgDisplayNames("outOfOrder");
        yield return new TestCaseData(Reading(1_000, -1, 900, 1_500)).SetArgDisplayNames("negative");
        yield return new TestCaseData(
            new CdcConnectorLagReadResult(CdcConnectorLagReadOutcome.Unavailable, null, "summary")
        ).SetArgDisplayNames("unavailable");
        yield return new TestCaseData(
            new CdcConnectorLagReadResult(CdcConnectorLagReadOutcome.MetricsAbsent, null, "summary")
        ).SetArgDisplayNames("metricsAbsent");
        yield return new TestCaseData(
            new CdcConnectorLagReadResult(CdcConnectorLagReadOutcome.MalformedResponse, null, "summary")
        ).SetArgDisplayNames("malformed");
    }

    [Test]
    public void It_carries_the_operation_envelope_onto_the_observation()
    {
        CdcConnectorLagObservation observation = Map(Reading(1_000, 400, 900, 1_500));

        using var _ = new AssertionScope();
        observation.ContractVersion.Should().Be(CdcJsonContract.CurrentContractVersion);
        observation.OperationId.Should().Be(OperationId);
        observation.ObservedAt.Should().Be(ObservedAt);
        observation.TargetIdentity.Should().Be(TargetIdentity());
        observation.Provider.Should().Be(CdcProvider.Postgresql);
        observation
            .PhysicalSourceFingerprint.Should()
            .Be(CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value);
    }

    [Test]
    public void It_rejects_a_missing_reading()
    {
        Action mapping = () =>
            CdcConnectorLagObservationMapper.Map(Context(), null!, LagThreshold, ObservedAt);

        mapping.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task It_reads_the_current_value_and_every_quantile_in_one_bridge_call()
    {
        (ICdcConnectorLagReader reader, StubHttpMessageHandler handler) = Reader(_ =>
            Json(
                HttpStatusCode.OK,
                $$"""
                {
                  "request": { "type": "read" },
                  "value": {
                    "debezium.postgres:context=streaming,server={{TopicPrefix}},type=connector-metrics": {
                      "MilliSecondsBehindSource": 1200,
                      "MilliSecondsBehindSourceP50": 400.4,
                      "MilliSecondsBehindSourceP95": 900.6,
                      "MilliSecondsBehindSourceP99": 1500.0
                    }
                  },
                  "status": 200
                }
                """
            )
        );

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeTrue();
        result.Reading!.CurrentMilliseconds.Should().Be(1_200);
        result.Reading.P50Milliseconds.Should().Be(400);
        result.Reading.P95Milliseconds.Should().Be(901);
        result.Reading.P99Milliseconds.Should().Be(1_500);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Uri.Port.Should().Be(CdcConnectorJolokiaLagReader.JolokiaPort);
        Uri.UnescapeDataString(handler.Requests[0].Uri.AbsolutePath)
            .Should()
            .Be(
                "/jolokia/read/debezium.postgres:type=connector-metrics,context=streaming,"
                    + $"server={TopicPrefix},*/MilliSecondsBehindSource,MilliSecondsBehindSourceP50,"
                    + "MilliSecondsBehindSourceP95,MilliSecondsBehindSourceP99"
            );
    }

    [Test]
    public async Task It_reads_the_sql_server_metrics_domain_for_a_sql_server_binding()
    {
        (ICdcConnectorLagReader reader, StubHttpMessageHandler handler) = Reader(_ =>
            Json(HttpStatusCode.OK, """{"value":{},"status":200}""")
        );

        await reader.ReadAsync(CdcProvider.SqlServer, TopicPrefix, CancellationToken.None);

        Uri.UnescapeDataString(handler.Requests[0].Uri.AbsolutePath)
            .Should()
            .StartWith("/jolokia/read/debezium.sql_server:type=connector-metrics,context=streaming,");
    }

    [Test]
    public async Task It_reads_a_flat_attribute_map_from_a_bridge_that_resolved_a_single_mbean()
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ =>
            Json(
                HttpStatusCode.OK,
                """
                {
                  "value": {
                    "MilliSecondsBehindSource": 10,
                    "MilliSecondsBehindSourceP50": 1,
                    "MilliSecondsBehindSourceP95": 2,
                    "MilliSecondsBehindSourceP99": 3
                  },
                  "status": 200
                }
                """
            )
        );

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        result.Reading.Should().Be(new CdcConnectorLagReading(10, 1, 2, 3));
    }

    [Test]
    public async Task It_reports_an_unreachable_bridge_as_unavailable()
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ => throw new HttpRequestException("refused"));

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        using AssertionScope scope = new();
        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.Unavailable);
        result.Reading.Should().BeNull();
        MapUnknown(result);
    }

    [Test]
    public async Task It_reports_a_bridge_that_never_answers_as_unavailable()
    {
        (ICdcConnectorLagReader reader, _) = Reader(
            _ => Json(HttpStatusCode.OK, "{}"),
            neverAnswers: true,
            requestTimeout: TimeSpan.FromMilliseconds(50)
        );

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        using AssertionScope scope = new();
        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.Unavailable);
        MapUnknown(result);
    }

    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task It_reports_a_bridge_failure_status_as_unavailable(HttpStatusCode statusCode)
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ => Json(statusCode, """{"error":"x"}"""));

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.Unavailable);
    }

    [Test]
    public async Task It_reports_an_absent_mbean_as_metrics_absent()
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ =>
            Json(
                HttpStatusCode.OK,
                """
                {
                  "error_type": "javax.management.InstanceNotFoundException",
                  "error": "instance not found",
                  "status": 404
                }
                """
            )
        );

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        using AssertionScope scope = new();
        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.MetricsAbsent);
        MapUnknown(result);
    }

    [Test]
    public async Task It_reports_a_pattern_that_matched_nothing_as_metrics_absent()
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ =>
            Json(HttpStatusCode.OK, """{"value":{},"status":200}""")
        );

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.MetricsAbsent);
    }

    /// <summary>
    /// Two matching MBeans are two connectors' metrics, and neither can be attributed to this
    /// binding, so the reading is absent rather than one of them chosen arbitrarily.
    /// </summary>
    [Test]
    public async Task It_reports_more_than_one_matching_mbean_as_metrics_absent()
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ =>
            Json(
                HttpStatusCode.OK,
                """
                {
                  "value": {
                    "debezium.postgres:context=streaming,server=a,type=connector-metrics": {
                      "MilliSecondsBehindSource": 1,
                      "MilliSecondsBehindSourceP50": 1,
                      "MilliSecondsBehindSourceP95": 1,
                      "MilliSecondsBehindSourceP99": 1
                    },
                    "debezium.postgres:context=streaming,server=b,type=connector-metrics": {
                      "MilliSecondsBehindSource": 2,
                      "MilliSecondsBehindSourceP50": 2,
                      "MilliSecondsBehindSourceP95": 2,
                      "MilliSecondsBehindSourceP99": 2
                    }
                  },
                  "status": 200
                }
                """
            )
        );

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        using AssertionScope scope = new();
        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.MetricsAbsent);
        result.Reading.Should().BeNull();
    }

    [TestCase("MilliSecondsBehindSource")]
    [TestCase("MilliSecondsBehindSourceP50")]
    [TestCase("MilliSecondsBehindSourceP95")]
    [TestCase("MilliSecondsBehindSourceP99")]
    public async Task It_reports_a_partial_quantile_set_as_metrics_absent(string omittedAttribute)
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ =>
            Json(HttpStatusCode.OK, Attributes(omittedAttribute))
        );

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        using AssertionScope scope = new();
        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.MetricsAbsent);
        result.Reading.Should().BeNull();
        MapUnknown(result);
    }

    /// <summary>
    /// Debezium reports a metric it has no measurement for as a negative sentinel, which is absent
    /// evidence rather than a lag of zero.
    /// </summary>
    [Test]
    public async Task It_reports_a_negative_sentinel_as_metrics_absent()
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ =>
            Json(
                HttpStatusCode.OK,
                """
                {
                  "value": {
                    "MilliSecondsBehindSource": -1,
                    "MilliSecondsBehindSourceP50": 1,
                    "MilliSecondsBehindSourceP95": 2,
                    "MilliSecondsBehindSourceP99": 3
                  },
                  "status": 200
                }
                """
            )
        );

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.MetricsAbsent);
    }

    [TestCase("{ this is not json")]
    [TestCase("")]
    [TestCase("[]")]
    [TestCase("""{"value":{"MilliSecondsBehindSource":1}}""")]
    [TestCase("""{"status":200}""")]
    [TestCase("""{"value":[],"status":200}""")]
    public async Task It_reports_an_undocumented_body_as_a_malformed_response(string body)
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ => Json(HttpStatusCode.OK, body));

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        using AssertionScope scope = new();
        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.MalformedResponse);
        MapUnknown(result);
    }

    [Test]
    public async Task It_reports_a_non_success_jolokia_status_as_unavailable()
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ =>
            Json(HttpStatusCode.OK, """{"error":"x","status":500}""")
        );

        CdcConnectorLagReadResult result = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.Unavailable);
    }

    [Test]
    public async Task It_reads_the_bridge_the_deployment_configured_rather_than_the_connect_host()
    {
        (ICdcConnectorLagReader reader, StubHttpMessageHandler handler) = Reader(
            _ => Json(HttpStatusCode.OK, """{"value":{},"status":200}"""),
            metricsBaseUri: "http://connect-metrics.internal:9999/"
        );

        await reader.ReadAsync(CdcProvider.Postgresql, TopicPrefix, CancellationToken.None);

        using var _ = new AssertionScope();
        handler.Requests[0].Uri.Host.Should().Be("connect-metrics.internal");
        handler.Requests[0].Uri.Port.Should().Be(9999);
    }

    [Test]
    public async Task It_propagates_caller_cancellation_rather_than_reporting_an_unreachable_bridge()
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ => Json(HttpStatusCode.OK, "{}"), neverAnswers: true);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        Func<Task> read = () => reader.ReadAsync(CdcProvider.Postgresql, TopicPrefix, cancellation.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_rejects_a_blank_topic_prefix()
    {
        (ICdcConnectorLagReader reader, _) = Reader(_ => Json(HttpStatusCode.OK, "{}"));

        Func<Task> read = () => reader.ReadAsync(CdcProvider.Postgresql, "  ", CancellationToken.None);

        await read.Should().ThrowAsync<ArgumentException>();
    }

    private static void AssertUnknownWithNoValues(CdcConnectorLagObservation observation)
    {
        observation.LagState.Should().Be(CdcConnectorLagState.Unknown);
        observation.CurrentLagMilliseconds.Should().BeNull();
        observation.ThresholdMilliseconds.Should().BeNull();
        observation.P50LagMilliseconds.Should().BeNull();
        observation.P95LagMilliseconds.Should().BeNull();
        observation.P99LagMilliseconds.Should().BeNull();
    }

    private static void MapUnknown(CdcConnectorLagReadResult result) =>
        AssertUnknownWithNoValues(Map(result));

    private static CdcConnectorLagObservation Map(CdcConnectorLagReadResult reading) =>
        CdcConnectorLagObservationMapper.Map(Context(), reading, LagThreshold, ObservedAt);

    private static CdcConnectorLagReadResult Reading(long current, long p50, long p95, long p99) =>
        new(CdcConnectorLagReadOutcome.Succeeded, new(current, p50, p95, p99), null);

    private static string Attributes(string omittedAttribute)
    {
        Dictionary<string, long> attributes = new(StringComparer.Ordinal)
        {
            [CdcConnectorJolokiaLagReader.CurrentLagAttributeName] = 10,
            [CdcConnectorJolokiaLagReader.P50LagAttributeName] = 1,
            [CdcConnectorJolokiaLagReader.P95LagAttributeName] = 2,
            [CdcConnectorJolokiaLagReader.P99LagAttributeName] = 3,
        };
        attributes.Remove(omittedAttribute);

        string reported = string.Join(
            ',',
            attributes.Select(attribute => $"\"{attribute.Key}\":{attribute.Value}")
        );

        return $$"""{"value":{{{reported}}},"status":200}""";
    }

    private static CdcContractValidationResult Validate(CdcConnectorLagObservation observation) =>
        CdcConnectorLagObservationValidator.Validate(
            observation,
            new(
                OperationId,
                TargetIdentity(),
                CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value,
                ObservedAt.AddMinutes(1)
            )
        );

    private static CdcObservationContext Context() =>
        new(
            OperationId,
            TargetIdentity(),
            CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value
        );

    private static CdcTargetIdentity TargetIdentity() =>
        CdcControlTemplateTestData.BuildTargetIdentity(Ddl.CdcProvider.Postgresql);

    private static CdcDiagnostic? Diagnostic(CdcConnectorLagObservation observation, string code) =>
        observation.Diagnostics.SingleOrDefault(diagnostic =>
            string.Equals(diagnostic.Code, code, StringComparison.Ordinal)
        );

    private static (ICdcConnectorLagReader Reader, StubHttpMessageHandler Handler) Reader(
        Func<RecordedRequest, HttpResponseMessage> respond,
        bool neverAnswers = false,
        TimeSpan? requestTimeout = null,
        string metricsBaseUri = ""
    )
    {
        StubHttpMessageHandler handler = new(respond, neverAnswers);
        IHttpClientFactory httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._))
            .ReturnsLazily(() => new HttpClient(handler, disposeHandler: false));

        CdcConnectorJolokiaLagReader reader = new(
            httpClientFactory,
            Options.Create(ControlOptions(metricsBaseUri, requestTimeout)),
            NullLogger<CdcConnectorJolokiaLagReader>.Instance
        );

        return (reader, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json) };

    private static CdcControlOptions ControlOptions(string metricsBaseUri, TimeSpan? requestTimeout) =>
        new()
        {
            DeploymentKey = "dms-local",
            InstanceKey = "data-store-1",
            TopicPrefix = "edfi.dms",
            Generation = 1,
            PartitionCount = 3,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://connect.internal:8083",
            ConnectMetricsBaseUri = metricsBaseUri,
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = 4_194_304,
            LagThreshold = LagThreshold,
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
            Timeouts = new() { ConnectRequest = requestTimeout ?? TimeSpan.FromSeconds(30) },
        };

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri);

    private sealed class StubHttpMessageHandler(
        Func<RecordedRequest, HttpResponseMessage> respond,
        bool neverAnswers
    ) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RecordedRequest recorded = new(request.Method, request.RequestUri!);
            Requests.Add(recorded);

            if (neverAnswers)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return respond(recorded);
        }
    }
}

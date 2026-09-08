// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public sealed class Given_CdcConnectorTelemetry(CdcProvider provider)
{
    private CdcDeploymentRequest _request = null!;
    private ICdcConnectTransport _connect = null!;
    private ICdcWorkerInspectionTransport _worker = null!;
    private TestClock _clock = null!;
    private Handler _handler = null!;
    private HttpClient _client = null!;
    private CdcConnectorTelemetryAdapter _adapter = null!;
    private CdcTelemetryObservationPass _pass = null!;
    private string _metrics = "";
    private List<string> _trace = [];

    [SetUp]
    public void Setup()
    {
        _request = CdcDeploymentRequestTestData.Request(
            provider,
            worker: CdcDeploymentRequestTestData.Worker(digest: CdcQualifiedWorkerImage.Digest)
        );
        _connect = A.Fake<ICdcConnectTransport>();
        _worker = A.Fake<ICdcWorkerInspectionTransport>();
        _clock = new();
        _trace = [];
        _metrics =
            Scalar("jmx_scrape_error", "0")
            + Scalar("edfi_cdc_worker_start_time_seconds", "123")
            + Metric("current", "5");
        _handler = new Handler(() =>
        {
            _trace.Add("scrape");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(_metrics, Encoding.UTF8, "text/plain"),
            };
        });
        _client = new(_handler);
        _adapter = new(_client, _connect, _worker, _clock);
        _pass = new(_request, "telemetry-test", 10);
        A.CallTo(() => _worker.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _trace.Add("worker");
                return Task.FromResult<CdcTransportResult<CdcWorkerInspection>>(
                    new CdcTransportResult<CdcWorkerInspection>.Observed(Worker())
                );
            });
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _trace.Add("status");
                return Task.FromResult<CdcTransportResult<CdcConnectStatus>>(
                    new CdcTransportResult<CdcConnectStatus>.Observed(Status())
                );
            });
    }

    [TearDown]
    public void Cleanup()
    {
        _pass.Dispose();
        _client.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task It_collects_current_lag_alone_in_identity_brackets_and_preserves_null_statistics()
    {
        var receipt = await Collect();
        var observation = Read(receipt);
        observation.CurrentLagMilliseconds.Should().Be(5);
        observation.LagState.Should().Be(CoreCdc.CdcConnectorLagState.WithinThreshold);
        observation.P50LagMilliseconds.Should().BeNull();
        observation.P95LagMilliseconds.Should().BeNull();
        observation.P99LagMilliseconds.Should().BeNull();
        _trace.Should().Equal("worker", "status", "scrape", "status", "worker");
        JsonSerializer.Serialize(receipt).Should().Be("{}");
        receipt.ToString().Should().Be(nameof(CdcConnectorTelemetryObservation));
        _handler.RequestUri.Should().Be(_request.WorkerMetricsEndpoint);
        _handler.NoCache.Should().BeTrue();
    }

    [TestCase("0", 0, CoreCdc.CdcConnectorLagState.WithinThreshold)]
    [TestCase("10", 10, CoreCdc.CdcConnectorLagState.WithinThreshold)]
    [TestCase("10.01", 11, CoreCdc.CdcConnectorLagState.Exceeded)]
    [TestCase("1e2", 100, CoreCdc.CdcConnectorLagState.Exceeded)]
    public async Task It_rounds_up_finite_lag_and_applies_only_the_current_threshold(
        string value,
        long expected,
        CoreCdc.CdcConnectorLagState state
    )
    {
        _metrics = _metrics.Replace(Metric("current", "5"), Metric("current", value));
        var observation = Read(await Collect());
        observation.CurrentLagMilliseconds.Should().Be(expected);
        observation.LagState.Should().Be(state);
    }

    [TestCase("NaN")]
    [TestCase("+Inf")]
    [TestCase("-1")]
    [TestCase("1e999")]
    [TestCase("9223372036854775808")]
    [TestCase("no-value-secret")]
    [TestCase("5 12345")]
    public async Task It_rejects_unusable_current_lag(string value)
    {
        _metrics = _metrics.Replace(Metric("current", "5"), Metric("current", value));
        await AssertUnavailable();
    }

    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("duplicate-type")]
    [TestCase("wrong-type")]
    [TestCase("no-type")]
    [TestCase("provider")]
    [TestCase("connector")]
    [TestCase("duplicate-label")]
    [TestCase("extra-label")]
    [TestCase("malformed-label")]
    [TestCase("missing-label")]
    [TestCase("bad-unit")]
    [TestCase("no-worker")]
    [TestCase("duplicate-worker")]
    [TestCase("zero-worker")]
    [TestCase("exporter-error")]
    [TestCase("no-exporter-status")]
    public async Task It_rejects_ambiguous_or_missing_required_identity_and_metrics(string scenario)
    {
        string current = Metric("current", "5");
        _metrics = scenario switch
        {
            "missing" => _metrics.Replace(current, ""),
            "duplicate" => _metrics + current.Split('\n')[1] + "\n",
            "duplicate-type" => _metrics + current.Split('\n')[0] + "\n",
            "wrong-type" => _metrics.Replace("milliseconds gauge", "milliseconds counter"),
            "no-type" => _metrics.Replace(current.Split('\n')[0], ""),
            "provider" => _metrics.Replace(ProviderLabel, "other"),
            "connector" => _metrics.Replace(_request.Binding.ConnectorName, "peer"),
            "duplicate-label" => _metrics.Replace("{connector=", "{connector=\"duplicate\",connector="),
            "extra-label" => _metrics.Replace("{connector=", "{task=\"0\",connector="),
            "malformed-label" => _metrics.Replace("{connector=", "{?connector="),
            "missing-label" => _metrics.Replace($",provider=\"{ProviderLabel}\"", ""),
            "bad-unit" => _metrics.Replace("current_milliseconds", "current_seconds"),
            "no-worker" => _metrics.Replace(Scalar("edfi_cdc_worker_start_time_seconds", "123"), ""),
            "duplicate-worker" => _metrics + "edfi_cdc_worker_start_time_seconds 123\n",
            "zero-worker" => _metrics.Replace("seconds 123", "seconds 0"),
            "exporter-error" => _metrics.Replace("jmx_scrape_error 0", "jmx_scrape_error 1"),
            "no-exporter-status" => _metrics.Replace(Scalar("jmx_scrape_error", "0"), ""),
            _ => throw new AssertionException("Unknown case"),
        };
        await AssertUnavailable();
    }

    [TestCase("missing")]
    [TestCase("NaN")]
    [TestCase("-1")]
    [TestCase("duplicate")]
    [TestCase("provider")]
    [TestCase("type")]
    [TestCase("malformed")]
    public async Task It_discards_unusable_optional_statistics_without_invalidating_current_lag(
        string scenario
    )
    {
        string optional = Metric("p95", "7");
        _metrics += scenario switch
        {
            "missing" => "",
            "duplicate" => optional + optional.Split('\n')[1] + "\n",
            "provider" => optional.Replace(ProviderLabel, "other"),
            "type" => optional.Replace("gauge", "counter"),
            "malformed" => optional.Replace("{connector=", "{?connector="),
            _ => Metric("p95", scenario),
        };
        var observation = Read(await Collect());
        observation.CurrentLagMilliseconds.Should().Be(5);
        observation.P95LagMilliseconds.Should().BeNull();
    }

    [Test]
    public async Task It_isolates_peer_connectors_and_maps_ordered_optional_percentiles()
    {
        _metrics += Metric("current", "99", "peer").Split('\n')[1] + "\n";
        _metrics += Metric("p50", "1") + Metric("p95", "2") + Metric("p99", "3");
        var observation = Read(await Collect());
        observation.CurrentLagMilliseconds.Should().Be(5);
        observation.P50LagMilliseconds.Should().Be(1);
        observation.P95LagMilliseconds.Should().Be(2);
        observation.P99LagMilliseconds.Should().Be(3);
    }

    [TestCase(true, "9")]
    [TestCase(false, "9")]
    [TestCase(false, "3.1")]
    public async Task It_discards_inconsistent_complete_or_partial_optional_ordering(
        bool complete,
        string p50
    )
    {
        _metrics += Metric("p50", p50) + Metric("p99", "3.01") + (complete ? Metric("p95", "2") : "");
        var observation = Read(await Collect());
        observation.CurrentLagMilliseconds.Should().Be(5);
        observation.P50LagMilliseconds.Should().BeNull();
        observation.P99LagMilliseconds.Should().BeNull();
    }

    [TestCase(9999, true)]
    [TestCase(10000, true)]
    [TestCase(10001, false)]
    public async Task It_rechecks_monotonic_age_at_the_exact_default_boundary(int milliseconds, bool fresh)
    {
        var receipt = await Collect();
        _clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Read(receipt)
            .LagState.Should()
            .Be(fresh ? CoreCdc.CdcConnectorLagState.WithinThreshold : CoreCdc.CdcConnectorLagState.Unknown);
    }

    [Test]
    public async Task It_includes_scrape_time_and_later_identity_work_in_age()
    {
        _handler.BeforeResponse = () => _clock.Advance(TimeSpan.FromSeconds(6));
        int calls = 0;
        A.CallTo(() => _worker.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                if (++calls == 2)
                {
                    _clock.Advance(TimeSpan.FromSeconds(5));
                }
                return Task.FromResult<CdcTransportResult<CdcWorkerInspection>>(
                    new CdcTransportResult<CdcWorkerInspection>.Observed(Worker())
                );
            });
        await AssertUnavailable();
    }

    [Test]
    public async Task It_records_collection_start_and_completion_and_ignores_wall_clock_rollback()
    {
        var start = _clock.GetUtcNow();
        _handler.BeforeResponse = () => _clock.Advance(TimeSpan.FromSeconds(2));
        var receipt = await Collect();
        receipt.CollectionStartedAt.Should().Be(start);
        receipt.CollectionCompletedAt.Should().Be(start.AddSeconds(2));
        _clock.WallOffset = TimeSpan.FromDays(-1);
        _clock.Advance(TimeSpan.FromSeconds(9));
        Read(receipt).LagState.Should().Be(CoreCdc.CdcConnectorLagState.Unknown);
    }

    [TestCase("disposed")]
    [TestCase("recovery")]
    [TestCase("different-pass")]
    public async Task It_rejects_invalidated_or_previous_pass_evidence_even_with_the_same_operation_id(
        string scenario
    )
    {
        var receipt = await Collect();
        using var next = new CdcTelemetryObservationPass(_request, "telemetry-test", 10);
        if (scenario == "disposed")
        {
            _pass.Dispose();
        }
        if (scenario == "recovery")
        {
            _pass.Invalidate();
        }
        receipt
            .ReadForEvaluation(scenario == "different-pass" ? next : _pass)
            .LagState.Should()
            .Be(CoreCdc.CdcConnectorLagState.Unknown);
    }

    [Test]
    public async Task It_does_not_allow_recollection_in_the_same_pass()
    {
        await Collect();
        (await _adapter.CollectAsync(_request, _pass, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _trace.Count(x => x == "scrape").Should().Be(1);
    }

    [TestCase("process")]
    [TestCase("worker-id")]
    [TestCase("endpoint")]
    [TestCase("digest")]
    [TestCase("heap")]
    [TestCase("group")]
    [TestCase("offset-topic")]
    [TestCase("missing-process")]
    [TestCase("missing-worker")]
    [TestCase("absent")]
    [TestCase("unavailable")]
    public async Task It_rejects_worker_identity_changes_and_missing_deployment_evidence(string scenario)
    {
        int calls = 0;
        A.CallTo(() => _worker.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                ++calls;
                CdcTransportResult<CdcWorkerInspection> result =
                    new CdcTransportResult<CdcWorkerInspection>.Observed(Worker(calls == 1 ? "" : scenario));
                if (calls == 2 && scenario == "absent")
                {
                    result = new CdcTransportResult<CdcWorkerInspection>.Absent();
                }
                if (calls == 2 && scenario == "unavailable")
                {
                    result = new CdcTransportResult<CdcWorkerInspection>.Unavailable(
                        new(CdcDeploymentComponent.Worker, CdcDeploymentFailure.Unavailable)
                    );
                }
                return Task.FromResult(result);
            });
        await AssertUnavailable();
    }

    [TestCase("reassignment")]
    [TestCase("task-restarting")]
    [TestCase("connector-stopped")]
    [TestCase("two-tasks")]
    [TestCase("wrong-task-id")]
    [TestCase("missing-worker")]
    [TestCase("connector")]
    [TestCase("source")]
    [TestCase("count")]
    public async Task It_rejects_task_recovery_reassignment_or_contradictory_status(string scenario)
    {
        int calls = 0;
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                Task.FromResult<CdcTransportResult<CdcConnectStatus>>(
                    new CdcTransportResult<CdcConnectStatus>.Observed(Status(++calls == 1 ? "" : scenario))
                )
            );
        await AssertUnavailable();
    }

    [TestCase(401, CdcDeploymentFailure.AuthenticationFailed)]
    [TestCase(403, CdcDeploymentFailure.AuthenticationFailed)]
    [TestCase(404, CdcDeploymentFailure.ValidationFailed)]
    [TestCase(302, CdcDeploymentFailure.ValidationFailed)]
    [TestCase(500, CdcDeploymentFailure.ValidationFailed)]
    public async Task It_never_interprets_failed_http_as_absent_or_ready(
        int code,
        CdcDeploymentFailure failure
    )
    {
        _handler.Response = () =>
            new((HttpStatusCode)code) { Content = new StringContent("private-secret-host") };
        var result = await AssertUnavailable();
        result.Diagnostics.Single().Failure.Should().Be(failure);
    }

    [Test]
    public async Task It_rejects_oversized_bodies_and_cache_responses()
    {
        _metrics = new string('x', CdcConnectorTelemetryAdapter.MaximumResponseBytes + 1);
        await AssertUnavailable();
    }

    [Test]
    public async Task It_propagates_cancellation_during_collection()
    {
        using var cancel = new CancellationTokenSource();
        _handler.BeforeResponse = () => cancel.Cancel();
        Func<Task> act = () => _adapter.CollectAsync(_request, _pass, cancel.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestCase("worker")]
    [TestCase("status")]
    [TestCase("http")]
    public async Task It_bounds_uncooperative_external_calls(string component)
    {
        ReplaceTiming(
            new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1))
        );
        if (component == "worker")
        {
            A.CallTo(() => _worker.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Returns(new TaskCompletionSource<CdcTransportResult<CdcWorkerInspection>>().Task);
        }
        if (component == "status")
        {
            A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Returns(new TaskCompletionSource<CdcTransportResult<CdcConnectStatus>>().Task);
        }
        if (component == "http")
        {
            _handler.Pending = true;
        }
        var result = await AssertUnavailable();
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.Timeout);
    }

    [Test]
    public async Task It_sanitizes_transport_exceptions()
    {
        _handler.BeforeResponse = () => throw new HttpRequestException("private-secret-host");
        await AssertUnavailable();
    }

    [TestCase("min")]
    [TestCase("max")]
    [TestCase("average")]
    public async Task It_discards_invalid_optional_diagnostics_independently(string statistic)
    {
        _metrics += Metric(statistic, "NaN");
        var receipt = await Collect();
        Read(receipt).CurrentLagMilliseconds.Should().Be(5);
        receipt
            .ReadStatisticsForEvaluation(_pass)
            .Should()
            .Be(new CdcConnectorTelemetryStatistics(null, null, null));
    }

    [Test]
    public async Task It_retains_fractional_optional_diagnostics_only_while_fresh()
    {
        _metrics += Metric("min", "1") + Metric("max", "3") + Metric("average", "1.5");
        var receipt = await Collect();
        receipt
            .ReadStatisticsForEvaluation(_pass)
            .Should()
            .Be(new CdcConnectorTelemetryStatistics(1, 3, 1.5));
        _clock.Advance(TimeSpan.FromSeconds(11));
        receipt
            .ReadStatisticsForEvaluation(_pass)
            .Should()
            .Be(new CdcConnectorTelemetryStatistics(null, null, null));
    }

    [TestCase("cached")]
    [TestCase("content-type")]
    public async Task It_rejects_cached_or_unqualified_content(string scenario)
    {
        _handler.Response = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _metrics,
                    Encoding.UTF8,
                    scenario == "content-type" ? "text/html" : "text/plain"
                ),
            };
            if (scenario == "cached")
            {
                response.Headers.Age = TimeSpan.FromSeconds(1);
            }
            return response;
        };
        await AssertUnavailable();
    }

    [Test]
    public async Task It_uses_the_configured_maximum_age()
    {
        ReplaceTiming(
            new(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2)
            )
        );
        var receipt = await Collect();
        _clock.Advance(TimeSpan.FromSeconds(2));
        Read(receipt).LagState.Should().Be(CoreCdc.CdcConnectorLagState.WithinThreshold);
        _clock.Advance(TimeSpan.FromTicks(1));
        Read(receipt).LagState.Should().Be(CoreCdc.CdcConnectorLagState.Unknown);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_bounds_response_body_collection_and_propagates_caller_cancellation(bool cancelCaller)
    {
        ReplaceTiming(
            new(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1))
        );
        using var cancellation = new CancellationTokenSource();
        _handler.Response = () =>
        {
            var content = new StreamContent(new HangingStream());
            content.Headers.ContentType = new("text/plain");
            return new(HttpStatusCode.OK) { Content = content };
        };
        if (cancelCaller)
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));
        }
        var pending = _adapter.CollectAsync(_request, _pass, cancellation.Token);
        if (cancelCaller)
        {
            Func<Task> act = async () => await pending;
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            var result = await pending;
            result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
            result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.Timeout);
        }
    }

    [Test]
    public async Task It_bounds_the_whole_pass_even_when_individual_calls_fit()
    {
        ReplaceTiming(
            new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(210), TimeSpan.FromMilliseconds(1))
        );
        A.CallTo(() => _worker.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(async _ =>
            {
                await Task.Delay(80);
                return new CdcTransportResult<CdcWorkerInspection>.Observed(Worker());
            });
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(async _ =>
            {
                await Task.Delay(80);
                return new CdcTransportResult<CdcConnectStatus>.Observed(Status());
            });
        var result = await AssertUnavailable();
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.Timeout);
    }

    [Test]
    public async Task It_rejects_recovery_observed_during_the_scrape()
    {
        _handler.BeforeResponse = () => _pass.Invalidate();
        await AssertUnavailable();
    }

    [Test]
    public async Task It_stops_before_http_when_initial_status_is_not_running()
    {
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(new CdcTransportResult<CdcConnectStatus>.Observed(Status("connector-stopped")));
        await AssertUnavailable();
        _trace.Should().NotContain("scrape");
    }

    [Test]
    public async Task It_rejects_a_different_request_scope_before_any_io()
    {
        var other = CdcDeploymentRequestTestData.Request(provider);
        (await _adapter.CollectAsync(other, _pass, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().BeEmpty();
    }

    [TestCase(-1, "operation")]
    [TestCase(10, "")]
    public void It_reuses_core_validation_for_the_evaluation_scope(long threshold, string operation)
    {
        Action act = () =>
        {
            using var pass = new CdcTelemetryObservationPass(_request, operation, threshold);
        };
        act.Should().Throw<ArgumentException>();
    }

    private sealed class HangingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private string ProviderLabel => provider == CdcProvider.Postgresql ? "postgres" : "sql_server";

    private static string Scalar(string name, string value) => $"# TYPE {name} gauge\n{name} {value}\n";

    private string Metric(string statistic, string value, string connector = "")
    {
        string name = $"edfi_cdc_source_lag_{statistic}_milliseconds";
        return $"# TYPE {name} gauge\n{name}{{connector=\"{(connector.Length == 0 ? _request.Binding.ConnectorName : connector)}\",provider=\"{ProviderLabel}\"}} {value}\n";
    }

    private CdcWorkerInspection Worker(string change = "") =>
        new(
            change switch
            {
                "missing-process" => "",
                "process" => "new-process",
                _ => "process",
            },
            change == "endpoint" ? new Uri("http://peer:9404/metrics") : _request.WorkerMetricsEndpoint,
            new Dictionary<string, string>
            {
                ["group.id"] = change == "group" ? "peer" : _request.WorkerPolicy.WorkerKey.Value,
                ["offset.storage.topic"] =
                    change == "offset-topic"
                        ? "peer-offsets"
                        : _request.WorkerPolicy.OffsetStorageTopic.Value,
            },
            change == "digest" ? "sha256:" + new string('a', 64) : _request.WorkerPolicy.QualifiedImageDigest,
            change == "heap" ? 1 : _request.WorkerPolicy.HeapBytes,
            change switch
            {
                "missing-worker" => "",
                "worker-id" => "peer:8083",
                _ => "worker:8083",
            }
        );

    private CdcConnectStatus Status(string change = "")
    {
        var state = CoreCdc.CdcConnectorRuntimeState.Running;
        var task = new CdcConnectTaskStatus(
            change == "wrong-task-id" ? 1 : 0,
            change == "task-restarting" ? CoreCdc.CdcConnectorRuntimeState.Unknown : state,
            change == "reassignment" ? "peer:8083" : "worker:8083"
        );
        return new(
            new(
                CoreCdc.CdcJsonContract.CurrentContractVersion,
                "status",
                _clock.GetUtcNow(),
                _request.TargetIdentity,
                _request.Binding.Provider,
                change == "source" ? "wrong" : _request.Binding.PhysicalSourceFingerprint,
                change == "connector" ? "peer" : _request.Binding.ConnectorName,
                change == "connector-stopped" ? CoreCdc.CdcConnectorRuntimeState.Stopped : state,
                change == "count" ? 2 : 1,
                1,
                state,
                CoreCdc.CdcConnectorSnapshotState.Unknown,
                null,
                null,
                []
            ),
            change == "missing-worker" ? "" : "worker:8083",
            change == "two-tasks" ? [task, task] : [task]
        );
    }

    private async Task<CdcConnectorTelemetryObservation> Collect()
    {
        var result = await _adapter.CollectAsync(_request, _pass, CancellationToken.None);
        result.Should().BeOfType<CdcTransportResult<CdcConnectorTelemetryObservation>.Observed>();
        return ((CdcTransportResult<CdcConnectorTelemetryObservation>.Observed)result).Value;
    }

    private CoreCdc.CdcConnectorLagObservation Read(CdcConnectorTelemetryObservation receipt)
    {
        var observation = receipt.ReadForEvaluation(_pass);
        CoreCdc
            .CdcConnectorLagObservationValidator.Validate(
                observation,
                new(
                    "telemetry-test",
                    _request.TargetIdentity,
                    _request.Binding.PhysicalSourceFingerprint,
                    _clock.GetUtcNow() < observation.ObservedAt ? observation.ObservedAt : _clock.GetUtcNow()
                )
            )
            .Succeeded.Should()
            .BeTrue();
        return observation;
    }

    private async Task<CdcTransportResult<CdcConnectorTelemetryObservation>> AssertUnavailable()
    {
        var result = await _adapter.CollectAsync(_request, _pass, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        JsonSerializer.Serialize(result).Should().NotContain("private-secret-host");
        return result;
    }

    private void ReplaceTiming(CdcDeploymentTiming timing)
    {
        _request = new(
            _request.Binding,
            _request.DmsSettings,
            _request.ProviderSetup,
            _request.ConnectEndpoint,
            _request.WorkerMetricsEndpoint,
            _request.ConnectorPolicy,
            _request.WorkerPolicy,
            _request.ProviderConnectionProperties,
            _request.KafkaClientSecurityProperties,
            timing
        );
        _pass.Dispose();
        _pass = new(_request, "telemetry-test", 10);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public TimeSpan WallOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero).AddTicks(_ticks).Add(WallOffset);

        public void Advance(TimeSpan time) => _ticks += time.Ticks;
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Response { get; set; } = response;
        public Action BeforeResponse { get; set; } = () => { };
        public Uri RequestUri { get; private set; } = new("http://unset");
        public bool NoCache { get; private set; }
        public bool Pending { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUri = request.RequestUri!;
            NoCache = request.Headers.CacheControl is { NoCache: true, NoStore: true };
            BeforeResponse();
            cancellationToken.ThrowIfCancellationRequested();
            return Pending
                ? new TaskCompletionSource<HttpResponseMessage>().Task
                : Task.FromResult(Response());
        }
    }
}

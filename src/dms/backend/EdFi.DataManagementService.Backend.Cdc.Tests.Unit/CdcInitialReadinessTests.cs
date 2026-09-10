// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_CdcInitialReadiness(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    [SetUp]
    public void SetupTerminalContainment() =>
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                ConfigureStoppedReadBack(() => true);
                return Observed(new CdcTransportAcknowledgement());
            });

    [TestCase(
        CdcConnectOffsetState.Streaming,
        CdcConnectorSnapshotState.Unknown,
        CdcTransportEvidenceState.Observed
    )]
    [TestCase(
        CdcConnectOffsetState.Snapshot,
        CdcConnectorSnapshotState.Unknown,
        CdcTransportEvidenceState.Unavailable
    )]
    [TestCase(
        CdcConnectOffsetState.AwaitingStreaming,
        CdcConnectorSnapshotState.Unknown,
        CdcTransportEvidenceState.Unavailable
    )]
    [TestCase(
        CdcConnectOffsetState.Streaming,
        CdcConnectorSnapshotState.Running,
        CdcTransportEvidenceState.Unavailable
    )]
    public async Task It_requires_streaming_barrier_evidence_when_REST_cannot_report_snapshot_completion(
        CdcConnectOffsetState offsetState,
        CdcConnectorSnapshotState snapshotState,
        CdcTransportEvidenceState expected
    )
    {
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var status = Status();
                return Observed(
                    new CdcConnectStatus(
                        status.Runtime with
                        {
                            SnapshotState = snapshotState,
                        },
                        status.WorkerId,
                        status.Tasks
                    )
                );
            });
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var offset = Offsets();
                return Observed(
                    new CdcConnectOffsetEvidence(
                        offsetState,
                        offset.SourcePartitionHash,
                        offset.Postgresql,
                        offset.SqlServer with
                        {
                            EventSerialNo = CdcSqlServerProviderPosition.HeartbeatAfterImageEventSerialNo,
                        }
                    )
                    {
                        SourcePartition = offset.SourcePartition,
                    }
                );
            });

        (await ReadyAsync()).State.Should().Be(expected);
        ReadJournal().WriterPublicationAuthorized.Should().Be(expected == CdcTransportEvidenceState.Observed);
    }

    [TestCase("retained-gap", CdcIncidentFailureCategory.RetainedHistoryGap)]
    [TestCase("missing", CdcIncidentFailureCategory.ConnectOffsetMissing)]
    [TestCase("malformed", CdcIncidentFailureCategory.ConnectOffsetMalformed)]
    [TestCase("source-mismatch", CdcIncidentFailureCategory.ConnectSourcePartitionMismatch)]
    [TestCase("null", CdcIncidentFailureCategory.ConnectOffsetMalformed)]
    [TestCase("snapshot", CdcIncidentFailureCategory.ConnectOffsetMalformed)]
    public async Task It_retains_and_contains_established_history_loss_before_initial_publication(
        string failure,
        CdcIncidentFailureCategory expected
    )
    {
        ShortTiming(300);
        var healthyProvider = _change;
        var healthyOffset = Offsets();
        var offset = LostOffset(failure, healthyOffset);
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(offset));
        if (failure == "retained-gap")
        {
            _change = result =>
                healthyProvider(result) with
                {
                    ProviderHistoryObservations = healthyProvider(result)
                        .ProviderHistoryObservations.Select(h =>
                            h with
                            {
                                SafeObservedValues = h.SafeObservedValues.ToDictionary(
                                    kv => kv.Key,
                                    kv =>
                                        kv.Key switch
                                        {
                                            "restart_lsn" => "0_11",
                                            "confirmed_flush_lsn" => "0_11",
                                            "retained_min_lsn" => "0x00000001000000020004",
                                            _ => kv.Value,
                                        }
                                ),
                            }
                        )
                        .ToArray(),
                };
        }
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        bool stopped = false;
        ConfigureStoppedReadBack(() => stopped);
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(async () =>
            {
                var retained = await bindings.ExactMatchBindingAsync(_request.Binding);
                retained.State!.State.Should().Be(CdcBindingState.IncidentLatched);
                retained.State.Incident!.FailureCategory.Should().Be(expected);
                ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
                Func<Task> contender = async () =>
                {
                    await using var other = await _store.AcquireAsync(
                        TimeSpan.FromMilliseconds(20),
                        TimeSpan.FromMilliseconds(5),
                        CancellationToken.None
                    );
                };
                await contender.Should().ThrowAsync<CdcWorkflowStateException>();
                Trace("stop");
                stopped = true;
                return Observed(new CdcTransportAcknowledgement());
            });

        var result = await ReadyAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        var state = await bindings.ExactMatchBindingAsync(_request.Binding);
        state.State!.Incident.Should().NotBeNull();
        state.State.Incident!.FailureCategory.Should().Be(expected);
        stopped.Should().BeTrue();
        _trace.IndexOf("stop").Should().BeLessThan(_trace.IndexOf("stopped-readback"));
        _trace.IndexOf("stopped-readback").Should().BeLessThan(_trace.IndexOf("dispose"));
        _trace.Should().NotContain("metrics");
        _disposals.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        // Later healthy samples cannot erase the terminal generation seen in this invocation.
        _change = healthyProvider;
        stopped = false;
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(healthyOffset));
        _trace.Clear();
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().Equal("dispose");
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        await using var released = await _store.AcquireAsync(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_does_not_invent_history_loss_when_the_offset_endpoint_is_unavailable(bool absent)
    {
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(
                absent
                    ? new CdcTransportResult<CdcConnectOffsetEvidence>.Absent()
                    : new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                        new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                    )
            );
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        (await bindings.ExactMatchBindingAsync(_request.Binding)).State!.Incident.Should().BeNull();
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        _disposals.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [TestCase("persist")]
    [TestCase("stop")]
    [TestCase("readback")]
    public async Task It_attempts_shutdown_and_reports_initial_containment_failures(string failure)
    {
        ShortTiming();
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(LostOffset("missing", Offsets())));
        if (failure == "persist")
        {
            var real = _services.GetRequiredService<ICdcBindingLifecycleService>();
            var faulting = A.Fake<ICdcBindingLifecycleService>();
            A.CallTo(() => faulting.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
                .ReturnsLazily(
                    (CdcBinding binding, CancellationToken token) =>
                        real.ExactMatchBindingAsync(binding, token)
                );
            A.CallTo(() => faulting.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
                .Throws(new IOException("private-incident-storage"));
            _readiness = new(
                _store,
                faulting,
                _provider,
                _templates,
                _kafka,
                _connect,
                _worker,
                _metrics,
                _positions,
                TimeProvider.System
            );
        }
        bool stopRequested = false;
        ConfigureStoppedReadBack(() => stopRequested);
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                stopRequested = true;
                if (failure == "stop")
                {
                    throw new IOException("private-stop-response");
                }
                if (failure == "readback")
                {
                    A.CallTo(() =>
                            _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                        )
                        .Returns(
                            new CdcTransportResult<CdcConnectStatus>.Unavailable(
                                new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                            )
                        );
                }
                return Observed(new CdcTransportAcknowledgement());
            });

        var result = await ReadyAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Component.Should()
            .Be(failure == "persist" ? CdcDeploymentComponent.WorkflowState : CdcDeploymentComponent.Connect);
        JsonSerializer.Serialize(result).Should().NotContain("private-");
        stopRequested.Should().BeTrue();
        if (failure != "readback")
        {
            _trace.Should().Contain("stopped-readback");
        }
        _disposals.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        await using var released = await _store.AcquireAsync(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_preserves_caller_cancellation_during_initial_containment()
    {
        using var cancellation = new CancellationTokenSource();
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(LostOffset("missing", Offsets())));
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                cancellation.Cancel();
                return Observed(new CdcTransportAcknowledgement());
            });
        Func<Task> act = async () => await ReadyAsync(cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        (await bindings.ExactMatchBindingAsync(_request.Binding)).State!.Incident.Should().NotBeNull();
        _disposals.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        await using var released = await _store.AcquireAsync(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_finishes_initial_containment_after_the_admission_deadline_expires()
    {
        ShortTiming(300);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool stopRequested = false;
        ConfigureStoppedReadBack(() =>
            stopRequested && elapsed.Elapsed > _request.Timing.WaitTimeout + TimeSpan.FromMilliseconds(30)
        );
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcDeploymentRequest _, CancellationToken token) =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(150), token);
                    return Observed(LostOffset("missing", Offsets()));
                }
            );
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                stopRequested = true;
                return Observed(new CdcTransportAcknowledgement());
            });
        elapsed.Restart();
        var result = await ReadyAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Failure.Should()
            .Be(CdcDeploymentFailure.ValidationFailed);
        elapsed.Elapsed.Should().BeGreaterThan(_request.Timing.WaitTimeout);
        _trace.Should().Contain("stopped-readback");
        _trace.IndexOf("stopped-readback").Should().BeLessThan(_trace.IndexOf("dispose"));
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        (await bindings.ExactMatchBindingAsync(_request.Binding)).State!.Incident.Should().NotBeNull();
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    private static CdcConnectOffsetEvidence LostOffset(string failure, CdcConnectOffsetEvidence healthy) =>
        new(
            failure switch
            {
                "missing" => CdcConnectOffsetState.Missing,
                "null" => CdcConnectOffsetState.Null,
                "snapshot" => CdcConnectOffsetState.Snapshot,
                "malformed" => CdcConnectOffsetState.Malformed,
                _ => CdcConnectOffsetState.Streaming,
            },
            failure switch
            {
                "missing" => "",
                "source-mismatch" => "sha256:" + new string('b', 64),
                _ => healthy.SourcePartitionHash,
            },
            healthy.Postgresql with
            {
                SourcePartitionMatchResult =
                    failure == "missing"
                        ? CdcConnectorOffsetMatchResult.Missing
                        : CdcConnectorOffsetMatchResult.Exact,
                LsnProc = failure == "malformed" ? null : healthy.Postgresql.LsnProc,
            },
            healthy.SqlServer with
            {
                SourcePartitionMatchResult =
                    failure == "missing"
                        ? CdcConnectorOffsetMatchResult.Missing
                        : CdcConnectorOffsetMatchResult.Exact,
                CommitLsn = failure == "malformed" ? null : healthy.SqlServer.CommitLsn,
                EventSerialNo = CdcSqlServerProviderPosition.HeartbeatAfterImageEventSerialNo,
            }
        )
        {
            SourcePartition = healthy.SourcePartition,
        };

    private void ConfigureStoppedReadBack(Func<bool> stopped) =>
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var status = Status();
                if (!stopped())
                {
                    return Observed(status);
                }
                Trace("stopped-readback");
                return Observed(
                    new CdcConnectStatus(
                        status.Runtime with
                        {
                            ConnectorState = CdcConnectorRuntimeState.Stopped,
                            TaskCount = 0,
                            RunningTaskCount = 0,
                            SoleTaskState = CdcConnectorRuntimeState.Unknown,
                        },
                        status.WorkerId,
                        []
                    )
                );
            });

    [Test]
    public async Task It_orders_fresh_admission_and_disposal_before_durable_publication()
    {
        var result = await ReadyAsync();
        result.Diagnostics.Should().BeEmpty(string.Join(",", _trace));
        result.State.Should().Be(CdcTransportEvidenceState.Observed, string.Join(",", _trace));
        _trace.IndexOf("start").Should().BeLessThan(_trace.IndexOf("projection-1"));
        _trace.IndexOf("projection-1").Should().BeLessThan(_trace.IndexOf("barrier"));
        _trace.IndexOf("barrier").Should().BeLessThan(_trace.IndexOf("projection-2"));
        _trace.IndexOf("projection-2").Should().BeLessThan(_trace.IndexOf("metrics"));
        _trace.IndexOf("metrics").Should().BeLessThan(_trace.IndexOf("dispose"));
        _disposals.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeTrue();
        JsonSerializer
            .Serialize(ReadJournal())
            .Should()
            .NotContain("barrierLsn")
            .And.NotContain("currentLag");
    }

    [TestCase("projection-1")]
    [TestCase("barrier")]
    [TestCase("offset")]
    [TestCase("provider")]
    [TestCase("projection-2")]
    [TestCase("metrics")]
    [TestCase("dispose")]
    public async Task It_keeps_writers_closed_on_each_interruption_and_repeats_fresh_sequence(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new IOException("private-source-secret");
            }
        };
        var result = await ReadyAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        JsonSerializer.Serialize(result).Should().NotContain("private-source-secret");
        _disposals.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        _onCall = _ => { };
        _projectionReads = 0;
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _projectionReads.Should().Be(2);
    }

    [TestCase("projection-1")]
    [TestCase("barrier")]
    [TestCase("projection-2")]
    [TestCase("metrics")]
    public async Task It_preserves_cancellation_and_disposes_runtime(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == stage)
            {
                cancellation.Cancel();
            }
        };
        Func<Task> act = async () => await ReadyAsync(cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _disposals.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task It_repeats_the_whole_sequence_when_queue_work_is_observed(int observation)
    {
        _onCall = name =>
        {
            if (name.StartsWith("projection-", StringComparison.Ordinal))
            {
                _backlog = _projectionReads == observation;
            }
        };
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _projectionReads.Should().Be(observation + 2);
        _barriers.Should().Be(observation);
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task It_rejects_a_different_source_in_either_projection_observation(int observation)
    {
        _onCall = name =>
        {
            if (name == "projection-" + observation)
            {
                _wrongSource = true;
            }
        };
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_requires_current_lag_independently_of_the_crossed_barrier()
    {
        _lag = 1001;
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _barriers.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_initial_enablement_after_successful_publication()
    {
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _trace.Clear();
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().Equal("dispose");
        (await new CdcInitialEnablement(_root).ActivateAsync(_request, _runtime))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("workflows")]
    [TestCase("source-history")]
    [TestCase("bindings")]
    public async Task It_rejects_missing_provenance_before_starting_processing(string folder)
    {
        Directory.Delete(Path.Combine(_root, folder), true);
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().Equal("dispose");
    }

    [TestCase("barrier")]
    [TestCase("projection-2")]
    [TestCase("metrics")]
    [TestCase("dispose")]
    public async Task It_rejects_observed_worker_replacement_without_restarting_it(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                _workerEvidence = new(
                    "replaced",
                    _workerEvidence.MetricsEndpoint,
                    _workerEvidence.EffectiveConfiguration,
                    _workerEvidence.ImageDigest,
                    _workerEvidence.HeapBytes,
                    _workerEvidence.ConnectWorkerId
                );
            }
        };
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase(CdcConnectorRuntimeState.Failed)]
    [TestCase(CdcConnectorRuntimeState.Unassigned)]
    [TestCase(CdcConnectorRuntimeState.Stopped)]
    public async Task It_rejects_observed_task_recovery_before_publication(CdcConnectorRuntimeState state)
    {
        _onCall = name =>
        {
            if (name == "metrics")
            {
                _runtimeState = state;
            }
        };
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_repeats_the_barrier_and_both_observations_after_telemetry_expiry()
    {
        bool expired = false;
        _onCall = name =>
        {
            if (name == "status" && _trace.Contains("metrics") && !expired)
            {
                expired = true;
                _telemetryClock.Advance(_request.Timing.MaximumObservationAge + TimeSpan.FromSeconds(1));
            }
        };
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _barriers.Should().Be(2);
        _projectionReads.Should().Be(4);
    }

    [Test]
    public async Task It_refuses_handoff_if_telemetry_expires_during_runtime_shutdown()
    {
        _onCall = name =>
        {
            if (name == "dispose")
            {
                _telemetryClock.Advance(_request.Timing.MaximumObservationAge + TimeSpan.FromSeconds(1));
            }
        };
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _disposals.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_refuses_stale_handoff_after_the_publication_intent_is_durable()
    {
        _onWrite = boundary =>
        {
            if (boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement)
            {
                _telemetryClock.Advance(_request.Timing.MaximumObservationAge + TimeSpan.FromSeconds(1));
            }
        };
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal().WriterPublicationAuthorized.Should().BeTrue();
        _onWrite = _ => { };
        _trace.Clear();
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().Equal("dispose");
    }

    [TestCase(CdcWorkflowWriteBoundary.BeforeTemporaryWrite)]
    [TestCase(CdcWorkflowWriteBoundary.AfterTemporaryFlush)]
    [TestCase(CdcWorkflowWriteBoundary.AfterAtomicReplacement)]
    public async Task It_reconciles_crashes_at_publication_without_replaying_durable_authorization(
        CdcWorkflowWriteBoundary boundary
    )
    {
        _onWrite = current =>
        {
            if (current == boundary)
            {
                throw new IOException("private-crash");
            }
        };
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _onWrite = _ => { };
        bool durable = boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement;
        ReadJournal().WriterPublicationAuthorized.Should().Be(durable);
        _projectionReads = 0;
        (await ReadyAsync())
            .State.Should()
            .Be(durable ? CdcTransportEvidenceState.Unavailable : CdcTransportEvidenceState.Observed);
        _projectionReads.Should().Be(durable ? 0 : 2);
    }

    [Test]
    public async Task It_holds_the_controller_lock_until_the_runtime_is_disposed()
    {
        A.CallTo(() => _runtime.DisposeAsync())
            .ReturnsLazily(async _ =>
            {
                _disposals++;
                Func<Task> compete = async () =>
                {
                    await using var session = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                        TimeSpan.FromMilliseconds(25),
                        TimeSpan.FromMilliseconds(5),
                        CancellationToken.None
                    );
                };
                await compete.Should().ThrowAsync<CdcWorkflowStateException>();
            });
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
    }

    [TestCase("projection")]
    [TestCase("barrier")]
    [TestCase("offset")]
    public async Task It_bounds_unresponsive_phase_calls_and_cleans_up(string stage)
    {
        ShortTiming();
        if (stage == "projection")
        {
            A.CallTo(() => _runtime.ObserveAsync(A<CancellationToken>._))
                .Returns(new TaskCompletionSource<DocumentCacheStatusResponse>().Task);
        }
        else if (stage == "barrier")
        {
            A.CallTo(() =>
                    _runtime.CaptureBarrierAsync(
                        A<CdcDeploymentRequest>._,
                        A<ICdcProviderSourcePositionAdapter>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new TaskCompletionSource<CdcProviderBarrierCaptureResult>().Task);
        }
        else
        {
            A.CallTo(() =>
                    _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .Returns(new TaskCompletionSource<CdcTransportResult<CdcConnectOffsetEvidence>>().Task);
        }
        (await ReadyAsync())
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Failure.Should()
            .Be(CdcDeploymentFailure.Timeout);
        _disposals.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_does_not_substitute_running_or_low_lag_for_committed_barrier_progress()
    {
        int reads = 0;
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                reads++;
                var offset = Offsets();
                return Observed(
                    new CdcConnectOffsetEvidence(
                        offset.State,
                        offset.SourcePartitionHash,
                        offset.Postgresql with
                        {
                            LsnProc = reads < 3 ? 1 : 16,
                        },
                        offset.SqlServer with
                        {
                            EventSerialNo =
                                reads < 3 ? 0 : CdcSqlServerProviderPosition.HeartbeatAfterImageEventSerialNo,
                        }
                    )
                    {
                        SourcePartition = offset.SourcePartition,
                    }
                );
            });
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        reads.Should().Be(3);
        _barriers.Should().Be(1);
    }

    [Test]
    public async Task It_times_out_the_complete_wait_when_the_queue_never_drains()
    {
        ShortTiming(500);
        _backlog = true;
        (await ReadyAsync())
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Failure.Should()
            .Be(CdcDeploymentFailure.Timeout);
        _barriers.Should().Be(0);
        _disposals.Should().Be(1);
    }

    [TestCase(CdcSqlServerSchemaHistoryState.Missing)]
    [TestCase(CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset)]
    [TestCase(CdcSqlServerSchemaHistoryState.RequiredRecordLost)]
    [TestCase(CdcSqlServerSchemaHistoryState.Unknown)]
    public async Task It_requires_independent_sql_server_history_retention(
        CdcSqlServerSchemaHistoryState state
    )
    {
        A.CallTo(() => _kafka.InspectSchemaHistoryAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(state));
        var result = await ReadyAsync();
        result
            .State.Should()
            .Be(
                Provider == Ddl.CdcProvider.SqlServer
                    ? CdcTransportEvidenceState.Unavailable
                    : CdcTransportEvidenceState.Observed
            );
        ReadJournal().WriterPublicationAuthorized.Should().Be(Provider == Ddl.CdcProvider.Postgresql);
    }
}

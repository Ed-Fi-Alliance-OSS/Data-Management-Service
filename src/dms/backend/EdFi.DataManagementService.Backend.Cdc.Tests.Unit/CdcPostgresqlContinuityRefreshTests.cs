// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(false)]
[TestFixture(true)]
internal class Given_Postgresql_established_wal_observation_is_refreshed(bool remainsUnavailable)
    : CdcReadinessTestBase(Ddl.CdcProvider.Postgresql)
{
    private CdcEstablishedValidationObservation _result = null!;
    private int _sourceReads;

    [SetUp]
    public async Task SetupRefresh()
    {
        _sourceReads = 0;
        var original = _change;
        _change = r =>
        {
            _sourceReads++;
            var result = original(r);
            return result with
            {
                ProviderHistoryObservations = result
                    .ProviderHistoryObservations.Select(h =>
                        h with
                        {
                            SafeObservedValues = new Dictionary<string, string>(h.SafeObservedValues)
                            {
                                ["confirmed_flush_lsn"] = "0_1",
                                ["current_wal_lsn"] =
                                    _sourceReads == 1 || remainsUnavailable ? "0_2" : "0_20",
                            },
                        }
                    )
                    .ToArray(),
            };
        };
        var validation = new CdcEstablishedValidation(
            _store,
            _services.GetRequiredService<ICdcBindingLifecycleService>(),
            _provider,
            _templates,
            _kafka,
            _connect,
            _worker,
            _metrics,
            _positions,
            TimeProvider.System
        );
        var observed = await validation.ValidateAsync(
            _request,
            _runtime,
            CdcEstablishedValidationMode.PreStart,
            1000,
            CdcDeploymentIntegrityReport.NoReportedLoss
        );
        _result = observed
            .Should()
            .BeOfType<CdcTransportResult<CdcEstablishedValidationObservation>.Observed>()
            .Subject.Value;
    }

    [Test]
    public void It_refreshes_source_evidence_once() => _sourceReads.Should().Be(2);

    [Test]
    public void It_requires_affirmative_evidence_for_prestart() =>
        _result.PreStartEligible.Should().Be(!remainsUnavailable);

    [Test]
    public void It_does_not_latch_loss_from_an_older_upper_wal_sample() =>
        _result.SourceHistory.IncidentCandidate.Should().BeNull();

    [Test]
    public void It_preserves_unknown_when_refresh_does_not_prove_continuity() =>
        _result
            .Continuity.Should()
            .Be(remainsUnavailable ? CdcSourceHistoryContinuity.Unknown : CdcSourceHistoryContinuity.Healthy);
}

[TestFixture(false)]
[TestFixture(true)]
internal class Given_Postgresql_admission_reads_the_offset_after_the_slot(bool offsetBehindSlot)
    : CdcReadinessTestBase(Ddl.CdcProvider.Postgresql)
{
    private CdcTransportEvidenceState _state;
    private int _reads;
    private bool _stopped;

    [SetUp]
    public async Task SetupAdmission()
    {
        ShortTiming(1000);
        _stopped = false;
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _stopped = true;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var status = Status();
                if (!_stopped)
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
        var original = _change;
        _change = r =>
        {
            var result = original(r);
            return result with
            {
                ProviderHistoryObservations = result
                    .ProviderHistoryObservations.Select(h =>
                        h with
                        {
                            SafeObservedValues = new Dictionary<string, string>(h.SafeObservedValues)
                            {
                                ["confirmed_flush_lsn"] = "0_11",
                            },
                        }
                    )
                    .ToArray(),
            };
        };
        _reads = 0;
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _reads++;
                Trace("offset");
                var offset = Offsets();
                return Observed(
                    new CdcConnectOffsetEvidence(
                        offset.State,
                        offset.SourcePartitionHash,
                        offset.Postgresql with
                        {
                            // The slot is sampled first, so a fresh offset behind it proves loss.
                            // A later healthy offset must not erase that already observed gap.
                            LsnProc = offsetBehindSlot && _reads == 1 ? 16 : 17,
                        },
                        offset.SqlServer
                    )
                    {
                        SourcePartition = offset.SourcePartition,
                    }
                );
            });
        _state = (await ReadyAsync()).State;
    }

    [Test]
    public void It_reads_the_offset_after_sampling_the_slot() =>
        _trace.Should().ContainInOrder("provider", "offset");

    [Test]
    public void It_uses_the_fresh_offset_without_resampling_a_proven_gap() => _reads.Should().Be(1);

    [Test]
    public void It_only_admits_a_resumable_offset() =>
        _state
            .Should()
            .Be(
                offsetBehindSlot ? CdcTransportEvidenceState.Unavailable : CdcTransportEvidenceState.Observed
            );

    [Test]
    public void It_keeps_writer_authorization_closed_for_a_proven_gap() =>
        ReadJournal().WriterPublicationAuthorized.Should().Be(!offsetBehindSlot);

    [Test]
    public void It_verifies_connector_shutdown_only_for_a_proven_gap() =>
        _trace.Contains("stopped-readback").Should().Be(offsetBehindSlot);
}

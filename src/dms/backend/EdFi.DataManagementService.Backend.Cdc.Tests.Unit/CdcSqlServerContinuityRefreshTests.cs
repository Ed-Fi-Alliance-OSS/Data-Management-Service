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

internal enum SqlServerRangeRefreshCase
{
    CatchesUp,
    StillBehind,
    RetentionAndOffsetAdvance,
    RetentionOvertakesOffset,
    OffsetUnavailable,
}

[TestFixture(SqlServerRangeRefreshCase.CatchesUp)]
[TestFixture(SqlServerRangeRefreshCase.StillBehind)]
[TestFixture(SqlServerRangeRefreshCase.RetentionAndOffsetAdvance)]
[TestFixture(SqlServerRangeRefreshCase.RetentionOvertakesOffset)]
[TestFixture(SqlServerRangeRefreshCase.OffsetUnavailable)]
internal class Given_SqlServer_established_range_is_refreshed(SqlServerRangeRefreshCase scenario)
    : CdcReadinessTestBase(Ddl.CdcProvider.SqlServer)
{
    private CdcEstablishedValidationObservation _result = null!;
    private int _sourceReads;

    [SetUp]
    public async Task SetupRefresh()
    {
        _sourceReads = 0;
        _offsetReads = 0;
        var floorAdvances =
            scenario
            is SqlServerRangeRefreshCase.RetentionAndOffsetAdvance
                or SqlServerRangeRefreshCase.RetentionOvertakesOffset;
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _offsetReads++;
                if (scenario == SqlServerRangeRefreshCase.OffsetUnavailable && _offsetReads > 1)
                {
                    return new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                        new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                    );
                }
                var offset = Offsets();
                return Observed(
                    new CdcConnectOffsetEvidence(
                        offset.State,
                        offset.SourcePartitionHash,
                        offset.Postgresql,
                        offset.SqlServer with
                        {
                            CommitLsn =
                                scenario == SqlServerRangeRefreshCase.RetentionAndOffsetAdvance
                                && _offsetReads > 1
                                    ? "00000001:00000002:0004"
                                    : "00000001:00000002:0003",
                            ChangeLsn =
                                scenario == SqlServerRangeRefreshCase.RetentionAndOffsetAdvance
                                && _offsetReads > 1
                                    ? "00000001:00000002:0004"
                                    : "00000001:00000002:0003",
                            EventSerialNo = CdcSqlServerProviderPosition.HeartbeatAfterImageEventSerialNo,
                        }
                    )
                    {
                        SourcePartition = offset.SourcePartition,
                    }
                );
            });
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
                                ["retained_min_lsn"] =
                                    floorAdvances && _sourceReads > 1
                                        ? "0x00000001000000020004"
                                        : "0x00000001000000020001",
                                ["retained_max_lsn"] =
                                    _sourceReads == 1 || scenario == SqlServerRangeRefreshCase.StillBehind
                                        ? "0x00000001000000020002"
                                        : "0x00000001000000020004",
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
    public void It_reads_the_offset_again_after_refreshing_the_range() => _offsetReads.Should().Be(2);

    [Test]
    public void It_requires_affirmative_evidence_for_prestart() =>
        _result
            .PreStartEligible.Should()
            .Be(
                scenario
                    is SqlServerRangeRefreshCase.CatchesUp
                        or SqlServerRangeRefreshCase.RetentionAndOffsetAdvance
            );

    [Test]
    public void It_only_reports_loss_when_the_fresh_offset_is_below_the_retention_floor()
    {
        if (scenario == SqlServerRangeRefreshCase.RetentionOvertakesOffset)
        {
            _result.SourceHistory.IncidentCandidate.Should().NotBeNull();
        }
        else
        {
            _result.SourceHistory.IncidentCandidate.Should().BeNull();
        }
    }

    [Test]
    public void It_classifies_the_refreshed_evidence() =>
        _result
            .Continuity.Should()
            .Be(
                scenario switch
                {
                    SqlServerRangeRefreshCase.StillBehind or SqlServerRangeRefreshCase.OffsetUnavailable =>
                        CdcSourceHistoryContinuity.Unknown,
                    SqlServerRangeRefreshCase.RetentionOvertakesOffset => CdcSourceHistoryContinuity.Lost,
                    _ => CdcSourceHistoryContinuity.Healthy,
                }
            );
}

[TestFixture(false)]
[TestFixture(true)]
internal class Given_SqlServer_admission_samples_an_older_upper_range(bool remainsUnavailable)
    : CdcReadinessTestBase(Ddl.CdcProvider.SqlServer)
{
    private CdcTransportEvidenceState _state;
    private int _sourceReads;

    [SetUp]
    public async Task SetupAdmission()
    {
        ShortTiming(1000);
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
                                ["retained_max_lsn"] =
                                    _sourceReads == 1 || remainsUnavailable
                                        ? "0x00000001000000020002"
                                        : "0x00000001000000020004",
                            },
                        }
                    )
                    .ToArray(),
            };
        };
        _state = (await ReadyAsync()).State;
    }

    [Test]
    public void It_collects_a_fresh_range_before_admission() => _sourceReads.Should().BeGreaterThan(1);

    [Test]
    public void It_requires_affirmative_retention_evidence() =>
        _state
            .Should()
            .Be(
                remainsUnavailable
                    ? CdcTransportEvidenceState.Unavailable
                    : CdcTransportEvidenceState.Observed
            );
}

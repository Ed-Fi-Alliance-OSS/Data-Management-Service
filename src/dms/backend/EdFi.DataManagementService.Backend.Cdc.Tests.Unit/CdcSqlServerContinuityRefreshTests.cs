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
    OffsetAdvancesAgain,
    FinalSampleCatchesUp,
    FinalOffsetUnavailable,
    FinalRetentionLoss,
    InitialOffsetUnavailable,
    RetentionAndOffsetAdvance,
    RetentionOvertakesOffset,
    OffsetUnavailable,
}

[TestFixture(SqlServerRangeRefreshCase.CatchesUp)]
[TestFixture(SqlServerRangeRefreshCase.StillBehind)]
[TestFixture(SqlServerRangeRefreshCase.OffsetAdvancesAgain)]
[TestFixture(SqlServerRangeRefreshCase.FinalSampleCatchesUp)]
[TestFixture(SqlServerRangeRefreshCase.FinalOffsetUnavailable)]
[TestFixture(SqlServerRangeRefreshCase.FinalRetentionLoss)]
[TestFixture(SqlServerRangeRefreshCase.InitialOffsetUnavailable)]
[TestFixture(SqlServerRangeRefreshCase.RetentionAndOffsetAdvance)]
[TestFixture(SqlServerRangeRefreshCase.RetentionOvertakesOffset)]
[TestFixture(SqlServerRangeRefreshCase.OffsetUnavailable)]
internal class Given_SqlServer_established_range_is_refreshed(SqlServerRangeRefreshCase scenario)
    : CdcReadinessTestBase(Ddl.CdcProvider.SqlServer)
{
    private CdcEstablishedValidationObservation _result = null!;
    private int _sourceReads;
    private readonly List<string> _readOrder = [];

    [SetUp]
    public async Task SetupRefresh()
    {
        _readOrder.Clear();
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
                _readOrder.Add("offset");
                if (
                    scenario == SqlServerRangeRefreshCase.InitialOffsetUnavailable
                    || (scenario == SqlServerRangeRefreshCase.OffsetUnavailable && _offsetReads > 1)
                    || (scenario == SqlServerRangeRefreshCase.FinalOffsetUnavailable && _offsetReads == 3)
                )
                {
                    return new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                        new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                    );
                }
                var position = scenario switch
                {
                    SqlServerRangeRefreshCase.OffsetAdvancesAgain
                    or SqlServerRangeRefreshCase.FinalSampleCatchesUp
                    or SqlServerRangeRefreshCase.FinalOffsetUnavailable
                    or SqlServerRangeRefreshCase.FinalRetentionLoss when _offsetReads > 1 =>
                        "00000001:00000002:0005",
                    SqlServerRangeRefreshCase.RetentionAndOffsetAdvance when _offsetReads > 1 =>
                        "00000001:00000002:0004",
                    _ => "00000001:00000002:0003",
                };
                var offset = Offsets();
                return Observed(
                    new CdcConnectOffsetEvidence(
                        offset.State,
                        offset.SourcePartitionHash,
                        offset.Postgresql,
                        offset.SqlServer with
                        {
                            CommitLsn = position,
                            ChangeLsn = position,
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
            _readOrder.Add("provider");
            var result = original(r);
            var minimum = scenario switch
            {
                SqlServerRangeRefreshCase.FinalRetentionLoss when _sourceReads == 3 =>
                    "0x00000001000000020006",
                _ when floorAdvances && _sourceReads > 1 => "0x00000001000000020004",
                _ => "0x00000001000000020001",
            };
            var maximum = scenario switch
            {
                _ when _sourceReads == 1 => "0x00000001000000020002",
                SqlServerRangeRefreshCase.StillBehind => "0x00000001000000020002",
                SqlServerRangeRefreshCase.FinalSampleCatchesUp
                or SqlServerRangeRefreshCase.FinalRetentionLoss when _sourceReads == 3 =>
                    "0x00000001000000020006",
                _ => "0x00000001000000020004",
            };
            return result with
            {
                ProviderHistoryObservations = result
                    .ProviderHistoryObservations.Select(h =>
                        h with
                        {
                            SafeObservedValues = new Dictionary<string, string>(h.SafeObservedValues)
                            {
                                ["retained_min_lsn"] = minimum,
                                ["retained_max_lsn"] = maximum,
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

    private int ExpectedSamples =>
        scenario switch
        {
            SqlServerRangeRefreshCase.InitialOffsetUnavailable => 1,
            SqlServerRangeRefreshCase.StillBehind
            or SqlServerRangeRefreshCase.OffsetAdvancesAgain
            or SqlServerRangeRefreshCase.FinalSampleCatchesUp
            or SqlServerRangeRefreshCase.FinalOffsetUnavailable
            or SqlServerRangeRefreshCase.FinalRetentionLoss => 3,
            _ => 2,
        };

    [Test]
    public void It_bounds_source_reads_and_stops_on_conclusive_or_unavailable_evidence() =>
        _sourceReads.Should().Be(ExpectedSamples);

    [Test]
    public void It_reads_the_offset_again_after_refreshing_the_range() =>
        _offsetReads.Should().Be(ExpectedSamples);

    [Test]
    public void It_samples_the_provider_before_each_offset() =>
        _readOrder
            .Should()
            .Equal(Enumerable.Range(0, ExpectedSamples).SelectMany(_ => new[] { "provider", "offset" }));

    [Test]
    public void It_requires_affirmative_evidence_for_prestart() =>
        _result
            .PreStartEligible.Should()
            .Be(
                scenario
                    is SqlServerRangeRefreshCase.CatchesUp
                        or SqlServerRangeRefreshCase.FinalSampleCatchesUp
                        or SqlServerRangeRefreshCase.RetentionAndOffsetAdvance
            );

    [Test]
    public void It_only_reports_loss_when_the_fresh_offset_is_below_the_retention_floor()
    {
        if (
            scenario
            is SqlServerRangeRefreshCase.RetentionOvertakesOffset
                or SqlServerRangeRefreshCase.FinalRetentionLoss
        )
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
                    SqlServerRangeRefreshCase.StillBehind
                    or SqlServerRangeRefreshCase.OffsetUnavailable
                    or SqlServerRangeRefreshCase.OffsetAdvancesAgain
                    or SqlServerRangeRefreshCase.FinalOffsetUnavailable
                    or SqlServerRangeRefreshCase.InitialOffsetUnavailable =>
                        CdcSourceHistoryContinuity.Unknown,
                    SqlServerRangeRefreshCase.RetentionOvertakesOffset
                    or SqlServerRangeRefreshCase.FinalRetentionLoss => CdcSourceHistoryContinuity.Lost,
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

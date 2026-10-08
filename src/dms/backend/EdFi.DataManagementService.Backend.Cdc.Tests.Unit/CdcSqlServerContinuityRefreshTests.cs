// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.Channels;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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

    protected override TimeProvider CreateObservationTime() =>
        new ContinuityRefreshClock(TimeSpan.FromSeconds(1));

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
            ObservationTime
        );
        var clock = (ContinuityRefreshClock)ObservationTime;
        using var cancellation = new CancellationTokenSource();
        var observation = validation.ValidateAsync(
            _request,
            _runtime,
            CdcEstablishedValidationMode.PreStart,
            1000,
            CdcDeploymentIntegrityReport.NoReportedLoss,
            cancellation.Token
        );
        try
        {
            for (var sample = 1; sample < ExpectedSamples; sample++)
            {
                await clock.WaitForPollAsync(observation);
                _sourceReads.Should().Be(sample);
                _offsetReads.Should().Be(sample);
                observation.IsCompleted.Should().BeFalse();
                clock.Advance(_request.Timing.PollInterval);
            }
            _result = (await observation)
                .Should()
                .BeOfType<CdcTransportResult<CdcEstablishedValidationObservation>.Observed>()
                .Subject.Value;
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await observation;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Drain validation before fixture teardown if a polling assertion failed.
            }
        }
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
    private CdcTransportResult<CdcWriterPublicationResult> _result = null!;
    private int _sourceReads;

    protected override TimeProvider CreateObservationTime() =>
        // Admission requires strictly ordered barrier, history, and projection timestamps.
        new ContinuityRefreshClock(TimeSpan.FromMilliseconds(5))
        {
            AutoAdvanceAmount = TimeSpan.FromTicks(1),
        };

    [SetUp]
    public async Task SetupAdmission()
    {
        ShortTiming(1000);
        var clock = (ContinuityRefreshClock)ObservationTime;
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
        using var cancellation = new CancellationTokenSource();
        Task<CdcTransportResult<CdcWriterPublicationResult>> readiness = ReadyAsync(cancellation.Token);
        try
        {
            await clock.WaitForPollAsync(readiness);
            _sourceReads.Should().Be(1);
            readiness.IsCompleted.Should().BeFalse("unknown retention evidence must not authorize admission");
            clock.Advance(_request.Timing.PollInterval);
            if (remainsUnavailable)
            {
                ITimer nextPoll = await clock.WaitForPollAsync(readiness);
                _sourceReads.Should().Be(2);
                readiness.IsCompleted.Should().BeFalse();
                // Hold this poll so deadline expiry cannot race a third observation.
                nextPoll.Dispose();
                clock.Advance(_request.Timing.WaitTimeout - _request.Timing.PollInterval);
            }
            _result = await readiness;
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await readiness;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Drain the operation before fixture teardown if an assertion failed while it was polling.
            }
        }
    }

    [Test]
    public void It_collects_a_fresh_range_before_admission() => _sourceReads.Should().Be(2);

    [Test]
    public void It_requires_affirmative_retention_evidence() =>
        _result
            .State.Should()
            .Be(
                remainsUnavailable
                    ? CdcTransportEvidenceState.Unavailable
                    : CdcTransportEvidenceState.Observed
            );

    [Test]
    public void It_only_authorizes_publication_after_the_range_catches_up() =>
        ReadJournal()
            .Operations.Count(o => o.Effect == CdcWorkflowEffect.AuthorizeWriterPublication)
            .Should()
            .Be(remainsUnavailable ? 0 : 1);

    [Test]
    public void It_reports_the_controlled_deadline_when_retention_remains_unknown()
    {
        if (remainsUnavailable)
        {
            _result
                .Should()
                .BeOfType<CdcTransportResult<CdcWriterPublicationResult>.Unavailable>()
                .Which.Diagnostics.Should()
                .ContainSingle()
                .Which.Failure.Should()
                .Be(CdcDeploymentFailure.Timeout);
        }
        else
        {
            _result.Should().BeOfType<CdcTransportResult<CdcWriterPublicationResult>.Observed>();
        }
    }
}

[TestFixture(false)]
[TestFixture(true)]
internal class Given_SqlServer_continuity_is_unknown_without_a_stale_range_end(bool initialAdmission)
    : CdcReadinessTestBase(Ddl.CdcProvider.SqlServer)
{
    private CdcSourceHistoryObservationRequest _historyRequest = null!;
    private CdcSourceHistoryClassificationResult _history = null!;
    private int _sourceReads;

    protected override TimeProvider CreateObservationTime() =>
        new ContinuityRefreshClock(TimeSpan.FromMilliseconds(5))
        {
            AutoAdvanceAmount = TimeSpan.FromTicks(1),
        };

    [SetUp]
    public async Task SetupUnknownContinuity()
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
                                ["retained_min_lsn"] = "",
                            },
                        }
                    )
                    .ToArray(),
            };
        };
        var positions = _positions;
        _positions = A.Fake<ICdcProviderSourcePositionAdapter>(options => options.Wrapping(positions));
        A.CallTo(() =>
                _positions.ObserveSourceHistoryAsync(
                    A<CdcSourceHistoryObservationRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (CdcSourceHistoryObservationRequest request, CancellationToken token) =>
                {
                    _historyRequest = request;
                    _history = await positions.ObserveSourceHistoryAsync(request, token);
                    return _history;
                }
            );
        ResetReadiness();
        using var cancellation = new CancellationTokenSource();
        var observation = ObserveAsync(cancellation.Token);
        try
        {
            await ((ContinuityRefreshClock)ObservationTime).RequireCompletionWithoutPollAsync(observation);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await observation;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Drain an unexpected refresh before fixture teardown.
            }
        }
    }

    private async Task ObserveAsync(CancellationToken token)
    {
        if (initialAdmission)
        {
            var result = await ReadyAsync(token);
            result.Should().BeOfType<CdcTransportResult<CdcWriterPublicationResult>.Unavailable>();
            result
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Failure.Should()
                .Be(CdcDeploymentFailure.ValidationFailed);
        }
        else
        {
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
                ObservationTime
            );
            var result = await validation.ValidateAsync(
                _request,
                _runtime,
                CdcEstablishedValidationMode.PreStart,
                1000,
                cancellationToken: token
            );
            result
                .Should()
                .BeOfType<CdcTransportResult<CdcEstablishedValidationObservation>.Observed>()
                .Which.Value.PreStartEligible.Should()
                .BeFalse();
        }
    }

    [Test]
    public void It_observes_an_offset_before_rejecting_unknown_continuity() =>
        _historyRequest.ConnectorOffset.Should().NotBeNull();

    [Test]
    public void It_preserves_unknown_continuity() =>
        _history.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);

    [Test]
    public void It_has_no_stale_range_end_diagnostic() =>
        _history
            .Observation.Diagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.ProviderHistoryUnknown
                && diagnostic.Path == "$.providerHistory.retainedRangeEnd"
            );

    [Test]
    public void It_reads_the_provider_only_once() => _sourceReads.Should().Be(1);

    [Test]
    public void It_reads_the_offset_only_once() => _trace.Count(call => call == "offset").Should().Be(1);

    [Test]
    public void It_keeps_writer_publication_unauthorized() =>
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
}

[TestFixture(false)]
[TestFixture(true)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_established_validation_deadlines_use_the_injected_clock(bool overallDeadline)
    : CdcReadinessTestBase(Ddl.CdcProvider.SqlServer)
{
    private CdcTransportResult<CdcEstablishedValidationObservation> _result = null!;

    protected override TimeProvider CreateObservationTime() =>
        new ContinuityRefreshClock(TimeSpan.FromMilliseconds(5));

    [SetUp]
    public async Task SetupDeadline()
    {
        ShortTiming(1000);
        var clock = (ContinuityRefreshClock)ObservationTime;
        var started = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var pending = new TaskCompletionSource<Ddl.CdcProviderSetupResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var reads = 0;
        A.CallTo(() => _provider.SetupAsync(A<Ddl.CdcProviderSetupRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                (Ddl.CdcProviderSetupRequest request, CancellationToken token) =>
                {
                    if (overallDeadline && ++reads == 1)
                    {
                        var result = ProviderResult(request);
                        return Task.FromResult(
                            result with
                            {
                                ProviderHistoryObservations = result
                                    .ProviderHistoryObservations.Select(h =>
                                        h with
                                        {
                                            SafeObservedValues = new Dictionary<string, string>(
                                                h.SafeObservedValues
                                            )
                                            {
                                                ["retained_max_lsn"] = "0x00000001000000020002",
                                            },
                                        }
                                    )
                                    .ToArray(),
                            }
                        );
                    }
                    started.SetResult(token);
                    return pending.Task;
                }
            );
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
            ObservationTime
        );
        using var cancellation = new CancellationTokenSource();
        var observation = validation.ValidateAsync(
            _request,
            _runtime,
            CdcEstablishedValidationMode.PreStart,
            1000,
            cancellationToken: cancellation.Token
        );
        try
        {
            var remaining = _request.Timing.CallTimeout;
            if (overallDeadline)
            {
                await clock.WaitForPollAsync(observation);
                // Start the next call with less overall budget left than its own call timeout.
                remaining /= 2;
                clock.Advance(_request.Timing.WaitTimeout - remaining);
            }
            (await Task.WhenAny(started.Task, observation)).Should().BeSameAs(started.Task);
            CancellationToken callToken = await started.Task;
            callToken.IsCancellationRequested.Should().BeFalse();
            clock.Advance(remaining);
            callToken.IsCancellationRequested.Should().BeTrue("virtual time must expire the active deadline");
            _result = await observation;
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await observation;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Drain validation before fixture teardown if a deadline assertion failed.
            }
        }
    }

    [Test]
    public void It_reports_the_provider_timeout() =>
        _result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new CdcDeploymentDiagnostic(
                    CdcDeploymentComponent.ProviderSetup,
                    CdcDeploymentFailure.Timeout
                )
            );
}

file sealed class ContinuityRefreshClock(TimeSpan pollInterval) : FakeTimeProvider(DateTimeOffset.UtcNow)
{
    private readonly Channel<ITimer> _polls = Channel.CreateUnbounded<ITimer>();

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime == pollInterval)
        {
            _polls.Writer.TryWrite(timer);
        }
        return timer;
    }

    public async Task<ITimer> WaitForPollAsync(Task observation)
    {
        Task<ITimer> poll = _polls.Reader.ReadAsync().AsTask();
        // Fail promptly if the refresh behavior is removed, instead of waiting for a signal forever.
        (await Task.WhenAny(poll, observation))
            .Should()
            .BeSameAs(poll);
        return await poll;
    }

    public async Task RequireCompletionWithoutPollAsync(Task observation)
    {
        using var cancellation = new CancellationTokenSource();
        Task<ITimer> poll = _polls.Reader.ReadAsync(cancellation.Token).AsTask();
        try
        {
            (await Task.WhenAny(poll, observation)).Should().BeSameAs(observation);
            await observation;
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }
}

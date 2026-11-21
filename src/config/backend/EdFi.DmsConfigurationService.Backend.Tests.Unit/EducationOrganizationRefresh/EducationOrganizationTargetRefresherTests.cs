// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using ReadResult = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionReadResult;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationRefresh;

public class EducationOrganizationTargetRefresherTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    [TestFixture]
    public class Given_a_target_that_refreshes
    {
        private RefresherHarness _harness = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _outcome = await _harness.RunAsync();
        }

        [Test]
        public void It_returns_the_refreshed_generation_and_item_count() =>
            _outcome.Should().Be(new TargetRefreshOutcome.Refreshed(RefresherHarness.Generation, 2));

        [Test]
        public void It_runs_the_protocol_in_order() =>
            _harness
                .Calls.Should()
                .Equal("Acquire", "Reclaim", "Renew", "Read", "Renew", "Stage", "Fence", "Activate", "Prune");

        [Test]
        public void It_acquires_with_a_new_execution_token() =>
            _harness.Owner.Should().MatchRegex("^[0-9a-f]{32}$");

        [Test]
        public void It_uses_the_acquired_lease_for_every_lease_operation() =>
            _harness
                .LeasesUsed.Should()
                .HaveCount(5)
                .And.AllBeEquivalentTo(
                    new EducationOrganizationSnapshotLease(
                        RefresherHarness.DataStoreId,
                        _harness.Owner,
                        RefresherHarness.Generation
                    )
                );

        [Test]
        public void It_reads_the_target_in_the_job_tenant() =>
            _harness
                .ReadRequest.Should()
                .Be(
                    new EducationOrganizationProjectionReadRequest(
                        "Tenant_7",
                        RefresherHarness.DataStoreId,
                        _harness.Target.Contexts
                    )
                );

        [Test]
        public void It_stages_the_read_items_and_contract_version()
        {
            _harness.StagedItems.Should().BeSameAs(_harness.ReadItems);
            _harness.StagedContractVersion.Should().Be("1.0.0");
        }

        [Test]
        public void It_activates_in_the_fence_transaction_with_the_read_contract_version()
        {
            _harness.ActivatedTransaction.Should().BeSameAs(_harness.Fence.Transaction);
            _harness.ActivatedContractVersion.Should().Be("1.0.0");
        }

        [Test]
        public void It_prunes_the_data_store() =>
            _harness.PrunedDataStoreId.Should().Be(RefresherHarness.DataStoreId);

        [Test]
        public void It_logs_the_refresh()
        {
            LogEntry entry = _harness.Logger.Entries.Should().ContainSingle().Subject;
            entry.EventId.Should().Be(EducationOrganizationTargetRefresher.TargetRefreshed);
            entry.EventId.Name.Should().Be("EducationOrganizationTargetRefreshed");
            entry.Field("TenantId").Should().Be(7L);
            entry.Field("DataStoreId").Should().Be(RefresherHarness.DataStoreId);
            entry.Field("Generation").Should().Be(RefresherHarness.Generation);
            entry.Field("ItemCount").Should().Be(2);
            entry.Field("ContractVersion").Should().Be("1.0.0");
            entry.Field("PageCount").Should().Be(4);
        }
    }

    [TestFixture]
    public class Given_a_single_tenant_refresh
    {
        private RefresherHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness { Tenant = new TenantContext.NotMultitenant() };
            await _harness.RunAsync();
        }

        [Test]
        public void It_reads_without_a_tenant_name() => _harness.ReadRequest!.TenantName.Should().BeNull();

        [Test]
        public void It_logs_no_tenant_id() =>
            _harness.Logger.Entries.Single().Field("TenantId").Should().BeNull();
    }

    [TestFixture]
    public class Given_every_step_before_the_read_completes_synchronously
    {
        private RefresherHarness _harness = null!;
        private string[] _callsWhenRefreshFirstYielded = null!;
        private bool _completedWhenRefreshFirstYielded;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            TaskCompletionSource<ReadResult> read = new();
            _harness.Read = _ => read.Task;

            Task<TargetRefreshOutcome> run = _harness.RunAsync();
            _callsWhenRefreshFirstYielded = [.. _harness.Calls];
            _completedWhenRefreshFirstYielded = run.IsCompleted;

            read.SetResult(RefresherHarness.SuccessfulRead(_harness.ReadItems));
            await run.WaitAsync(_wait);
        }

        [Test]
        public void It_calls_the_reader_before_anything_is_awaited_after_the_admission_renewal()
        {
            // Every earlier step returned a completed task, so the refresh reached the reader without yielding. Any
            // awaited operation between admission and the read would have yielded first, and the reader would not have
            // been called yet.
            _completedWhenRefreshFirstYielded.Should().BeFalse();
            _callsWhenRefreshFirstYielded.Should().Equal("Acquire", "Reclaim", "Renew", "Read");
        }
    }

    [TestFixture(SnapshotLeaseLossReason.Taken, TargetLeaseLossReason.Taken)]
    [TestFixture(SnapshotLeaseLossReason.Expired, TargetLeaseLossReason.Expired)]
    public class Given_an_admission_renewal_that_finds_the_lease_lost(
        SnapshotLeaseLossReason lost,
        TargetLeaseLossReason expected
    )
    {
        private RefresherHarness _harness = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Renew = _ => RefresherHarness.Result(new SnapshotTargetResult.LeaseLost(lost));
            _outcome = await _harness.RunAsync();
        }

        [Test]
        public void It_returns_the_loss() =>
            _outcome.Should().Be(new TargetRefreshOutcome.LeaseLost(expected));

        [Test]
        public void It_releases_without_reading() =>
            _harness.Calls.Should().Equal("Acquire", "Reclaim", "Renew", "Release");

        [Test]
        public void It_logs_the_loss_at_admission()
        {
            LogEntry entry = _harness.Logger.Entries.Should().ContainSingle().Subject;
            entry.EventId.Should().Be(EducationOrganizationTargetRefresher.TargetLeaseLost);
            entry.Field("Operation").Should().Be("Admission");
            entry.Field("Reason").Should().Be(expected.ToString());
        }
    }

    public enum AdmissionFailure
    {
        DbFailure,
        ThrowsBeforeReturning,
        Faults,
        DataStoreRemoved,
    }

    [TestFixture(AdmissionFailure.DbFailure)]
    [TestFixture(AdmissionFailure.ThrowsBeforeReturning)]
    [TestFixture(AdmissionFailure.Faults)]
    [TestFixture(AdmissionFailure.DataStoreRemoved)]
    public class Given_an_admission_renewal_that_does_not_succeed(AdmissionFailure failure)
    {
        private RefresherHarness _harness = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Renew = failure switch
            {
                AdmissionFailure.DbFailure => _ =>
                    RefresherHarness.Result(new SnapshotTargetResult.DbFailure(RefresherHarness.Diagnostic)),
                AdmissionFailure.ThrowsBeforeReturning => _ => throw new InvalidOperationException(),
                AdmissionFailure.Faults => _ =>
                    Task.FromException<SnapshotTargetResult>(new InvalidOperationException()),
                _ => _ => RefresherHarness.Result(new SnapshotTargetResult.DataStoreRemoved()),
            };
            _outcome = await _harness.RunAsync();
        }

        [Test]
        public void It_returns_an_uncertain_loss_or_the_removal() =>
            _outcome
                .Should()
                .Be(
                    failure == AdmissionFailure.DataStoreRemoved
                        ? new TargetRefreshOutcome.Removed()
                        : new TargetRefreshOutcome.LeaseLost(TargetLeaseLossReason.RenewalUncertain)
                );

        [Test]
        public void It_releases_without_reading() =>
            _harness.Calls.Should().Equal("Acquire", "Reclaim", "Renew", "Release");
    }

    [TestFixture]
    public class Given_an_admission_renewal_that_outlasts_its_timeout
    {
        private RefresherHarness _harness = null!;
        private bool _completedJustBeforeTheTimeout;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Renew = _ => new TaskCompletionSource<SnapshotTargetResult>().Task;

            Task<TargetRefreshOutcome> run = _harness.RunAsync();
            _harness.Time.Advance(_harness.Settings.RenewalTimeout - TimeSpan.FromTicks(1));
            _completedJustBeforeTheTimeout = run.IsCompleted;
            _harness.Time.Advance(TimeSpan.FromTicks(1));
            _outcome = await run.WaitAsync(_wait);
        }

        [Test]
        public void It_waits_for_half_the_renewal_interval() =>
            _completedJustBeforeTheTimeout.Should().BeFalse();

        [Test]
        public void It_returns_an_uncertain_loss() =>
            _outcome.Should().Be(new TargetRefreshOutcome.LeaseLost(TargetLeaseLossReason.RenewalUncertain));

        [Test]
        public void It_releases_without_reading() =>
            _harness.Calls.Should().Equal("Acquire", "Reclaim", "Renew", "Release");
    }

    [TestFixture]
    public class Given_a_long_read_while_the_lease_stays_renewed
    {
        private RefresherHarness _harness = null!;
        private int _renewalsJustBeforeTheFirstTick;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            TaskCompletionSource<ReadResult> read = new();
            _harness.Read = _ => read.Task;

            Task<TargetRefreshOutcome> run = _harness.RunAsync();
            _harness.Time.Advance(_harness.Settings.RenewalInterval - TimeSpan.FromTicks(1));
            _renewalsJustBeforeTheFirstTick = _harness.Renewals;
            _harness.Time.Advance(TimeSpan.FromTicks(1));
            await _harness.RenewalCalled(2).WaitAsync(_wait);
            _harness.Time.Advance(_harness.Settings.RenewalInterval);
            await _harness.RenewalCalled(3).WaitAsync(_wait);

            read.SetResult(RefresherHarness.SuccessfulRead(_harness.ReadItems));
            _outcome = await run.WaitAsync(_wait);
        }

        [Test]
        public void It_renews_only_once_each_interval() => _renewalsJustBeforeTheFirstTick.Should().Be(1);

        [Test]
        public void It_publishes_after_the_loop_renewals() =>
            _harness
                .Calls.Should()
                .Equal(
                    "Acquire",
                    "Reclaim",
                    "Renew",
                    "Read",
                    "Renew",
                    "Renew",
                    "Renew",
                    "Stage",
                    "Fence",
                    "Activate",
                    "Prune"
                );

        [Test]
        public void It_returns_refreshed() =>
            _outcome.Should().Be(new TargetRefreshOutcome.Refreshed(RefresherHarness.Generation, 2));

        [Test]
        public void It_renews_with_no_caller_token_in_the_loop() =>
            _harness.RenewTokens.Should().AllSatisfy(token => token.Should().Be(CancellationToken.None));
    }

    [TestFixture(SnapshotLeaseLossReason.Taken, TargetLeaseLossReason.Taken)]
    [TestFixture(SnapshotLeaseLossReason.Expired, TargetLeaseLossReason.Expired)]
    public class Given_a_renewal_that_finds_the_lease_lost_during_the_read(
        SnapshotLeaseLossReason lost,
        TargetLeaseLossReason expected
    )
    {
        private RefresherHarness _harness = null!;
        private CancellationTokenSource _caller = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Renew = renewal =>
                renewal == 1
                    ? RefresherHarness.Result(new SnapshotTargetResult.Success(1))
                    : RefresherHarness.Result(new SnapshotTargetResult.LeaseLost(lost));
            _harness.Read = RefresherHarness.UntilCancelledAsync;
            _caller = new CancellationTokenSource();

            Task<TargetRefreshOutcome> run = _harness.RunAsync(_caller.Token);
            _harness.Time.Advance(_harness.Settings.RenewalInterval);
            _outcome = await run.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_returns_the_loss_without_propagating() =>
            _outcome.Should().Be(new TargetRefreshOutcome.LeaseLost(expected));

        [Test]
        public void It_cancels_the_read_but_not_the_caller()
        {
            _harness.ReadToken.IsCancellationRequested.Should().BeTrue();
            _caller.IsCancellationRequested.Should().BeFalse();
        }

        [Test]
        public void It_releases_without_staging() =>
            _harness.Calls.Should().Equal("Acquire", "Reclaim", "Renew", "Read", "Renew", "Release");

        [Test]
        public void It_logs_the_loss_during_the_read()
        {
            LogEntry entry = _harness.Logger.Entries.Should().ContainSingle().Subject;
            entry.EventId.Should().Be(EducationOrganizationTargetRefresher.TargetLeaseLost);
            entry.Field("Operation").Should().Be("Read");
            entry.Field("Reason").Should().Be(expected.ToString());
        }
    }

    [TestFixture]
    public class Given_a_renewal_that_finds_the_data_store_removed_during_the_read
    {
        private RefresherHarness _harness = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Renew = renewal =>
                renewal == 1
                    ? RefresherHarness.Result(new SnapshotTargetResult.Success(1))
                    : RefresherHarness.Result(new SnapshotTargetResult.DataStoreRemoved());
            _harness.Read = RefresherHarness.UntilCancelledAsync;

            Task<TargetRefreshOutcome> run = _harness.RunAsync();
            _harness.Time.Advance(_harness.Settings.RenewalInterval);
            _outcome = await run.WaitAsync(_wait);
        }

        [Test]
        public void It_returns_removed() => _outcome.Should().Be(new TargetRefreshOutcome.Removed());

        [Test]
        public void It_releases_without_staging() =>
            _harness.Calls.Should().Equal("Acquire", "Reclaim", "Renew", "Read", "Renew", "Release");
    }

    [TestFixture]
    public class Given_a_renewal_that_times_out_during_the_read
    {
        private RefresherHarness _harness = null!;
        private bool _completedJustBeforeTheTimeout;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Renew = renewal =>
                renewal == 1
                    ? RefresherHarness.Result(new SnapshotTargetResult.Success(1))
                    : new TaskCompletionSource<SnapshotTargetResult>().Task;
            _harness.Read = RefresherHarness.UntilCancelledAsync;

            Task<TargetRefreshOutcome> run = _harness.RunAsync();
            int timers = _harness.Time.TimersCreated;
            _harness.Time.Advance(_harness.Settings.RenewalInterval);
            await _harness.RenewalCalled(2).WaitAsync(_wait);
            // The renewal's timeout timer exists once the loop awaits it.
            await _harness.Time.WaitForTimersCreatedAsync(timers + 1).WaitAsync(_wait);

            _harness.Time.Advance(_harness.Settings.RenewalTimeout - TimeSpan.FromTicks(1));
            _completedJustBeforeTheTimeout = run.IsCompleted;
            _harness.Time.Advance(TimeSpan.FromTicks(1));
            _outcome = await run.WaitAsync(_wait);
        }

        [Test]
        public void It_waits_for_half_the_renewal_interval() =>
            _completedJustBeforeTheTimeout.Should().BeFalse();

        [Test]
        public void It_returns_an_uncertain_loss() =>
            _outcome.Should().Be(new TargetRefreshOutcome.LeaseLost(TargetLeaseLossReason.RenewalUncertain));

        [Test]
        public void It_releases_without_staging() =>
            _harness.Calls.Should().Equal("Acquire", "Reclaim", "Renew", "Read", "Renew", "Release");

        [Test]
        public void It_logs_an_uncertain_renewal() =>
            _harness.Logger.Entries.Single().Field("Reason").Should().Be("RenewalUncertain");
    }

    [TestFixture]
    public class Given_the_caller_cancels_during_the_read
    {
        private RefresherHarness _harness = null!;
        private Exception? _exception;
        private int _renewalsAfterLaterIntervals;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Read = RefresherHarness.UntilCancelledAsync;
            using CancellationTokenSource caller = new();

            Task<TargetRefreshOutcome> run = _harness.RunAsync(caller.Token);
            await caller.CancelAsync();
            try
            {
                await run.WaitAsync(_wait);
            }
            catch (Exception exception)
            {
                _exception = exception;
            }

            _harness.Time.Advance(_harness.Settings.RenewalInterval * 3);
            _renewalsAfterLaterIntervals = _harness.Renewals;
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _exception.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_releases_without_staging() =>
            _harness.Calls.Should().Equal("Acquire", "Reclaim", "Renew", "Read", "Release");

        [Test]
        public void It_releases_with_no_caller_token() =>
            _harness.ReleaseTokens.Should().Equal(CancellationToken.None);

        [Test]
        public void It_stops_the_renewal_loop() => _renewalsAfterLaterIntervals.Should().Be(1);

        [Test]
        public void It_logs_no_lease_loss() => _harness.Logger.Entries.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_read_returns_while_a_renewal_is_in_flight
    {
        private RefresherHarness _harness = null!;
        private string[] _callsWhileTheRenewalWasInFlight = null!;
        private TargetRefreshOutcome _outcome = null!;
        private int _renewalsAfterLaterIntervals;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            TaskCompletionSource<SnapshotTargetResult> loopRenewal = new();
            _harness.Renew = renewal =>
                renewal == 2
                    ? loopRenewal.Task
                    : RefresherHarness.Result(new SnapshotTargetResult.Success(1));
            TaskCompletionSource<ReadResult> read = new();
            _harness.Read = _ => read.Task;

            Task<TargetRefreshOutcome> run = _harness.RunAsync();
            _harness.Time.Advance(_harness.Settings.RenewalInterval);
            await _harness.RenewalCalled(2).WaitAsync(_wait);

            read.SetResult(RefresherHarness.SuccessfulRead(_harness.ReadItems));
            _callsWhileTheRenewalWasInFlight = [.. _harness.Calls];

            _harness.Record("LoopRenewalCompleted");
            loopRenewal.SetResult(new SnapshotTargetResult.Success(1));
            _outcome = await run.WaitAsync(_wait);

            _harness.Time.Advance(_harness.Settings.RenewalInterval * 3);
            _renewalsAfterLaterIntervals = _harness.Renewals;
        }

        [Test]
        public void It_does_not_stage_while_the_renewal_is_in_flight() =>
            _callsWhileTheRenewalWasInFlight.Should().Equal("Acquire", "Reclaim", "Renew", "Read", "Renew");

        [Test]
        public void It_awaits_the_loop_before_the_pre_stage_renewal() =>
            _harness
                .Calls.Should()
                .Equal(
                    "Acquire",
                    "Reclaim",
                    "Renew",
                    "Read",
                    "Renew",
                    "LoopRenewalCompleted",
                    "Renew",
                    "Stage",
                    "Fence",
                    "Activate",
                    "Prune"
                );

        [Test]
        public void It_returns_refreshed() =>
            _outcome.Should().Be(new TargetRefreshOutcome.Refreshed(RefresherHarness.Generation, 2));

        [Test]
        public void It_renews_no_more_after_the_read() => _renewalsAfterLaterIntervals.Should().Be(3);
    }

    [TestFixture(EducationOrganizationProjectionFailureCode.TargetSchemaIncompatible)]
    [TestFixture(EducationOrganizationProjectionFailureCode.ServiceUnavailable)]
    public class Given_a_read_that_fails(EducationOrganizationProjectionFailureCode code)
    {
        private RefresherHarness _harness = null!;
        private EducationOrganizationProjectionFailure _failure = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _failure = EducationOrganizationProjectionFailure.Create(
                code,
                EducationOrganizationProjectionStage.Page,
                httpStatus: 409,
                problemType: "urn:ed-fi:api:conflict",
                correlationId: "corr-1",
                pagesRead: 2
            );
            _harness.Read = _ => Task.FromResult<ReadResult>(new ReadResult.Failure(_failure));
            _outcome = await _harness.RunAsync();
        }

        [Test]
        public void It_returns_the_failure() =>
            _outcome.Should().Be(new TargetRefreshOutcome.Failed(_failure));

        [Test]
        public void It_releases_without_staging() =>
            _harness.Calls.Should().Equal("Acquire", "Reclaim", "Renew", "Read", "Release");

        [Test]
        public void It_logs_the_classified_failure()
        {
            LogEntry entry = _harness.Logger.Entries.Should().ContainSingle().Subject;
            entry.EventId.Should().Be(EducationOrganizationTargetRefresher.TargetRefreshFailed);
            entry.Field("Category").Should().Be(_failure.Category.ToString());
            entry.Field("Code").Should().Be(code.ToString());
            entry.Field("Stage").Should().Be("Page");
            entry.Field("HttpStatus").Should().Be(409);
            entry.Field("ProblemType").Should().Be("urn:ed-fi:api:conflict");
            entry.Field("CorrelationId").Should().Be("corr-1");
            entry.Field("PagesRead").Should().Be(2);
        }
    }

    public enum AcquisitionResult
    {
        Busy,
        DataStoreNotFound,
        DbFailure,
    }

    [TestFixture(AcquisitionResult.Busy)]
    [TestFixture(AcquisitionResult.DataStoreNotFound)]
    [TestFixture(AcquisitionResult.DbFailure)]
    public class Given_an_acquisition_that_does_not_acquire(AcquisitionResult result)
    {
        private RefresherHarness _harness = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Acquire = _ =>
                Task.FromResult<SnapshotAcquireResult>(
                    result switch
                    {
                        AcquisitionResult.Busy => new SnapshotAcquireResult.Busy(),
                        AcquisitionResult.DataStoreNotFound => new SnapshotAcquireResult.DataStoreNotFound(),
                        _ => new SnapshotAcquireResult.DbFailure(RefresherHarness.Diagnostic),
                    }
                );
            _outcome = await _harness.RunAsync();
        }

        [Test]
        public void It_returns_the_matching_outcome() =>
            _outcome
                .Should()
                .Be(
                    result switch
                    {
                        AcquisitionResult.Busy => new TargetRefreshOutcome.Busy(),
                        AcquisitionResult.DataStoreNotFound => new TargetRefreshOutcome.Removed(),
                        _ => new TargetRefreshOutcome.DbFailure(RefresherHarness.Diagnostic),
                    }
                );

        [Test]
        public void It_does_nothing_more_and_has_nothing_to_release() =>
            _harness.Calls.Should().Equal("Acquire");

        [Test]
        public void It_logs_the_outcome() =>
            _harness
                .Logger.Entries.Single()
                .EventId.Should()
                .Be(
                    result switch
                    {
                        AcquisitionResult.Busy => EducationOrganizationTargetRefresher.TargetBusy,
                        AcquisitionResult.DataStoreNotFound =>
                            EducationOrganizationTargetRefresher.TargetRemoved,
                        _ => EducationOrganizationTargetRefresher.TargetRefreshFailed,
                    }
                );
    }

    public enum StopPoint
    {
        ReclaimLost,
        ReclaimRemoved,
        ReclaimDbFailure,
        PreStageLost,
        StageLost,
        StageDbFailure,
        ActivationLost,
        ActivationRemoved,
        ActivationStoreFailure,
    }

    [TestFixture(StopPoint.ReclaimLost)]
    [TestFixture(StopPoint.ReclaimRemoved)]
    [TestFixture(StopPoint.ReclaimDbFailure)]
    [TestFixture(StopPoint.PreStageLost)]
    [TestFixture(StopPoint.StageLost)]
    [TestFixture(StopPoint.StageDbFailure)]
    [TestFixture(StopPoint.ActivationLost)]
    [TestFixture(StopPoint.ActivationRemoved)]
    [TestFixture(StopPoint.ActivationStoreFailure)]
    public class Given_a_step_after_acquisition_that_stops_the_refresh(StopPoint stop)
    {
        private static readonly SnapshotTargetResult _taken = new SnapshotTargetResult.LeaseLost(
            SnapshotLeaseLossReason.Taken
        );
        private static readonly SnapshotTargetResult _expired = new SnapshotTargetResult.LeaseLost(
            SnapshotLeaseLossReason.Expired
        );
        private static readonly SnapshotTargetResult _removed = new SnapshotTargetResult.DataStoreRemoved();
        private static readonly SnapshotTargetResult _dbFailure = new SnapshotTargetResult.DbFailure(
            RefresherHarness.Diagnostic
        );

        private RefresherHarness _harness = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            switch (stop)
            {
                case StopPoint.ReclaimLost:
                    _harness.Reclaim = _ => RefresherHarness.Result(_taken);
                    break;
                case StopPoint.ReclaimRemoved:
                    _harness.Reclaim = _ => RefresherHarness.Result(_removed);
                    break;
                case StopPoint.ReclaimDbFailure:
                    _harness.Reclaim = _ => RefresherHarness.Result(_dbFailure);
                    break;
                case StopPoint.PreStageLost:
                    _harness.Renew = renewal =>
                        RefresherHarness.Result(
                            renewal == 1 ? new SnapshotTargetResult.Success(1) : _expired
                        );
                    break;
                case StopPoint.StageLost:
                    _harness.Stage = () => RefresherHarness.Result(_taken);
                    break;
                case StopPoint.StageDbFailure:
                    _harness.Stage = () => RefresherHarness.Result(_dbFailure);
                    break;
                case StopPoint.ActivationLost:
                    _harness.Activate = () => RefresherHarness.Result(_expired);
                    break;
                case StopPoint.ActivationRemoved:
                    _harness.Activate = () => RefresherHarness.Result(_removed);
                    break;
                case StopPoint.ActivationStoreFailure:
                    _harness.Activate = () => throw new SnapshotStoreException(RefresherHarness.Diagnostic);
                    break;
            }
            _outcome = await _harness.RunAsync();
        }

        [Test]
        public void It_returns_the_outcome_of_the_stopping_step() =>
            _outcome
                .Should()
                .Be(
                    stop switch
                    {
                        StopPoint.ReclaimLost or StopPoint.StageLost => new TargetRefreshOutcome.LeaseLost(
                            TargetLeaseLossReason.Taken
                        ),
                        StopPoint.PreStageLost or StopPoint.ActivationLost =>
                            new TargetRefreshOutcome.LeaseLost(TargetLeaseLossReason.Expired),
                        StopPoint.ReclaimRemoved or StopPoint.ActivationRemoved =>
                            new TargetRefreshOutcome.Removed(),
                        _ => new TargetRefreshOutcome.DbFailure(RefresherHarness.Diagnostic),
                    }
                );

        [Test]
        public void It_releases_after_the_stopping_step_without_pruning() =>
            _harness
                .Calls.Should()
                .Equal(
                    stop switch
                    {
                        StopPoint.ReclaimLost or StopPoint.ReclaimRemoved or StopPoint.ReclaimDbFailure =>
                            new[] { "Acquire", "Reclaim", "Release" },
                        StopPoint.PreStageLost => new[]
                        {
                            "Acquire",
                            "Reclaim",
                            "Renew",
                            "Read",
                            "Renew",
                            "Release",
                        },
                        StopPoint.StageLost or StopPoint.StageDbFailure => new[]
                        {
                            "Acquire",
                            "Reclaim",
                            "Renew",
                            "Read",
                            "Renew",
                            "Stage",
                            "Release",
                        },
                        _ => new[]
                        {
                            "Acquire",
                            "Reclaim",
                            "Renew",
                            "Read",
                            "Renew",
                            "Stage",
                            "Fence",
                            "Activate",
                            "Release",
                        },
                    }
                );

        [Test]
        public void It_logs_the_stopping_operation() =>
            _harness
                .Logger.Entries.Single()
                .Field("Operation")
                .Should()
                .Be(
                    stop switch
                    {
                        StopPoint.ReclaimLost or StopPoint.ReclaimRemoved or StopPoint.ReclaimDbFailure =>
                            "Reclaim",
                        StopPoint.PreStageLost => "PreStage",
                        StopPoint.StageLost or StopPoint.StageDbFailure => "Stage",
                        _ => "Activate",
                    }
                );
    }

    public enum FenceRefusal
    {
        LeaseLost,
        Unavailable,
    }

    [TestFixture(FenceRefusal.LeaseLost)]
    [TestFixture(FenceRefusal.Unavailable)]
    public class Given_a_fence_that_refuses_the_activation(FenceRefusal refusal)
    {
        private RefresherHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Fence.Refusal =
                refusal == FenceRefusal.LeaseLost
                    ? new JobLeaseLostException()
                    : new JobFenceUnavailableException();
            try
            {
                await _harness.RunAsync();
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_propagates_the_refusal() => _exception.Should().BeSameAs(_harness.Fence.Refusal);

        [Test]
        public void It_releases_without_activating() =>
            _harness
                .Calls.Should()
                .Equal("Acquire", "Reclaim", "Renew", "Read", "Renew", "Stage", "Fence", "Release");
    }

    [TestFixture]
    public class Given_the_caller_cancels_during_reclaim
    {
        private RefresherHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            using CancellationTokenSource caller = new();
            _harness.Reclaim = token =>
            {
                caller.Cancel();
                return Task.FromCanceled<SnapshotTargetResult>(token);
            };
            try
            {
                await _harness.RunAsync(caller.Token);
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _exception.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_releases_with_no_caller_token()
        {
            _harness.Calls.Should().Equal("Acquire", "Reclaim", "Release");
            _harness.ReleaseTokens.Should().Equal(CancellationToken.None);
        }
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_a_prune_that_fails(bool throws)
    {
        private RefresherHarness _harness = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Prune = throws
                ? () => throw new InvalidOperationException("prune")
                : () =>
                    RefresherHarness.Result(new SnapshotTargetResult.DbFailure(RefresherHarness.Diagnostic));
            _outcome = await _harness.RunAsync();
        }

        [Test]
        public void It_still_returns_refreshed() =>
            _outcome.Should().Be(new TargetRefreshOutcome.Refreshed(RefresherHarness.Generation, 2));

        [Test]
        public void It_does_not_release_the_published_lease() =>
            _harness.Calls.Should().NotContain("Release");

        [Test]
        public void It_logs_the_prune_failure_after_the_refresh()
        {
            _harness
                .Logger.Entries.Select(entry => entry.EventId)
                .Should()
                .Equal(
                    EducationOrganizationTargetRefresher.TargetRefreshed,
                    EducationOrganizationTargetRefresher.SnapshotPruneFailed
                );
            LogEntry entry = _harness.Logger.Entries.Last();
            entry.EventId.Name.Should().Be("EducationOrganizationSnapshotPruneFailed");
            entry
                .Field("ExceptionTypeChain")
                .Should()
                .Be(
                    throws
                        ? typeof(InvalidOperationException).FullName
                        : RefresherHarness.Diagnostic.ExceptionTypeChain
                );
            entry
                .Field("FailedOperation")
                .Should()
                .Be(throws ? "Prune" : RefresherHarness.Diagnostic.Operation);
        }
    }

    [TestFixture]
    public class Given_a_release_that_does_not_finish
    {
        private RefresherHarness _harness = null!;
        private bool _completedJustBeforeTheBound;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Reclaim = _ =>
                RefresherHarness.Result(new SnapshotTargetResult.LeaseLost(SnapshotLeaseLossReason.Taken));
            _harness.Release = () => new TaskCompletionSource<SnapshotTargetResult>().Task;

            Task<TargetRefreshOutcome> run = _harness.RunAsync();
            _harness.Time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
            _completedJustBeforeTheBound = run.IsCompleted;
            _harness.Time.Advance(TimeSpan.FromTicks(1));
            _outcome = await run.WaitAsync(_wait);
        }

        [Test]
        public void It_waits_at_most_five_seconds() => _completedJustBeforeTheBound.Should().BeFalse();

        [Test]
        public void It_keeps_the_outcome() =>
            _outcome.Should().Be(new TargetRefreshOutcome.LeaseLost(TargetLeaseLossReason.Taken));

        [Test]
        public void It_logs_the_release_timeout()
        {
            LogEntry entry = _harness.Logger.Entries.Last();
            entry.EventId.Should().Be(EducationOrganizationTargetRefresher.LeaseReleaseFailed);
            entry.EventId.Name.Should().Be("EducationOrganizationLeaseReleaseFailed");
            entry.Field("ExceptionTypeChain").Should().Be(typeof(TimeoutException).FullName);
            entry.Field("FailedOperation").Should().Be("Release");
        }
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_a_release_that_fails(bool throws)
    {
        private RefresherHarness _harness = null!;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Stage = () =>
                RefresherHarness.Result(new SnapshotTargetResult.LeaseLost(SnapshotLeaseLossReason.Expired));
            _harness.Release = throws
                ? () => throw new InvalidOperationException("release")
                : () =>
                    RefresherHarness.Result(new SnapshotTargetResult.DbFailure(RefresherHarness.Diagnostic));
            _outcome = await _harness.RunAsync();
        }

        [Test]
        public void It_keeps_the_outcome() =>
            _outcome.Should().Be(new TargetRefreshOutcome.LeaseLost(TargetLeaseLossReason.Expired));

        [Test]
        public void It_logs_the_release_failure() =>
            _harness
                .Logger.Entries.Select(entry => entry.EventId)
                .Should()
                .Equal(
                    EducationOrganizationTargetRefresher.TargetLeaseLost,
                    EducationOrganizationTargetRefresher.LeaseReleaseFailed
                );
    }

    [TestFixture]
    public class Given_test_seams
    {
        private RefresherHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            _harness.Refresher.AfterAcquire = (_, _) => Recorded("AfterAcquire");
            _harness.Refresher.AfterReclaim = (_, _) => Recorded("AfterReclaim");
            _harness.Refresher.AfterAdmission = (_, _) => Recorded("AfterAdmission");
            await _harness.RunAsync();
        }

        private Task Recorded(string name)
        {
            _harness.Record(name);
            return Task.CompletedTask;
        }

        [Test]
        public void It_runs_each_seam_at_its_point() =>
            _harness
                .Calls.Should()
                .Equal(
                    "Acquire",
                    "AfterAcquire",
                    "Reclaim",
                    "AfterReclaim",
                    "Renew",
                    "AfterAdmission",
                    "Read",
                    "Renew",
                    "Stage",
                    "Fence",
                    "Activate",
                    "Prune"
                );
    }

    [TestFixture]
    public class Given_an_execution_paused_after_admission
    {
        private RefresherHarness _harness = null!;
        private int _renewalsWhilePaused;
        private TargetRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness();
            TaskCompletionSource paused = new();
            TaskCompletionSource resume = new();
            _harness.Refresher.AfterAdmission = async (_, _) =>
            {
                paused.SetResult();
                await resume.Task;
            };
            _harness.Renew = renewal =>
                RefresherHarness.Result(
                    renewal == 1
                        ? new SnapshotTargetResult.Success(1)
                        : new SnapshotTargetResult.LeaseLost(SnapshotLeaseLossReason.Taken)
                );
            _harness.Read = RefresherHarness.UntilCancelledAsync;

            Task<TargetRefreshOutcome> run = _harness.RunAsync();
            await paused.Task.WaitAsync(_wait);
            _harness.Time.Advance(_harness.Settings.RenewalInterval * 2);
            _renewalsWhilePaused = _harness.Renewals;

            resume.SetResult();
            await _harness.ReadStarted.Task.WaitAsync(_wait);
            _harness.Time.Advance(_harness.Settings.RenewalInterval);
            _outcome = await run.WaitAsync(_wait);
        }

        [Test]
        public void It_starts_renewing_only_once_it_reads() => _renewalsWhilePaused.Should().Be(1);

        [Test]
        public void It_cancels_the_late_read_at_its_next_renewal() =>
            _outcome.Should().Be(new TargetRefreshOutcome.LeaseLost(TargetLeaseLossReason.Taken));

        [Test]
        public void It_never_stages() => _harness.Calls.Should().NotContain("Stage");
    }

    public enum SentinelPath
    {
        Refreshed,
        ReadFailed,
        LeaseLostDuringRead,
        PruneThrows,
    }

    [TestFixture(SentinelPath.Refreshed)]
    [TestFixture(SentinelPath.ReadFailed)]
    [TestFixture(SentinelPath.LeaseLostDuringRead)]
    [TestFixture(SentinelPath.PruneThrows)]
    public class Given_sentinel_values_in_contexts_items_and_exceptions(SentinelPath path)
    {
        private const string Sentinel = "SENTINEL";
        private RefresherHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RefresherHarness
            {
                Target = new EducationOrganizationRefreshTarget(
                    RefresherHarness.DataStoreId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [$"{Sentinel}-context-key"] = $"{Sentinel}-context-value",
                    }
                ),
                ReadItems =
                [
                    new EducationOrganizationProjectionItem(
                        255901,
                        $"{Sentinel}-name",
                        $"{Sentinel}-short-name",
                        $"{Sentinel}-discriminator",
                        null
                    ),
                ],
            };
            switch (path)
            {
                case SentinelPath.ReadFailed:
                    _harness.Read = _ =>
                        Task.FromResult<ReadResult>(
                            new ReadResult.Failure(
                                EducationOrganizationProjectionFailure.Create(
                                    EducationOrganizationProjectionFailureCode.DataInvalid,
                                    EducationOrganizationProjectionStage.Page
                                )
                            )
                        );
                    break;
                case SentinelPath.LeaseLostDuringRead:
                    _harness.Renew = renewal =>
                        renewal == 1
                            ? RefresherHarness.Result(new SnapshotTargetResult.Success(1))
                            : Task.FromException<SnapshotTargetResult>(
                                new InvalidOperationException(Sentinel)
                            );
                    _harness.Read = RefresherHarness.UntilCancelledAsync;
                    break;
                case SentinelPath.PruneThrows:
                    _harness.Prune = () => throw new InvalidOperationException(Sentinel);
                    break;
            }

            Task<TargetRefreshOutcome> run = _harness.RunAsync();
            if (path == SentinelPath.LeaseLostDuringRead)
            {
                _harness.Time.Advance(_harness.Settings.RenewalInterval);
            }
            await run.WaitAsync(_wait);
        }

        [Test]
        public void It_logs_something() => _harness.Logger.Entries.Should().NotBeEmpty();

        [Test]
        public void It_logs_no_context_item_exception_message_or_execution_token()
        {
            foreach (LogEntry entry in _harness.Logger.Entries)
            {
                IEnumerable<string> texts = entry
                    .Fields.Select(field => Convert.ToString(field.Value) ?? string.Empty)
                    .Append(entry.Message);
                texts
                    .Should()
                    .AllSatisfy(text =>
                    {
                        text.Should().NotContain(Sentinel);
                        text.Should().NotContain(_harness.Owner);
                    });
            }
        }
    }
}

/// <summary>
/// A <see cref="FakeTimeProvider"/> that counts the timers created, so a test can advance time only once the timer it
/// targets exists.
/// </summary>
internal sealed class TimerCountingTimeProvider()
    : FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero))
{
    private readonly object _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private int _created;

    public int TimersCreated
    {
        get
        {
            lock (_gate)
            {
                return _created;
            }
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        lock (_gate)
        {
            _created++;
            foreach (
                (int count, TaskCompletionSource signal) in _waiters.Where(waiter => waiter.Count <= _created)
            )
            {
                signal.TrySetResult();
            }
            _waiters.RemoveAll(waiter => waiter.Count <= _created);
        }
        return timer;
    }

    public Task WaitForTimersCreatedAsync(int count)
    {
        lock (_gate)
        {
            if (_created >= count)
            {
                return Task.CompletedTask;
            }
            TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
            return signal.Task;
        }
    }
}

/// <summary>A fence that records its use and runs the work in a fake transaction, unless told to refuse.</summary>
internal sealed class RecordingRefreshFence(RefresherHarness harness) : IJobFence
{
    public DbTransaction Transaction { get; } = A.Fake<DbTransaction>();

    public Exception? Refusal { get; set; }

    public async Task ExecuteAsync(
        Func<DbTransaction, CancellationToken, Task> work,
        CancellationToken cancellationToken
    )
    {
        harness.Record("Fence");
        if (Refusal is not null)
        {
            throw Refusal;
        }
        await work(Transaction, cancellationToken);
    }
}

/// <summary>
/// An <see cref="EducationOrganizationTargetRefresher"/> over FakeItEasy fakes of the repository and reader, a
/// recording fence, a fake clock and a capturing logger. Every repository, reader and fence call is recorded in order.
/// By default every step succeeds.
/// </summary>
internal sealed class RefresherHarness
{
    public const int DataStoreId = 42;
    public const long Generation = 3;

    public static readonly JobFailureDiagnostic Diagnostic = new(
        "Npgsql.PostgresException",
        "55P03",
        "TestOperation"
    );

    private readonly ConcurrentDictionary<int, TaskCompletionSource> _renewalSignals = new();
    private readonly ConcurrentQueue<string> _calls = new();
    private int _renewals;

    public RefresherHarness()
    {
        IEducationOrganizationSnapshotRepository repository =
            A.Fake<IEducationOrganizationSnapshotRepository>();
        IEducationOrganizationProjectionReader reader = A.Fake<IEducationOrganizationProjectionReader>();
        Fence = new RecordingRefreshFence(this);
        // Read lazily, so a harness initialized with other items reads those.
        Read = _ => Task.FromResult(SuccessfulRead(ReadItems));

        A.CallTo(() => repository.AcquireAsync(A<int>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily(
                (int dataStoreId, string owner, CancellationToken _) =>
                {
                    Record("Acquire");
                    Owner = owner;
                    return Acquire(new EducationOrganizationSnapshotLease(dataStoreId, owner, Generation));
                }
            );
        A.CallTo(() =>
                repository.ReclaimAsync(A<EducationOrganizationSnapshotLease>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (EducationOrganizationSnapshotLease lease, CancellationToken token) =>
                {
                    Record("Reclaim");
                    LeasesUsed.Enqueue(lease);
                    return Reclaim(token);
                }
            );
        A.CallTo(() => repository.RenewAsync(A<EducationOrganizationSnapshotLease>._, A<CancellationToken>._))
            .ReturnsLazily(
                (EducationOrganizationSnapshotLease lease, CancellationToken token) =>
                {
                    Record("Renew");
                    LeasesUsed.Enqueue(lease);
                    RenewTokens.Enqueue(token);
                    int renewal = Interlocked.Increment(ref _renewals);
                    try
                    {
                        return Renew(renewal);
                    }
                    finally
                    {
                        _renewalSignals.GetOrAdd(renewal, _ => NewSignal()).TrySetResult();
                    }
                }
            );
        A.CallTo(() =>
                repository.StageAsync(
                    A<EducationOrganizationSnapshotLease>._,
                    A<IReadOnlyList<EducationOrganizationProjectionItem>>._,
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    EducationOrganizationSnapshotLease lease,
                    IReadOnlyList<EducationOrganizationProjectionItem> items,
                    string contractVersion,
                    CancellationToken _
                ) =>
                {
                    Record("Stage");
                    LeasesUsed.Enqueue(lease);
                    StagedItems = items;
                    StagedContractVersion = contractVersion;
                    return Stage();
                }
            );
        A.CallTo(() =>
                repository.ActivateAsync(
                    A<DbTransaction>._,
                    A<EducationOrganizationSnapshotLease>._,
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    DbTransaction transaction,
                    EducationOrganizationSnapshotLease lease,
                    string contractVersion,
                    CancellationToken _
                ) =>
                {
                    Record("Activate");
                    LeasesUsed.Enqueue(lease);
                    ActivatedTransaction = transaction;
                    ActivatedContractVersion = contractVersion;
                    return Activate();
                }
            );
        A.CallTo(() => repository.PruneAsync(A<int>._, A<CancellationToken>._))
            .ReturnsLazily(
                (int dataStoreId, CancellationToken _) =>
                {
                    Record("Prune");
                    PrunedDataStoreId = dataStoreId;
                    return Prune();
                }
            );
        A.CallTo(() =>
                repository.ReleaseAsync(A<EducationOrganizationSnapshotLease>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (EducationOrganizationSnapshotLease lease, CancellationToken token) =>
                {
                    Record("Release");
                    LeasesUsed.Enqueue(lease);
                    ReleaseTokens.Enqueue(token);
                    return Release();
                }
            );
        A.CallTo(() =>
                reader.ReadAllAsync(A<EducationOrganizationProjectionReadRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (EducationOrganizationProjectionReadRequest request, CancellationToken token) =>
                {
                    Record("Read");
                    ReadRequest = request;
                    ReadToken = token;
                    ReadStarted.TrySetResult();
                    return Read(token);
                }
            );

        Refresher = new EducationOrganizationTargetRefresher(
            repository,
            reader,
            Options.Create(Settings),
            Time,
            Logger
        );
    }

    public JobOptions Settings { get; } =
        new() { LeaseDuration = TimeSpan.FromMinutes(5), RenewalInterval = TimeSpan.FromMinutes(1) };

    public TimerCountingTimeProvider Time { get; } = new();

    public CapturingLogger<EducationOrganizationTargetRefresher> Logger { get; } = new();

    public RecordingRefreshFence Fence { get; }

    public EducationOrganizationTargetRefresher Refresher { get; }

    public TenantContext Tenant { get; init; } = new TenantContext.Multitenant(7, "Tenant_7");

    public EducationOrganizationRefreshTarget Target { get; init; } =
        new(DataStoreId, new Dictionary<string, string>(StringComparer.Ordinal) { ["schoolYear"] = "2025" });

    public IReadOnlyList<EducationOrganizationProjectionItem> ReadItems { get; init; } =
    [
        new(255901, "Grand Bend ISD", null, "edfi.LocalEducationAgency", null),
        new(255901001, "Grand Bend High School", "GBHS", "edfi.School", 255901),
    ];

    public Func<EducationOrganizationSnapshotLease, Task<SnapshotAcquireResult>> Acquire { get; set; } =
        lease => Task.FromResult<SnapshotAcquireResult>(new SnapshotAcquireResult.Acquired(lease));

    public Func<CancellationToken, Task<SnapshotTargetResult>> Reclaim { get; set; } =
        _ => Result(new SnapshotTargetResult.Success(0));

    /// <summary>The renewal response by its 1-based call number: 1 is admission.</summary>
    public Func<int, Task<SnapshotTargetResult>> Renew { get; set; } =
        _ => Result(new SnapshotTargetResult.Success(1));

    public Func<CancellationToken, Task<ReadResult>> Read { get; set; }

    public Func<Task<SnapshotTargetResult>> Stage { get; set; } =
        () => Result(new SnapshotTargetResult.Success(2));

    public Func<Task<SnapshotTargetResult>> Activate { get; set; } =
        () => Result(new SnapshotTargetResult.Success(1));

    public Func<Task<SnapshotTargetResult>> Prune { get; set; } =
        () => Result(new SnapshotTargetResult.Success(0));

    public Func<Task<SnapshotTargetResult>> Release { get; set; } =
        () => Result(new SnapshotTargetResult.Success(1));

    public IReadOnlyList<string> Calls => [.. _calls];

    public int Renewals => Volatile.Read(ref _renewals);

    public string Owner { get; private set; } = string.Empty;

    public ConcurrentQueue<EducationOrganizationSnapshotLease> LeasesUsed { get; } = new();

    public ConcurrentQueue<CancellationToken> RenewTokens { get; } = new();

    public ConcurrentQueue<CancellationToken> ReleaseTokens { get; } = new();

    public EducationOrganizationProjectionReadRequest? ReadRequest { get; private set; }

    public CancellationToken ReadToken { get; private set; }

    public TaskCompletionSource ReadStarted { get; } = NewSignal();

    public IReadOnlyList<EducationOrganizationProjectionItem>? StagedItems { get; private set; }

    public string? StagedContractVersion { get; private set; }

    public DbTransaction? ActivatedTransaction { get; private set; }

    public string? ActivatedContractVersion { get; private set; }

    public int? PrunedDataStoreId { get; private set; }

    public Task<TargetRefreshOutcome> RunAsync(CancellationToken cancellationToken = default) =>
        Refresher.RefreshAsync(
            new JobExecutionContext("job-11", Tenant, 1, 5, Fence),
            Target,
            cancellationToken
        );

    public void Record(string call) => _calls.Enqueue(call);

    /// <summary>Completes once the renewal with this 1-based call number has been requested.</summary>
    public Task RenewalCalled(int renewal) => _renewalSignals.GetOrAdd(renewal, _ => NewSignal()).Task;

    public static Task<SnapshotTargetResult> Result(SnapshotTargetResult result) => Task.FromResult(result);

    public static ReadResult SuccessfulRead(IReadOnlyList<EducationOrganizationProjectionItem> items) =>
        new ReadResult.Success(items, "1.0.0", 4, 0);

    /// <summary>A read that ends only when its token is cancelled, throwing as the reader contract requires.</summary>
    public static async Task<ReadResult> UntilCancelledAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Unreachable: an infinite delay ends only by cancellation.");
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

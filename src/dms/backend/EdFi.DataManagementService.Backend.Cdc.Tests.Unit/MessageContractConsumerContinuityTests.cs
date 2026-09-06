// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal static class MessageContractConsumerContinuityData
{
    public static MessageContractConsumerBootstrap Valid(
        Func<IReadOnlyList<MessageContractPartitionBounds>> observeBounds
    )
    {
        var consumer = new MessageContractConsumerBootstrap(
            MessageContractConsumerBootstrapData.Epoch,
            observeBounds
        );
        var scan = consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(consumer, scan, 10);
        consumer.CompleteCheckpoint(scan, 11);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(consumer, 1, 30);
        return consumer;
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-CONTINUITY-DURABILITY")]
public class Given_MessageContractConsumerContinuity_renewal_durability
{
    private MessageContractConsumerBootstrap _consumer = null!;
    private MessageContractConsumerScan _first = null!;
    private MessageContractConsumerScan _idle = null!;

    [SetUp]
    public void Setup()
    {
        _consumer = MessageContractConsumerContinuityData.Valid(() => [new(0, 10, 11), new(1, 30, 30)]);
        _consumer.AdvanceTime(TimeSpan.FromHours(20));
        _consumer.BeginRenewal(new Dictionary<int, long> { [0] = 12, [1] = 30 });
        _first = _consumer.StartPartitionScan(0);
        _idle = _consumer.StartPartitionScan(1);
        _consumer.Stage(_first, MessageContractConsumerBootstrapData.Record(0, 11, delete: true));
    }

    [Test]
    public void It_preserves_valid_state_during_ordinary_in_window_lag()
    {
        _consumer.IsValid.Should().BeTrue();
        _consumer.Documents.Should().ContainSingle();
        _consumer.RenewalInProgress.Should().BeTrue();
        _consumer.Attempt.Should().Be(1);
    }

    [Test]
    public void It_does_not_renew_the_proof_from_capture_or_delivery_alone()
    {
        _consumer.ProofCompletedAt.Should().Be(MessageContractConsumerBootstrapData.Epoch);
        _consumer.DurableNextOffsets[0].Should().Be(11);
        _consumer.Checkpoints[0].Should().Be(11);
    }

    [Test]
    public void It_cannot_checkpoint_an_unflushed_state_write()
    {
        Action checkpoint = () => _consumer.CompleteCheckpoint(_first, 12);
        checkpoint.Should().Throw<InvalidOperationException>();
        _consumer.RenewalInProgress.Should().BeTrue();
    }

    [Test]
    public void It_cannot_scan_past_an_unflushed_state_write()
    {
        Action scan = () => _consumer.CompleteScan(_first, 12);
        scan.Should().Throw<InvalidOperationException>();
        _consumer.RenewalInProgress.Should().BeTrue();
    }

    [Test]
    public void It_requires_checkpoint_persistence_after_application()
    {
        _consumer.CompleteApply(_first);
        CompleteIdle();
        _consumer.Documents.Should().BeEmpty();
        _consumer.ProofCompletedAt.Should().Be(MessageContractConsumerBootstrapData.Epoch);
        _consumer.RenewalInProgress.Should().BeTrue();
    }

    [Test]
    public void It_requires_every_partition_including_the_idle_partition()
    {
        _consumer.CompleteApply(_first);
        _consumer.CompleteCheckpoint(_first, 12);
        _consumer.RenewalInProgress.Should().BeTrue();
        _consumer.CompleteCheckpoint(_idle, 30); // Old durable position alone is not a new scan.
        _consumer.RenewalInProgress.Should().BeTrue();
        _consumer.CompleteScan(_idle, 30);
        _consumer.RenewalInProgress.Should().BeTrue();
        _consumer.CompleteCheckpoint(_idle, 30);
        _consumer.RenewalInProgress.Should().BeFalse();
    }

    [Test]
    public void It_requires_a_checkpoint_through_the_captured_exclusive_end()
    {
        _consumer.CompleteApply(_first);
        CompleteIdle();
        _consumer.CompleteCheckpoint(_first, 11);
        _consumer.RenewalInProgress.Should().BeTrue();
        _consumer.CompleteCheckpoint(_first, 12);
        _consumer.RenewalInProgress.Should().BeFalse();
    }

    [Test]
    public void It_starts_the_next_interval_only_when_the_entire_proof_is_durable()
    {
        _consumer.CompleteApply(_first);
        _consumer.CompleteCheckpoint(_first, 12);
        _consumer.AdvanceTime(TimeSpan.FromHours(3));
        CompleteIdle();
        _consumer.ProofCompletedAt.Should().Be(MessageContractConsumerBootstrapData.Epoch.AddHours(23));
        _consumer.RenewalDeadline.Should().Be(MessageContractConsumerBootstrapData.Epoch.AddHours(47));
        _consumer.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_does_not_extend_the_deadline_when_restarting_a_scan()
    {
        _consumer.AdvanceTime(TimeSpan.FromHours(3));
        _consumer.StartPartitionScan(0);
        _consumer.RenewalDeadline.Should().Be(MessageContractConsumerBootstrapData.Epoch.AddHours(24));
    }

    [Test]
    public void It_keeps_the_active_barriers_frozen()
    {
        Action recapture = () => _consumer.BeginRenewal(new Dictionary<int, long> { [0] = 99, [1] = 99 });
        recapture.Should().Throw<InvalidOperationException>();
        _consumer.Barriers.Values.Select(p => p.EndOffset).Should().Equal(12, 30);
        _consumer.RenewalDeadline.Should().Be(MessageContractConsumerBootstrapData.Epoch.AddHours(24));
    }

    private void CompleteIdle()
    {
        _consumer.CompleteScan(_idle, 30);
        _consumer.CompleteCheckpoint(_idle, 30);
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-CONTINUITY-IDLE")]
public class Given_MessageContractConsumerContinuity_idle_renewal
{
    private MessageContractConsumerBootstrap _consumer = null!;
    private MessageContractConsumerScan _oldScan = null!;

    [SetUp]
    public void Setup()
    {
        _consumer = MessageContractConsumerContinuityData.Valid(() => [new(0, 10, 11), new(1, 30, 30)]);
        _oldScan = _consumer.StartPartitionScan(0);
        _consumer.AdvanceTime(TimeSpan.FromHours(23));
    }

    [Test]
    public void It_renews_repeatedly_with_unchanged_offsets_without_waiting_for_records()
    {
        for (int interval = 1; interval <= 4; interval++)
        {
            BeginIdleRenewal();
            _consumer.RenewalInProgress.Should().BeTrue();
            MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 0, 11);
            MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
            _consumer
                .ProofCompletedAt.Should()
                .Be(MessageContractConsumerBootstrapData.Epoch.AddHours(23 * interval));
            _consumer.IsValid.Should().BeTrue();
            _consumer.RenewalInProgress.Should().BeFalse();
            _consumer.AdvanceTime(TimeSpan.FromHours(23));
        }
        _consumer.Attempt.Should().Be(1);
        _consumer.Documents.Should().ContainSingle();
        _consumer.Checkpoints.Values.Should().Equal(11, 30);
    }

    [Test]
    public void It_does_not_count_delayed_scan_and_checkpoint_callbacks_from_an_older_proof()
    {
        BeginIdleRenewal();
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.CompleteScan(_oldScan, 11);
        _consumer.CompleteCheckpoint(_oldScan, 11);
        _consumer.RenewalInProgress.Should().BeTrue();
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 0, 11);
        _consumer.RenewalInProgress.Should().BeFalse();
    }

    [Test]
    public void It_allows_an_older_pending_apply_to_finish_without_certifying_the_new_proof()
    {
        _consumer.Stage(_oldScan, MessageContractConsumerBootstrapData.Record(0, 11, delete: true));
        _consumer.BeginRenewal(new Dictionary<int, long> { [0] = 12, [1] = 30 });
        _consumer.CompleteApply(_oldScan);
        _consumer.CompleteCheckpoint(_oldScan, 12);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.Documents.Should().BeEmpty();
        _consumer.RenewalInProgress.Should().BeTrue();
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 0, 12);
        _consumer.RenewalInProgress.Should().BeFalse();
    }

    [Test]
    public void It_does_not_reuse_partition_completion_evidence_in_the_next_renewal()
    {
        BeginIdleRenewal();
        var previous = _consumer.StartPartitionScan(0);
        _consumer.CompleteScan(previous, 11);
        _consumer.CompleteCheckpoint(previous, 11);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.AdvanceTime(TimeSpan.FromHours(23));
        BeginIdleRenewal();
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.CompleteScan(previous, 11);
        _consumer.CompleteCheckpoint(previous, 11);
        _consumer.RenewalInProgress.Should().BeTrue();
        _consumer.ProofCompletedAt.Should().Be(MessageContractConsumerBootstrapData.Epoch.AddHours(23));
    }

    [Test]
    public void It_preserves_incremental_continuation_after_in_window_downtime()
    {
        _consumer.ObserveAssignment([1, 0]); // Same assignment, different observation order.
        var resumed = _consumer.StartPartitionScan(0);
        resumed.NextOffset.Should().Be(11);
        _consumer.Stage(resumed, MessageContractConsumerBootstrapData.Record(0, 11, delete: true));
        _consumer.CompleteApply(resumed);
        _consumer.CompleteCheckpoint(resumed, 12);
        _consumer.IsValid.Should().BeTrue();
        _consumer.Attempt.Should().Be(1);
        _consumer.Documents.Should().BeEmpty();
        _consumer.ProofCompletedAt.Should().Be(MessageContractConsumerBootstrapData.Epoch);
    }

    private void BeginIdleRenewal() =>
        _consumer.BeginRenewal(new Dictionary<int, long> { [0] = 11, [1] = 30 });
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-CONTINUITY-DEADLINE")]
public class Given_MessageContractConsumerContinuity_deadline
{
    private MessageContractConsumerBootstrap _consumer = null!;
    private MessageContractPartitionBounds[] _bounds = [];

    [SetUp]
    public void Setup()
    {
        _bounds = [new(0, 10, 11), new(1, 30, 30)];
        _consumer = MessageContractConsumerContinuityData.Valid(() => _bounds);
    }

    [TestCase(-1L)]
    [TestCase(0L)]
    public void It_allows_renewal_up_to_and_including_the_deadline(long ticks)
    {
        _consumer.AdvanceTime(TimeSpan.FromHours(23));
        _consumer.BeginRenewal(new Dictionary<int, long> { [0] = 11, [1] = 30 });
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 0, 11);
        _consumer.AdvanceTime(TimeSpan.FromHours(1) + TimeSpan.FromTicks(ticks));
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.IsValid.Should().BeTrue();
        _consumer.ProofCompletedAt.Should().Be(_consumer.Now);
        _consumer.Attempt.Should().Be(1);
    }

    [TestCase("no-renewal")]
    [TestCase("lagging")]
    [TestCase("pending-apply")]
    [TestCase("pending-checkpoint")]
    [TestCase("idle-partition-unproven")]
    public void It_discards_all_state_one_tick_after_an_unrenewed_deadline(string progress)
    {
        if (progress != "no-renewal")
        {
            _consumer.BeginRenewal(new Dictionary<int, long> { [0] = 12, [1] = 30 });
            var scan = _consumer.StartPartitionScan(0);
            if (progress != "lagging")
            {
                _consumer.Stage(scan, MessageContractConsumerBootstrapData.Record(0, 11));
                if (progress != "pending-apply")
                {
                    _consumer.CompleteApply(scan);
                }
                if (progress == "idle-partition-unproven")
                {
                    _consumer.CompleteCheckpoint(scan, 12);
                }
            }
            if (progress != "idle-partition-unproven")
            {
                MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
            }
        }
        _consumer.AdvanceTime(MessageContractConsumerBootstrap.Budget);
        _consumer.IsValid.Should().BeTrue();
        _bounds = [new(0, 50, 50), new(1, 90, 90)];
        _consumer.AdvanceTime(TimeSpan.FromTicks(1));
        _consumer.IsValid.Should().BeFalse();
        _consumer.Documents.Should().BeEmpty();
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.RenewalInProgress.Should().BeFalse();
        _consumer.Attempt.Should().Be(2);
        _consumer.StartPartitionScan(0).NextOffset.Should().Be(50);
        _consumer.StartPartitionScan(1).NextOffset.Should().Be(90);
    }

    [Test]
    public void It_cannot_silently_reuse_a_durable_checkpoint_after_long_downtime()
    {
        _consumer.AdvanceTime(TimeSpan.FromDays(8));
        _consumer.IsValid.Should().BeFalse();
        _consumer.Documents.Should().BeEmpty();
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.StartPartitionScan(0).NextOffset.Should().Be(10);
    }

    [Test]
    public void It_requires_another_new_proof_after_a_successful_renewal_interval()
    {
        _consumer.AdvanceTime(TimeSpan.FromHours(23));
        _consumer.BeginRenewal(new Dictionary<int, long> { [0] = 11, [1] = 30 });
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 0, 11);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.AdvanceTime(MessageContractConsumerBootstrap.Budget + TimeSpan.FromTicks(1));
        _consumer.IsValid.Should().BeFalse();
        _consumer.Documents.Should().BeEmpty();
    }

    [Test]
    public void It_measures_the_first_renewal_interval_from_durable_bootstrap_completion()
    {
        _consumer = new(MessageContractConsumerBootstrapData.Epoch, () => _bounds);
        var scan = _consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(_consumer, scan, 10);
        _consumer.CompleteCheckpoint(scan, 11);
        _consumer.AdvanceTime(TimeSpan.FromHours(12));
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.AdvanceTime(TimeSpan.FromHours(23));
        _consumer.IsValid.Should().BeTrue();
        _consumer.RenewalDeadline.Should().Be(MessageContractConsumerBootstrapData.Epoch.AddHours(36));
    }
}

public enum MessageContractContinuityFault
{
    MissingCheckpoint,
    CorruptCheckpoint,
    UncertainProgress,
    AddedPartition,
    RemovedPartition,
    ReplacedPartition,
    DuplicatePartition,
}

[TestFixture(MessageContractContinuityFault.MissingCheckpoint)]
[TestFixture(MessageContractContinuityFault.CorruptCheckpoint)]
[TestFixture(MessageContractContinuityFault.UncertainProgress)]
[TestFixture(MessageContractContinuityFault.AddedPartition)]
[TestFixture(MessageContractContinuityFault.RemovedPartition)]
[TestFixture(MessageContractContinuityFault.ReplacedPartition)]
[TestFixture(MessageContractContinuityFault.DuplicatePartition)]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-CONTINUITY-RECOVERY")]
public class Given_MessageContractConsumerContinuity_fault_recovery(MessageContractContinuityFault fault)
{
    private MessageContractConsumerBootstrap _consumer = null!;
    private MessageContractConsumerScan _oldScan = null!;
    private MessageContractPartitionBounds[] _bounds = [];
    private int _observations;
    private bool _failObservation;

    [SetUp]
    public void Setup()
    {
        _observations = 0;
        _failObservation = false;
        _bounds = [new(0, 10, 11), new(1, 30, 30)];
        _consumer = MessageContractConsumerContinuityData.Valid(() =>
        {
            _observations++;
            if (_failObservation)
            {
                throw new InvalidOperationException("Current consumer bounds unavailable.");
            }
            return _bounds;
        });
        _consumer.AdvanceTime(TimeSpan.FromHours(1));
        _consumer.BeginRenewal(new Dictionary<int, long> { [0] = 12, [1] = 30 });
        _oldScan = _consumer.StartPartitionScan(0);
        _consumer.Stage(_oldScan, MessageContractConsumerBootstrapData.Record(0, 11));
        _bounds = fault switch
        {
            MessageContractContinuityFault.AddedPartition => [new(0, 50, 50), new(1, 90, 90), new(2, 5, 5)],
            MessageContractContinuityFault.RemovedPartition => [new(0, 50, 50)],
            MessageContractContinuityFault.ReplacedPartition => [new(0, 50, 50), new(2, 90, 90)],
            _ => [new(0, 50, 50), new(1, 90, 90)],
        };
    }

    [Test]
    public void It_immediately_revokes_and_discards_the_complete_state_before_the_deadline()
    {
        InjectFault();
        _consumer.IsValid.Should().BeFalse();
        _consumer.State.Should().Be(MessageContractBootstrapState.AwaitingScan);
        _consumer.Documents.Should().BeEmpty();
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.RenewalInProgress.Should().BeFalse();
        _consumer.Attempt.Should().Be(2);
        _observations.Should().Be(2);
    }

    [Test]
    public void It_restarts_every_current_partition_at_fresh_earliest_offsets()
    {
        InjectFault();
        _consumer.Barriers.Values.Should().BeEquivalentTo(_bounds);
        foreach (var partition in _bounds)
        {
            _consumer
                .StartPartitionScan(partition.Partition)
                .NextOffset.Should()
                .Be(partition.EarliestOffset);
        }
    }

    [Test]
    public void It_removes_a_stale_document_even_when_its_tombstone_is_no_longer_retained()
    {
        _consumer.Documents.Should().ContainSingle();
        InjectFault();
        foreach (var partition in _bounds)
        {
            // Current retained log is empty: no tombstone can repair the old materialized state.
            MessageContractConsumerBootstrapData.ScanAndCheckpoint(
                _consumer,
                partition.Partition,
                partition.EndOffset
            );
        }
        _consumer.IsValid.Should().BeTrue();
        _consumer.Documents.Should().BeEmpty();
        _consumer.ProofCompletedAt.Should().Be(_consumer.Now);
    }

    [TestCase("apply")]
    [TestCase("checkpoint")]
    public void It_fences_delayed_persistence_from_the_discarded_attempt(string callback)
    {
        InjectFault();
        var current = _consumer.StartPartitionScan(0);
        _consumer.Stage(current, MessageContractConsumerBootstrapData.Record(0, 50));
        Action delayed =
            callback == "apply"
                ? () => _consumer.CompleteApply(_oldScan)
                : () => _consumer.CompleteCheckpoint(_oldScan, 12);
        delayed
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Consumer scan belongs to an inactive bootstrap attempt.");
        _consumer.Documents.Should().BeEmpty();
        _consumer.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_revokes_validity_even_when_fresh_bounds_cannot_be_observed()
    {
        _failObservation = true;
        Action invalidate = InjectFault;
        invalidate
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Current consumer bounds unavailable.");
        _consumer.Documents.Should().BeEmpty();
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.IsValid.Should().BeFalse();
        Action scan = () => _consumer.StartPartitionScan(0);
        scan.Should().Throw<InvalidOperationException>();
        _failObservation = false;
        _consumer.FailBootstrap();
        foreach (var partition in _bounds)
        {
            MessageContractConsumerBootstrapData.ScanAndCheckpoint(
                _consumer,
                partition.Partition,
                partition.EndOffset
            );
        }
        _consumer.IsValid.Should().BeTrue();
    }

    private void InjectFault()
    {
        switch (fault)
        {
            case MessageContractContinuityFault.MissingCheckpoint:
                _consumer.LoseCheckpoints();
                break;
            case MessageContractContinuityFault.CorruptCheckpoint:
                _consumer.CorruptCheckpoints();
                break;
            case MessageContractContinuityFault.UncertainProgress:
                _consumer.ReportUncertainProgress();
                break;
            case MessageContractContinuityFault.DuplicatePartition:
                _consumer.ObserveAssignment([0, 0]);
                break;
            default:
                _consumer.ObserveAssignment(_bounds.Select(p => p.Partition).ToArray());
                break;
        }
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-CONTINUITY-OBSERVATIONS")]
public class Given_MessageContractConsumerContinuity_uncertain_barrier_observations
{
    private MessageContractConsumerBootstrap _consumer = null!;

    [SetUp]
    public void Setup() =>
        _consumer = MessageContractConsumerContinuityData.Valid(() => [new(0, 10, 11), new(1, 30, 30)]);

    [TestCase("missing")]
    [TestCase("unexpected")]
    [TestCase("regressed")]
    public void It_invalidates_incomplete_mismatched_or_regressing_barrier_observations(string observation)
    {
        Dictionary<int, long> ends = observation switch
        {
            "missing" => new() { [0] = 11 },
            "unexpected" => new() { [0] = 11, [2] = 30 },
            _ => new() { [0] = 10, [1] = 30 },
        };
        Action capture = () => _consumer.BeginRenewal(ends);
        capture.Should().Throw<InvalidOperationException>();
        _consumer.IsValid.Should().BeFalse();
        _consumer.Documents.Should().BeEmpty();
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.StartPartitionScan(0).NextOffset.Should().Be(10);
    }

    [Test]
    public void It_cannot_renew_before_reconstruction_is_durable()
    {
        _consumer.ReportUncertainProgress();
        Action capture = () => _consumer.BeginRenewal(new Dictionary<int, long> { [0] = 11, [1] = 30 });
        capture.Should().Throw<InvalidOperationException>();
        _consumer.IsValid.Should().BeFalse();
    }
}

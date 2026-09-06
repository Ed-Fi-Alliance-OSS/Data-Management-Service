// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal static class MessageContractConsumerBootstrapData
{
    public static readonly DateTimeOffset Epoch = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

    public static MessageContractConsumerRecord Record(int partition, long offset, bool delete = false)
    {
        var fixture = MessageContractFixtureCatalog
            .LoadAll(TestContext.CurrentContext.TestDirectory)
            .First(f => f.ScenarioId == "MC-FIX-PG-ORDINARY-LINK-BEARING-STUDENT-SCHOOL-ASSOCIATION");
        return new(
            partition,
            offset,
            Encoding.UTF8.GetBytes(fixture.ExpectedEnvelope.GetProperty("documentUuid").GetString()!),
            delete
                ? ReadOnlyMemory<byte>.Empty
                : JsonSerializer.SerializeToUtf8Bytes(fixture.ExpectedEnvelope),
            delete
        );
    }

    public static void Apply(
        MessageContractConsumerBootstrap consumer,
        MessageContractConsumerScan scan,
        long offset
    )
    {
        consumer.Stage(scan, Record(scan.Partition, offset));
        consumer.CompleteApply(scan);
    }

    public static void ScanAndCheckpoint(
        MessageContractConsumerBootstrap consumer,
        int partition,
        long position
    )
    {
        var scan = consumer.StartPartitionScan(partition);
        consumer.CompleteScan(scan, position);
        consumer.CompleteCheckpoint(scan, position);
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-BOOTSTRAP-DURABILITY")]
public class Given_MessageContractConsumerBootstrap_durability
{
    private MessageContractConsumerBootstrap _consumer = null!;
    private MessageContractConsumerScan _first = null!;
    private MessageContractConsumerScan _second = null!;

    [SetUp]
    public void Setup()
    {
        _consumer = new(MessageContractConsumerBootstrapData.Epoch, () => [new(0, 10, 11), new(1, 30, 31)]);
        _first = _consumer.StartPartitionScan(0);
        _second = _consumer.StartPartitionScan(1);
        _consumer.Stage(_first, MessageContractConsumerBootstrapData.Record(0, 10));
        _consumer.Stage(_second, MessageContractConsumerBootstrapData.Record(1, 30, delete: true));
    }

    [Test]
    public void It_starts_each_partition_at_its_own_earliest_offset() =>
        new[] { _first.NextOffset, _second.NextOffset }.Should().Equal(10, 30);

    [Test]
    public void It_does_not_advertise_successful_reads_as_durable_state() =>
        _consumer.IsValid.Should().BeFalse();

    [Test]
    public void It_has_no_materialized_state_before_apply() => _consumer.Documents.Should().BeEmpty();

    [Test]
    public void It_cannot_checkpoint_a_pending_state_write()
    {
        Action checkpoint = () => _consumer.CompleteCheckpoint(_first, 11);
        checkpoint
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Consumer checkpoint must follow durable application.");
    }

    [Test]
    public void It_cannot_scan_past_an_unflushed_write()
    {
        Action scan = () => _consumer.CompleteScan(_first, 11);
        scan.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Consumer scan cannot pass an unfinished apply or regress.");
    }

    [Test]
    public void It_withholds_validity_when_another_partition_has_an_unflushed_write()
    {
        _consumer.CompleteApply(_first);
        _consumer.CompleteCheckpoint(_first, 11);
        _consumer.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_requires_checkpoints_even_after_all_state_writes_finish()
    {
        _consumer.CompleteApply(_first);
        _consumer.CompleteApply(_second);
        _consumer.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_requires_the_last_partition_checkpoint()
    {
        _consumer.CompleteApply(_first);
        _consumer.CompleteApply(_second);
        _consumer.CompleteCheckpoint(_first, 11);
        _consumer.CompleteCheckpoint(_second, 30);
        _consumer.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_becomes_valid_only_after_both_barriers_are_durable()
    {
        CompleteBootstrap();
        _consumer.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_includes_the_delete_in_the_durable_bootstrap_state()
    {
        CompleteBootstrap();
        _consumer.Documents.Should().BeEmpty();
    }

    [Test]
    public void It_continues_incrementally_from_durable_next_offsets()
    {
        CompleteBootstrap();
        var incremental = _consumer.StartPartitionScan(0);
        incremental.NextOffset.Should().Be(11);
        MessageContractConsumerBootstrapData.Apply(_consumer, incremental, 11);
        _consumer.CompleteCheckpoint(incremental, 12);
        _consumer.Checkpoints[0].Should().Be(12);
        _consumer.IsValid.Should().BeTrue();
    }

    private void CompleteBootstrap()
    {
        _consumer.CompleteApply(_first);
        _consumer.CompleteApply(_second);
        _consumer.CompleteCheckpoint(_first, 11);
        _consumer.CompleteCheckpoint(_second, 31);
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-BOOTSTRAP-OFFSETS")]
public class Given_MessageContractConsumerBootstrap_offset_boundaries
{
    private MessageContractConsumerBootstrap _consumer = null!;
    private MessageContractPartitionBounds[] _bounds = [];

    [SetUp]
    public void Setup()
    {
        _bounds = [new(0, 10, 20), new(1, 30, 30), new(2, 0, 0)];
        _consumer = new(MessageContractConsumerBootstrapData.Epoch, () => _bounds);
    }

    [Test]
    public void It_captures_exclusive_ends_without_advancing_progress()
    {
        _consumer.Barriers.Values.Select(p => p.EndOffset).Should().Equal(20, 30, 0);
        _consumer.DurableNextOffsets.Values.Should().Equal(10, 30, 0);
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_requires_every_assigned_partition_even_when_empty()
    {
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 0, 20);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.IsValid.Should().BeFalse();
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 2, 0);
        _consumer.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_completes_an_all_empty_topic_without_waiting_for_a_record()
    {
        _bounds = [new(0, 42, 42), new(1, 0, 0)];
        _consumer.FailBootstrap();
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 0, 42);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 0);
        _consumer.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_applies_the_last_retained_record_and_scans_a_compacted_tail()
    {
        var scan = _consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(_consumer, scan, 17);
        _consumer.CompleteCheckpoint(scan, 18);
        CompleteEmptyPartitions();
        _consumer.IsValid.Should().BeFalse();
        _consumer.CompleteScan(scan, 20);
        _consumer.IsValid.Should().BeFalse();
        _consumer.CompleteCheckpoint(scan, 20);
        _consumer.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_does_not_wait_for_the_record_at_the_exclusive_end()
    {
        var scan = _consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(_consumer, scan, 19);
        _consumer.CompleteCheckpoint(scan, 20);
        CompleteEmptyPartitions();
        _consumer.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_does_not_move_captured_barriers_when_the_source_grows()
    {
        _bounds[0] = new(0, 10, 100);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 0, 20);
        CompleteEmptyPartitions();
        _consumer.Barriers[0].EndOffset.Should().Be(20);
        _consumer.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_accepts_durable_progress_beyond_the_captured_barrier()
    {
        var scan = _consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(_consumer, scan, 23);
        _consumer.CompleteCheckpoint(scan, 24);
        CompleteEmptyPartitions();
        _consumer.IsValid.Should().BeTrue();
        _consumer.StartPartitionScan(0).NextOffset.Should().Be(24);
    }

    [Test]
    public void It_continues_after_durable_concurrent_writes_even_if_the_checkpoint_only_covers_the_barrier()
    {
        var scan = _consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(_consumer, scan, 23);
        _consumer.CompleteCheckpoint(scan, 20);
        CompleteEmptyPartitions();
        _consumer.IsValid.Should().BeTrue();
        var incremental = _consumer.StartPartitionScan(0);
        incremental.NextOffset.Should().Be(24);
        _consumer.Stage(incremental, MessageContractConsumerBootstrapData.Record(0, 24, delete: true));
        _consumer.CompleteApply(incremental);
        _consumer.CompleteCheckpoint(incremental, 25);
        _consumer.Documents.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_wait_for_an_unflushed_concurrent_record_beyond_an_already_durable_barrier()
    {
        var scan = _consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(_consumer, scan, 19);
        _consumer.CompleteCheckpoint(scan, 20);
        _consumer.Stage(scan, MessageContractConsumerBootstrapData.Record(0, 20, delete: true));
        CompleteEmptyPartitions();
        _consumer.IsValid.Should().BeTrue();
        _consumer.Documents.Should().ContainSingle();
    }

    private void CompleteEmptyPartitions()
    {
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 2, 0);
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-BOOTSTRAP-DEADLINE")]
public class Given_MessageContractConsumerBootstrap_deadline
{
    private MessageContractConsumerBootstrap _consumer = null!;

    [SetUp]
    public void Setup() =>
        _consumer = new(MessageContractConsumerBootstrapData.Epoch, () => [new(0, 10, 11), new(1, 30, 30)]);

    [Test]
    public void It_starts_the_budget_with_the_first_partition_scan()
    {
        _consumer.AdvanceTime(TimeSpan.FromDays(2));
        _consumer.Attempt.Should().Be(1);
        _consumer.StartPartitionScan(0);
        _consumer.StartedAt.Should().Be(MessageContractConsumerBootstrapData.Epoch.AddDays(2));
        _consumer.Deadline.Should().Be(MessageContractConsumerBootstrapData.Epoch.AddDays(3));
    }

    [TestCase(-1L)]
    [TestCase(0L)]
    public void It_permits_durable_completion_up_to_and_including_the_deadline(long ticksFromDeadline)
    {
        var scan = _consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(_consumer, scan, 10);
        _consumer.CompleteCheckpoint(scan, 11);
        _consumer.AdvanceTime(
            MessageContractConsumerBootstrap.Budget + TimeSpan.FromTicks(ticksFromDeadline)
        );
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.IsValid.Should().BeTrue();
        _consumer.Attempt.Should().Be(1);
    }

    [Test]
    public void It_includes_skew_stalls_retries_and_rebalance_time_in_one_budget()
    {
        var scan = _consumer.StartPartitionScan(0);
        _consumer.AdvanceTime(TimeSpan.FromHours(8));
        MessageContractConsumerBootstrapData.Apply(_consumer, scan, 10);
        _consumer.CompleteCheckpoint(scan, 11);
        _consumer.AdvanceTime(TimeSpan.FromHours(8));
        _consumer.StartPartitionScan(0); // Same-attempt retry does not renew the deadline.
        _consumer.AdvanceTime(TimeSpan.FromHours(7));
        _consumer.StartPartitionScan(1); // Late partition/reassignment keeps the first scan's clock.
        _consumer.Deadline.Should().Be(MessageContractConsumerBootstrapData.Epoch.AddDays(1));
        _consumer.AdvanceTime(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        _consumer.IsValid.Should().BeFalse();
        _consumer.Attempt.Should().Be(2);
        _consumer.Documents.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_restarts_when_state_or_checkpoint_persistence_misses_the_deadline(bool applyIsDurable)
    {
        var scan = _consumer.StartPartitionScan(0);
        _consumer.Stage(scan, MessageContractConsumerBootstrapData.Record(0, 10));
        if (applyIsDurable)
        {
            _consumer.CompleteApply(scan);
        }
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 30);
        _consumer.AdvanceTime(MessageContractConsumerBootstrap.Budget + TimeSpan.FromTicks(1));
        _consumer.State.Should().Be(MessageContractBootstrapState.AwaitingScan);
        _consumer.Documents.Should().BeEmpty();
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.DurableNextOffsets.Values.Should().Equal(10, 30);
    }

    [Test]
    public void It_remains_invalid_at_the_deadline_without_completion_and_restarts_one_tick_later()
    {
        _consumer.StartPartitionScan(0);
        _consumer.AdvanceTime(MessageContractConsumerBootstrap.Budget);
        _consumer.IsValid.Should().BeFalse();
        _consumer.Attempt.Should().Be(1);
        _consumer.AdvanceTime(TimeSpan.FromTicks(1));
        _consumer.Attempt.Should().Be(2);
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-BOOTSTRAP-RECOVERY")]
public class Given_MessageContractConsumerBootstrap_recovery
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
        _bounds = [new(0, 10, 20), new(1, 30, 40)];
        _consumer = new(
            MessageContractConsumerBootstrapData.Epoch,
            () =>
            {
                _observations++;
                if (_failObservation)
                {
                    throw new InvalidOperationException("Bounds observation unavailable.");
                }
                return _bounds;
            }
        );
        _oldScan = _consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(_consumer, _oldScan, 19);
        _consumer.CompleteCheckpoint(_oldScan, 20);
        var pending = _consumer.StartPartitionScan(1);
        _consumer.Stage(pending, MessageContractConsumerBootstrapData.Record(1, 39, delete: true));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_discards_all_partitions_and_reobserves_current_earliest_offsets(bool timeout)
    {
        _bounds = [new(0, 15, 25), new(1, 35, 45)];
        Restart(timeout);
        _observations.Should().Be(2);
        _consumer.Documents.Should().BeEmpty();
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.IsValid.Should().BeFalse();
        _consumer.StartPartitionScan(0).NextOffset.Should().Be(15);
        _consumer.StartPartitionScan(1).NextOffset.Should().Be(35);
        _consumer.Barriers.Values.Select(p => p.EndOffset).Should().Equal(25, 45);
    }

    [TestCase("stage")]
    [TestCase("apply")]
    [TestCase("scan")]
    [TestCase("checkpoint")]
    public void It_rejects_delayed_callbacks_from_the_discarded_attempt(string callback)
    {
        _consumer.FailBootstrap();
        var current = _consumer.StartPartitionScan(0);
        _consumer.Stage(current, MessageContractConsumerBootstrapData.Record(0, 19));
        Action delayed = callback switch
        {
            "stage" => () => _consumer.Stage(_oldScan, MessageContractConsumerBootstrapData.Record(0, 19)),
            "apply" => () => _consumer.CompleteApply(_oldScan),
            "scan" => () => _consumer.CompleteScan(_oldScan, 20),
            _ => () => _consumer.CompleteCheckpoint(_oldScan, 20),
        };
        delayed
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Consumer scan belongs to an inactive bootstrap attempt.");
        _consumer.IsValid.Should().BeFalse();
        _consumer.Documents.Should().BeEmpty();
    }

    [Test]
    public void It_cannot_restart_from_old_bounds_when_a_fresh_observation_fails()
    {
        _failObservation = true;
        Action restart = () => _consumer.FailBootstrap();
        restart.Should().Throw<InvalidOperationException>().WithMessage("Bounds observation unavailable.");
        _consumer.Documents.Should().BeEmpty();
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.IsValid.Should().BeFalse();
        Action scan = () => _consumer.StartPartitionScan(0);
        scan.Should().Throw<InvalidOperationException>();
        _failObservation = false;
        _bounds = [new(0, 50, 50), new(1, 60, 60)];
        _consumer.FailBootstrap();
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 0, 50);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 60);
        _consumer.IsValid.Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_can_finish_a_complete_reconstruction_with_a_new_first_scan_deadline(bool timeout)
    {
        Restart(timeout);
        _consumer.AdvanceTime(TimeSpan.FromDays(2));
        var scan = _consumer.StartPartitionScan(0);
        MessageContractConsumerBootstrapData.Apply(_consumer, scan, 19);
        _consumer.CompleteCheckpoint(scan, 20);
        _consumer.AdvanceTime(MessageContractConsumerBootstrap.Budget);
        MessageContractConsumerBootstrapData.ScanAndCheckpoint(_consumer, 1, 40);
        _consumer.IsValid.Should().BeTrue();
        _consumer.Documents.Should().ContainSingle();
        _consumer.Attempt.Should().Be(2);
        _consumer.Now.Should().Be(_consumer.Deadline);
    }

    private void Restart(bool timeout)
    {
        if (timeout)
        {
            _consumer.AdvanceTime(MessageContractConsumerBootstrap.Budget + TimeSpan.FromTicks(1));
        }
        else
        {
            _consumer.FailBootstrap();
        }
    }
}

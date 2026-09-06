// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

public enum MessageContractBootstrapState
{
    AwaitingScan,
    Scanning,
    Valid,
}

/// <summary>Fences delayed transport/persistence callbacks from a discarded bootstrap attempt.</summary>
public sealed record MessageContractConsumerScan(int Attempt, int Partition, long NextOffset, int Proof = 0);

/// <summary>
/// Test-only bootstrap coordinator over the serialized-record consumer. The bounds callback supplies
/// current broker observations on every full restart; completion requires independent scan and durable
/// checkpoint observations. Incremental continuity renews that proof without discarding valid state;
/// every uncertainty or expired deadline uses the same full-bootstrap restart path.
/// </summary>
public sealed class MessageContractConsumerBootstrap
{
    public static readonly TimeSpan Budget = TimeSpan.FromHours(24);

    private readonly MessageContractConsumer _consumer;
    private readonly Func<IReadOnlyList<MessageContractPartitionBounds>> _observeBounds;
    private readonly HashSet<int> _scanningPartitions = [];
    private readonly HashSet<int> _renewalScannedPartitions = [];
    private readonly HashSet<int> _renewalCheckpointedPartitions = [];
    private int _proof;
    private bool _hasBounds;

    public MessageContractConsumerBootstrap(
        DateTimeOffset now,
        Func<IReadOnlyList<MessageContractPartitionBounds>> observeBounds
    )
    {
        _consumer = new(now);
        _observeBounds = observeBounds;
        Restart();
    }

    public MessageContractBootstrapState State { get; private set; }
    public bool IsValid => State == MessageContractBootstrapState.Valid;
    public int Attempt { get; private set; }
    public DateTimeOffset Now => _consumer.Now;
    public bool RenewalInProgress { get; private set; }

    // Meaningful only while valid. Capture/start/retry cannot extend this completed proof's interval.
    public DateTimeOffset ProofCompletedAt { get; private set; }
    public DateTimeOffset RenewalDeadline => ProofCompletedAt + Budget;

    // Meaningful once the first partition has started scanning in this attempt.
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset Deadline => StartedAt + Budget;
    public IReadOnlyDictionary<string, MessageContractConsumerDocument> Documents => _consumer.Documents;
    public IReadOnlyDictionary<int, MessageContractPartitionBounds> Barriers => _consumer.Assignment;
    public IReadOnlyDictionary<int, long> DurableNextOffsets => _consumer.DurableNextOffsets;
    public IReadOnlyDictionary<int, long> Checkpoints => _consumer.Checkpoints;

    /// <summary>
    /// Starts at earliest on bootstrap, or at the durable next offset after bootstrap. Repeated starts
    /// model retries/rebalance delays in the same attempt and never reset its wall-clock budget.
    /// </summary>
    public MessageContractConsumerScan StartPartitionScan(int partition)
    {
        if (!_hasBounds || !Barriers.ContainsKey(partition))
        {
            throw new InvalidOperationException("Unassigned consumer partition.");
        }
        if (State == MessageContractBootstrapState.AwaitingScan)
        {
            StartedAt = Now;
            State = MessageContractBootstrapState.Scanning;
        }
        _scanningPartitions.Add(partition);
        return new(Attempt, partition, DurableNextOffsets[partition], _proof);
    }

    public void Stage(MessageContractConsumerScan scan, MessageContractConsumerRecord record)
    {
        RequireCurrentScan(scan);
        if (scan.Partition != record.Partition)
        {
            throw new InvalidOperationException("Consumer scan and delivery partitions differ.");
        }
        _consumer.Stage(record);
    }

    public MessageContractConsumerApplyResult CompleteApply(MessageContractConsumerScan scan)
    {
        RequireCurrentScan(scan);
        var result = _consumer.CompleteApply(scan.Partition);
        ObserveRenewalScan(scan);
        return result;
    }

    /// <summary>Requires a real transport position, including scans through compacted gaps.</summary>
    public void CompleteScan(MessageContractConsumerScan scan, long nextOffset)
    {
        RequireCurrentScan(scan);
        _consumer.CompleteScan(scan.Partition, nextOffset);
        ObserveRenewalScan(scan);
    }

    public void CompleteCheckpoint(MessageContractConsumerScan scan, long nextOffset)
    {
        RequireCurrentScan(scan);
        _consumer.CompleteCheckpoint(scan.Partition, nextOffset);
        if (
            State == MessageContractBootstrapState.Scanning
            && Barriers.Values.All(p =>
                _scanningPartitions.Contains(p.Partition)
                && Checkpoints.TryGetValue(p.Partition, out long checkpoint)
                && checkpoint >= p.EndOffset
                && DurableNextOffsets[p.Partition] >= p.EndOffset
            )
        )
        {
            State = MessageContractBootstrapState.Valid;
            ProofCompletedAt = Now;
        }
        if (
            RenewalInProgress
            && scan.Proof == _proof
            && _renewalScannedPartitions.Contains(scan.Partition)
            && nextOffset >= Barriers[scan.Partition].EndOffset
        )
        {
            _renewalCheckpointedPartitions.Add(scan.Partition);
            if (_renewalCheckpointedPartitions.Count == Barriers.Count)
            {
                ProofCompletedAt = Now;
                RenewalInProgress = false;
            }
        }
    }

    /// <summary>
    /// Captures fresh exclusive ends for the entire assignment. Each partition then needs a scan/apply
    /// and checkpoint completion under a new scan handle, even when its end has not changed. Older
    /// callbacks may finish ordinary incremental work but cannot certify this new proof.
    /// </summary>
    public void BeginRenewal(IReadOnlyDictionary<int, long> endOffsets)
    {
        if (!IsValid || RenewalInProgress)
        {
            throw new InvalidOperationException(
                "Consumer renewal requires valid state and no active renewal."
            );
        }
        try
        {
            _consumer.CaptureEndOffsets(endOffsets);
        }
        catch (InvalidOperationException)
        {
            Restart();
            throw;
        }
        _proof++;
        _renewalScannedPartitions.Clear();
        _renewalCheckpointedPartitions.Clear();
        RenewalInProgress = true;
    }

    public void LoseCheckpoints()
    {
        _consumer.LoseCheckpoints();
        Restart();
    }

    public void CorruptCheckpoints()
    {
        _consumer.CorruptCheckpoints();
        Restart();
    }

    public void ReportUncertainProgress() => Restart();

    public void ObserveAssignment(IReadOnlyList<int> partitions)
    {
        if (partitions.Count != Barriers.Count || !partitions.ToHashSet().SetEquals(Barriers.Keys))
        {
            Restart();
        }
    }

    public void AdvanceTime(TimeSpan elapsed)
    {
        _consumer.AdvanceTime(elapsed);
        // Completion at exactly 24 hours is permitted. Time spent waiting for persistence counts.
        if (
            (State == MessageContractBootstrapState.Scanning && Now > Deadline)
            || (IsValid && Now > RenewalDeadline)
        )
        {
            Restart();
        }
    }

    public void FailBootstrap()
    {
        if (IsValid)
        {
            throw new InvalidOperationException("Bootstrap has already completed.");
        }
        Restart();
    }

    private void Restart()
    {
        // Revoke and discard before observing new bounds, even if that observation fails.
        State = MessageContractBootstrapState.AwaitingScan;
        RenewalInProgress = false;
        ProofCompletedAt = default;
        _hasBounds = false;
        Attempt++;
        _scanningPartitions.Clear();
        _renewalScannedPartitions.Clear();
        _renewalCheckpointedPartitions.Clear();
        _consumer.DiscardState();
        _consumer.Assign(_observeBounds());
        _hasBounds = true;
    }

    private void ObserveRenewalScan(MessageContractConsumerScan scan)
    {
        if (
            RenewalInProgress
            && scan.Proof == _proof
            && DurableNextOffsets[scan.Partition] >= Barriers[scan.Partition].EndOffset
        )
        {
            _renewalScannedPartitions.Add(scan.Partition);
        }
    }

    private void RequireCurrentScan(MessageContractConsumerScan scan)
    {
        if (scan.Attempt != Attempt || !_scanningPartitions.Contains(scan.Partition))
        {
            throw new InvalidOperationException("Consumer scan belongs to an inactive bootstrap attempt.");
        }
    }
}

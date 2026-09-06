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
public sealed record MessageContractConsumerScan(int Attempt, int Partition, long NextOffset);

/// <summary>
/// Test-only bootstrap coordinator over the serialized-record consumer. The bounds callback supplies
/// current broker observations on every full restart; completion requires independent scan and durable
/// checkpoint observations. Recurring continuity proofs belong to the subsequent conformance task.
/// </summary>
public sealed class MessageContractConsumerBootstrap
{
    public static readonly TimeSpan Budget = TimeSpan.FromHours(24);

    private readonly MessageContractConsumer _consumer;
    private readonly Func<IReadOnlyList<MessageContractPartitionBounds>> _observeBounds;
    private readonly HashSet<int> _scanningPartitions = [];
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
        return new(Attempt, partition, DurableNextOffsets[partition]);
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
        return _consumer.CompleteApply(scan.Partition);
    }

    /// <summary>Requires a real transport position, including scans through compacted gaps.</summary>
    public void CompleteScan(MessageContractConsumerScan scan, long nextOffset)
    {
        RequireCurrentScan(scan);
        _consumer.CompleteScan(scan.Partition, nextOffset);
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
        }
    }

    public void AdvanceTime(TimeSpan elapsed)
    {
        _consumer.AdvanceTime(elapsed);
        // Completion at exactly 24 hours is permitted. Time spent waiting for persistence counts.
        if (State == MessageContractBootstrapState.Scanning && Now > Deadline)
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
        _hasBounds = false;
        Attempt++;
        _scanningPartitions.Clear();
        _consumer.DiscardState();
        _consumer.Assign(_observeBounds());
        _hasBounds = true;
    }

    private void RequireCurrentScan(MessageContractConsumerScan scan)
    {
        if (scan.Attempt != Attempt || !_scanningPartitions.Contains(scan.Partition))
        {
            throw new InvalidOperationException("Consumer scan belongs to an inactive bootstrap attempt.");
        }
    }
}

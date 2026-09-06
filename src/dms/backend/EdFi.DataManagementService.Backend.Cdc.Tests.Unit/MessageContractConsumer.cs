// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

public enum MessageContractConsumerApplyResult
{
    Inserted,
    Replaced,
    Stale,
    Duplicate,
    ProducerContractViolation,
    Deleted,
}

public enum MessageContractCheckpointHealth
{
    Healthy,
    Missing,
    Corrupt,
}

public sealed record MessageContractPartitionBounds(int Partition, long EarliestOffset, long EndOffset);

/// <summary>A test transport record. Kafka null is explicit and distinct from any non-null bytes.</summary>
public sealed record MessageContractConsumerRecord(
    int Partition,
    long Offset,
    ReadOnlyMemory<byte> Key,
    ReadOnlyMemory<byte> Value,
    bool IsKafkaNull = false
);

/// <summary>Materialized state deliberately has no per-document offset or delete watermark.</summary>
public sealed class MessageContractConsumerDocument
{
    private readonly byte[] _bytes;

    internal MessageContractConsumerDocument(long contentVersion, JsonElement envelope, byte[] bytes)
    {
        ContentVersion = contentVersion;
        Envelope = envelope;
        _bytes = bytes;
    }

    public long ContentVersion { get; }
    public JsonElement Envelope { get; }
    public byte[] SerializedValue => (byte[])_bytes.Clone();

    internal bool HasSameBytes(MessageContractConsumerDocument other) =>
        _bytes.AsSpan().SequenceEqual(other._bytes);
}

/// <summary>
/// Test-only consumer boundary shared with broker conformance tests. Delivery, durable application,
/// and checkpoint persistence are separately driven. This is not a bootstrap/continuity evaluator.
/// </summary>
public sealed class MessageContractConsumer(DateTimeOffset now)
{
    private readonly Dictionary<string, MessageContractConsumerDocument> _documents = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<int, MessageContractPartitionBounds> _assignment = [];
    private readonly Dictionary<int, PendingApply> _pending = [];
    private readonly Dictionary<int, long> _durableNextOffsets = [];
    private readonly Dictionary<int, long> _checkpoints = [];

    public DateTimeOffset Now { get; private set; } = now;
    public MessageContractCheckpointHealth CheckpointHealth { get; private set; } =
        MessageContractCheckpointHealth.Healthy;
    public IReadOnlyDictionary<string, MessageContractConsumerDocument> Documents => _documents.AsReadOnly();
    public IReadOnlyDictionary<int, MessageContractPartitionBounds> Assignment => _assignment.AsReadOnly();
    public IReadOnlyDictionary<int, long> DurableNextOffsets => _durableNextOffsets.AsReadOnly();
    public IReadOnlyDictionary<int, long> Checkpoints => _checkpoints.AsReadOnly();
    public bool HasPendingApplies => _pending.Count != 0;

    // Assignment replacement and checkpoint fault injection only expose observations here.
    // The subsequent bootstrap/continuity tasks own their invalidation/reconstruction policy.
    public void Assign(IReadOnlyList<MessageContractPartitionBounds> partitions)
    {
        if (
            partitions.Count == 0
            || partitions.Select(p => p.Partition).Distinct().Count() != partitions.Count
            || partitions.Any(p => p.Partition < 0 || p.EarliestOffset < 0 || p.EndOffset < p.EarliestOffset)
        )
        {
            throw new InvalidOperationException("Invalid consumer partition bounds.");
        }

        _assignment.Clear();
        _pending.Clear();
        _durableNextOffsets.Clear();
        _checkpoints.Clear();
        foreach (var partition in partitions)
        {
            _assignment.Add(partition.Partition, partition);
            _durableNextOffsets.Add(partition.Partition, partition.EarliestOffset);
        }
        CheckpointHealth = MessageContractCheckpointHealth.Healthy;
    }

    public void CaptureEndOffsets(IReadOnlyDictionary<int, long> endOffsets)
    {
        if (
            endOffsets.Count != _assignment.Count
            || _assignment.Values.Any(p =>
                !endOffsets.TryGetValue(p.Partition, out long end) || end < p.EndOffset
            )
        )
        {
            throw new InvalidOperationException("Invalid consumer end-offset observation.");
        }
        foreach (var (partition, end) in endOffsets)
        {
            _assignment[partition] = _assignment[partition] with { EndOffset = end };
        }
    }

    public void Stage(MessageContractConsumerRecord record)
    {
        RequireAssigned(record.Partition);
        if (
            _pending.ContainsKey(record.Partition)
            || record.Offset < _durableNextOffsets[record.Partition]
            || record.Offset == long.MaxValue
        )
        {
            throw new InvalidOperationException("Invalid consumer delivery position or pending apply.");
        }

        string key = Encoding.UTF8.GetString(record.Key.Span);
        if (!Guid.TryParseExact(key, "D", out Guid uuid) || uuid.ToString("D") != key)
        {
            throw new InvalidOperationException("Invalid public consumer key.");
        }

        if (record.IsKafkaNull)
        {
            if (!record.Value.IsEmpty)
            {
                throw new InvalidOperationException("Kafka null cannot carry value bytes.");
            }
            _pending.Add(record.Partition, new PendingDelete(key, record.Offset + 1));
            return;
        }

        // Copy before parsing: transport buffers may be reused after Stage returns.
        byte[] bytes = record.Value.ToArray();
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes);
            JsonElement root = json.RootElement;
            if (
                root.GetProperty("contractVersion").GetInt32() != 1
                || root.GetProperty("documentUuid").GetString() != key
                || root.GetProperty("document").GetProperty("id").GetString() != key
            )
            {
                throw new InvalidOperationException();
            }
            long version = root.GetProperty("contentVersion").GetInt64();
            _pending.Add(
                record.Partition,
                new PendingUpsert(
                    key,
                    record.Offset + 1,
                    new MessageContractConsumerDocument(version, root.Clone(), bytes)
                )
            );
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or InvalidOperationException
                        or KeyNotFoundException
                        or FormatException
                        or OverflowException
            )
        {
            // Never include public bodies or parser exception text in test diagnostics.
            throw new InvalidOperationException("Invalid public consumer value.");
        }
    }

    public MessageContractConsumerApplyResult CompleteApply(int partition)
    {
        if (!_pending.Remove(partition, out var pending))
        {
            throw new InvalidOperationException("No pending consumer apply.");
        }

        MessageContractConsumerApplyResult result;
        if (pending is PendingDelete)
        {
            _documents.Remove(pending.Key);
            result = MessageContractConsumerApplyResult.Deleted;
        }
        else
        {
            var candidate = ((PendingUpsert)pending).Document;
            if (!_documents.TryGetValue(pending.Key, out var retained))
            {
                _documents.Add(pending.Key, candidate);
                result = MessageContractConsumerApplyResult.Inserted;
            }
            else if (candidate.ContentVersion > retained.ContentVersion)
            {
                _documents[pending.Key] = candidate;
                result = MessageContractConsumerApplyResult.Replaced;
            }
            else if (candidate.ContentVersion < retained.ContentVersion)
            {
                result = MessageContractConsumerApplyResult.Stale;
            }
            else
            {
                result = candidate.HasSameBytes(retained)
                    ? MessageContractConsumerApplyResult.Duplicate
                    : MessageContractConsumerApplyResult.ProducerContractViolation;
            }
        }

        // Even ignored deliveries complete a partition apply; offsets never break version ties.
        _durableNextOffsets[partition] = pending.NextOffset;
        return result;
    }

    /// <summary>Accept a transport scan position, including empty partitions and compacted gaps.</summary>
    public void CompleteScan(int partition, long nextOffset)
    {
        RequireAssigned(partition);
        if (_pending.ContainsKey(partition) || nextOffset < _durableNextOffsets[partition])
        {
            throw new InvalidOperationException("Consumer scan cannot pass an unfinished apply or regress.");
        }
        _durableNextOffsets[partition] = nextOffset;
    }

    public void CompleteCheckpoint(int partition, long nextOffset)
    {
        RequireAssigned(partition);
        long previous = _checkpoints.GetValueOrDefault(partition, _assignment[partition].EarliestOffset);
        if (
            CheckpointHealth != MessageContractCheckpointHealth.Healthy
            || nextOffset < previous
            || nextOffset > _durableNextOffsets[partition]
        )
        {
            throw new InvalidOperationException("Consumer checkpoint must follow durable application.");
        }
        _checkpoints[partition] = nextOffset;
    }

    public void LoseCheckpoints()
    {
        _checkpoints.Clear();
        CheckpointHealth = MessageContractCheckpointHealth.Missing;
    }

    public void CorruptCheckpoints() => CheckpointHealth = MessageContractCheckpointHealth.Corrupt;

    public void DiscardState()
    {
        _documents.Clear();
        _pending.Clear();
        _checkpoints.Clear();
        foreach (var partition in _assignment.Values)
        {
            _durableNextOffsets[partition.Partition] = partition.EarliestOffset;
        }
        CheckpointHealth = MessageContractCheckpointHealth.Healthy;
    }

    public void AdvanceTime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }
        Now += elapsed;
    }

    private void RequireAssigned(int partition)
    {
        if (!_assignment.ContainsKey(partition))
        {
            throw new InvalidOperationException("Unassigned consumer partition.");
        }
    }

    private abstract record PendingApply(string Key, long NextOffset);

    private sealed record PendingDelete(string Key, long NextOffset) : PendingApply(Key, NextOffset);

    private sealed record PendingUpsert(string Key, long NextOffset, MessageContractConsumerDocument Document)
        : PendingApply(Key, NextOffset);
}

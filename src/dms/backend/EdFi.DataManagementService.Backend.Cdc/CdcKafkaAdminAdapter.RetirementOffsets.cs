// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed partial class CdcKafkaAdminAdapter
{
    internal Func<IConsumer<byte[], byte[]>> CreateOffsetConsumer { get; init; } =
        () => throw new InvalidOperationException("Offset-store inspection is unavailable.");

    private static IConsumer<byte[], byte[]> BuildOffsetConsumer(AdminClientConfig administration) =>
        new ConsumerBuilder<byte[], byte[]>(
            new ConsumerConfig(administration)
            {
                ClientId = "dms-cdc-retirement",
                // Manual assignment never joins this group or asks the coordinator for stored positions.
                GroupId = "dms-cdc-retirement",
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false,
                EnablePartitionEof = true,
                AutoOffsetReset = AutoOffsetReset.Error,
                AllowAutoCreateTopics = false,
                IsolationLevel = IsolationLevel.ReadCommitted,
                QueuedMaxMessagesKbytes = 65536,
            }
        ).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();

    /// <summary>
    /// Bounded, read-only snapshot of the qualified Connect internal JSON store. This is absence
    /// evidence for retirement only, never source-position/readiness evidence. No subscription,
    /// offset commits, new grants or worker-storage mutations. Unreadable storage fails closed.
    /// </summary>
    public async Task<CdcTransportResult<CdcRetirementOffsetState>> InspectRetirementOffsetsAsync(
        CdcArtifactCleanupScope scope,
        CancellationToken cancellationToken
    )
    {
        var request = scope.Request;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.CallTimeout);
        var token = timeout.Token;
        return await GuardAsync(
            async () =>
            {
                string topic = request.WorkerPolicy.OffsetStorageTopic.Value;
                var inspection = await InspectTopicAsync(request, topic, token);
                if (inspection is not CdcTransportResult<CdcKafkaTopicEvidence>.Observed observed)
                {
                    return Failure<CdcRetirementOffsetState>(
                        inspection.Diagnostics.FirstOrDefault()?.Failure ?? CdcDeploymentFailure.Unavailable
                    );
                }
                var evidence = observed.Value;
                if (
                    evidence.PartitionReplicas.Count > 1024
                    || !evidence.Configuration.TryGetValue("cleanup.policy", out var cleanup)
                    || cleanup.Value != "compact"
                )
                {
                    return Failure<CdcRetirementOffsetState>(CdcDeploymentFailure.ValidationFailed);
                }
                var partitions = evidence
                    .PartitionReplicas.Keys.Select(p => new TopicPartition(topic, p))
                    .ToArray();
                Dictionary<TopicPartition, (long Low, long High)> bounds = [];
                foreach (var partition in partitions)
                {
                    long low = await ReadHistoryOffsetAsync(request, partition, OffsetSpec.Earliest(), token);
                    long high = await ReadHistoryOffsetAsync(request, partition, OffsetSpec.Latest(), token);
                    if (low > high)
                    {
                        return Failure<CdcRetirementOffsetState>(CdcDeploymentFailure.Unavailable);
                    }
                    bounds.Add(partition, (low, high));
                }
                var state = await Task.Run(
                    () => ReadRetirementSnapshot(request.Binding.ConnectorName, bounds, token),
                    token
                );
                // Retention/truncation or partition changes during the snapshot invalidate its absence proof.
                foreach (var partition in partitions)
                {
                    if (
                        await ReadHistoryOffsetAsync(request, partition, OffsetSpec.Earliest(), token)
                            != bounds[partition].Low
                        || await ReadHistoryOffsetAsync(request, partition, OffsetSpec.Latest(), token)
                            < bounds[partition].High
                    )
                    {
                        return Failure<CdcRetirementOffsetState>(CdcDeploymentFailure.Unavailable);
                    }
                }
                var final = await InspectTopicAsync(request, topic, token);
                if (
                    final is not CdcTransportResult<CdcKafkaTopicEvidence>.Observed current
                    || !current
                        .Value.PartitionReplicas.Keys.Order()
                        .SequenceEqual(evidence.PartitionReplicas.Keys.Order())
                )
                {
                    return Failure<CdcRetirementOffsetState>(CdcDeploymentFailure.Unavailable);
                }
                return new CdcTransportResult<CdcRetirementOffsetState>.Observed(state);
            },
            cancellationToken
        );
    }

    private CdcRetirementOffsetState ReadRetirementSnapshot(
        string connector,
        Dictionary<TopicPartition, (long Low, long High)> bounds,
        CancellationToken token
    )
    {
        using var consumer = CreateOffsetConsumer();
        consumer.Assign(bounds.Select(p => new TopicPartitionOffset(p.Key, p.Value.Low)));
        HashSet<TopicPartition> pending = [.. bounds.Keys];
        Dictionary<string, (TopicPartition Partition, bool Present)> keys = [];
        long bytes = 0;
        int records = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var record = consumer.Consume(token);
            if (record is null)
            {
                throw new InvalidOperationException("Offset-store snapshot is incomplete.");
            }
            if (!bounds.TryGetValue(record.TopicPartition, out var range) || record.Offset.Value < range.Low)
            {
                throw new InvalidOperationException("Offset-store snapshot is contradictory.");
            }
            if (record.Offset.Value >= range.High)
            {
                pending.Remove(record.TopicPartition);
                consumer.Pause([record.TopicPartition]);
                continue;
            }
            if (record.IsPartitionEOF)
            {
                throw new InvalidOperationException("Offset-store snapshot is truncated.");
            }
            bytes += (record.Message.Key?.Length ?? 0) + (record.Message.Value?.Length ?? 0);
            if (++records > 1_000_000 || bytes > 64 * 1024 * 1024)
            {
                throw new InvalidOperationException("Offset-store snapshot exceeds inspection bounds.");
            }
            ApplyRetirementOffset(connector, record, keys);
        }
        return keys.Values.Any(key => key.Present)
            ? CdcRetirementOffsetState.Present
            : CdcRetirementOffsetState.Absent;
    }

    private static void ApplyRetirementOffset(
        string connector,
        ConsumeResult<byte[], byte[]> record,
        Dictionary<string, (TopicPartition Partition, bool Present)> keys
    )
    {
        // Kafka Connect's internal schemaless JSON key is [connector namespace, source partition].
        // Keep byte identity for tombstones, just as KafkaOffsetBackingStore does; never export keys/values.
        if (record.Message.Key is null)
        {
            throw new InvalidOperationException("Offset-store key is malformed.");
        }
        using var key = JsonDocument.Parse(record.Message.Key);
        var root = key.RootElement;
        if (
            root.ValueKind != JsonValueKind.Array
            || root.GetArrayLength() == 0
            || root[0].ValueKind != JsonValueKind.String
        )
        {
            throw new InvalidOperationException("Offset-store key is malformed.");
        }
        if (root[0].GetString() != connector)
        {
            return;
        }
        if (root.GetArrayLength() != 2 || root[1].ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Offset-store partition is malformed.");
        }
        bool present = record.Message.Value is not null;
        if (present)
        {
            using var value = JsonDocument.Parse(record.Message.Value!);
            if (value.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Offset-store value is malformed.");
            }
        }
        string identity = Convert.ToBase64String(record.Message.Key);
        if (keys.TryGetValue(identity, out var existing) && existing.Partition != record.TopicPartition)
        {
            throw new InvalidOperationException("Offset-store key spans partitions.");
        }
        keys[identity] = (record.TopicPartition, present);
    }
}

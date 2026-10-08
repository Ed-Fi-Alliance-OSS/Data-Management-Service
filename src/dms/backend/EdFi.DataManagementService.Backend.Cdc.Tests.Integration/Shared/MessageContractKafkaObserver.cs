// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Endpoint-only byte observations; callers own provider fences and broker lifecycle.</summary>
internal sealed class MessageContractKafkaObserver(string bootstrapServers)
{
    internal ConsumerConfig CreateByteConsumerConfig() =>
        new()
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"cdc-message-observer-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            EnablePartitionEof = true,
            AutoOffsetReset = AutoOffsetReset.Error,
            AllowAutoCreateTopics = false,
            MaxPartitionFetchBytes = 70_000_000,
            FetchMaxBytes = 140_000_000,
        };

    private IConsumer<byte[], byte[]> CreateByteConsumer() =>
        new ConsumerBuilder<byte[], byte[]>(CreateByteConsumerConfig())
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();

    public Task<IReadOnlyList<MessageContractKafkaBoundary>> CaptureKafkaBoundariesAsync(
        string topic,
        CancellationToken token
    ) =>
        Task.Run(
            () =>
            {
                token.ThrowIfCancellationRequested();
                using IConsumer<byte[], byte[]> consumer = CreateByteConsumer();
                using IAdminClient admin = new AdminClientBuilder(
                    new AdminClientConfig { BootstrapServers = bootstrapServers }
                )
                    .SetLogHandler((_, _) => { })
                    .Build();
                try
                {
                    Metadata metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
                    TopicMetadata found = metadata.Topics.Single(t => t.Topic == topic);
                    if (found.Error.IsError || found.Partitions.Count == 0)
                    {
                        throw new InvalidOperationException("CDC Kafka topic metadata unavailable.");
                    }

                    return (IReadOnlyList<MessageContractKafkaBoundary>)
                        found
                            .Partitions.OrderBy(p => p.PartitionId)
                            .Select(p =>
                            {
                                token.ThrowIfCancellationRequested();
                                WatermarkOffsets offsets = consumer.QueryWatermarkOffsets(
                                    new TopicPartition(topic, p.PartitionId),
                                    TimeSpan.FromSeconds(10)
                                );
                                return new MessageContractKafkaBoundary(
                                    topic,
                                    p.PartitionId,
                                    offsets.Low.Value,
                                    offsets.High.Value
                                );
                            })
                            .ToArray();
                }
                catch (KafkaException)
                {
                    throw new InvalidOperationException("CDC Kafka boundary read failed. Details redacted.");
                }
            },
            token
        );

    /// <summary>Reads exactly [start, captured exclusive end) on each partition. Completion requires actual
    /// consumer scan positions, including gaps in a compacted log. A timeout never proves absence.</summary>
    public Task<MessageContractKafkaScan> ConsumeThroughAsync(
        IReadOnlyList<MessageContractKafkaBoundary> boundaries,
        CancellationToken token
    ) =>
        Task.Run(
            () =>
            {
                ValidateBoundaries(boundaries);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                using IConsumer<byte[], byte[]> consumer = CreateByteConsumer();
                try
                {
                    var pending = boundaries.ToDictionary(b => new TopicPartition(b.Topic, b.Partition));
                    var positions = boundaries.ToDictionary(
                        b => new TopicPartition(b.Topic, b.Partition),
                        b => b.StartOffset
                    );
                    List<MessageContractKafkaRecord> records = [];
                    foreach (var (partition, bound) in pending)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        WatermarkOffsets current = consumer.QueryWatermarkOffsets(
                            partition,
                            TimeSpan.FromSeconds(10)
                        );
                        if (current.Low.Value > bound.StartOffset || current.High.Value < bound.EndOffset)
                        {
                            throw new InvalidOperationException(
                                "CDC Kafka captured bounds are no longer retained."
                            );
                        }
                    }
                    consumer.Assign(
                        pending.Select(p => new TopicPartitionOffset(p.Key, new Offset(p.Value.StartOffset)))
                    );
                    while (pending.Count > 0)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        foreach (var (partition, bound) in pending.ToArray())
                        {
                            long position = consumer.Position(partition).Value;
                            if (bound.StartOffset == bound.EndOffset || position >= bound.EndOffset)
                            {
                                positions[partition] = bound.EndOffset;
                                consumer.Pause([partition]);
                                pending.Remove(partition);
                            }
                        }
                        if (pending.Count == 0)
                        {
                            break;
                        }

                        ConsumeResult<byte[], byte[]> observed = consumer.Consume(timeout.Token);
                        if (
                            observed.IsPartitionEOF
                            || !pending.TryGetValue(observed.TopicPartition, out var boundForRecord)
                            || observed.Offset.Value >= boundForRecord.EndOffset
                        )
                        {
                            continue;
                        }

                        records.Add(
                            new(
                                observed.Topic,
                                observed.Partition.Value,
                                observed.Offset.Value,
                                MessageContractKafkaBytes.From(observed.Message.Key),
                                MessageContractKafkaBytes.From(observed.Message.Value),
                                observed
                                    .Message.Headers.Select(h => new MessageContractKafkaHeader(
                                        h.Key,
                                        MessageContractKafkaBytes.From(h.GetValueBytes())
                                    ))
                                    .ToArray(),
                                observed.Message.Timestamp.UnixTimestampMs
                            )
                        );
                    }
                    return new MessageContractKafkaScan(
                        records,
                        boundaries
                            .Select(b => new MessageContractKafkaBoundary(
                                b.Topic,
                                b.Partition,
                                b.StartOffset,
                                positions[new TopicPartition(b.Topic, b.Partition)]
                            ))
                            .ToArray()
                    );
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("CDC Kafka scan did not reach every captured boundary.");
                }
                catch (KafkaException)
                {
                    throw new InvalidOperationException("CDC Kafka scan failed. Details redacted.");
                }
            },
            token
        );

    internal static void ValidateBoundaries(IReadOnlyList<MessageContractKafkaBoundary> boundaries)
    {
        if (
            boundaries.Count == 0
            || boundaries.Any(b =>
                b.Partition < 0
                || b.StartOffset < 0
                || b.EndOffset < b.StartOffset
                || string.IsNullOrWhiteSpace(b.Topic)
            )
            || boundaries.Select(b => (b.Topic, b.Partition)).Distinct().Count() != boundaries.Count
        )
        {
            throw new ArgumentException("Invalid CDC Kafka scan boundaries.");
        }
    }
}

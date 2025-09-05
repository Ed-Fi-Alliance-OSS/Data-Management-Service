// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public sealed class KafkaMessageCollector : IDisposable
{
    private const string DOCUMENTS_TOPIC = "edfi.dms.document";

    private readonly ConcurrentBag<KafkaTestMessage> _messages = [];
    private readonly IConsumer<string, string> _consumer;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _consumeTask;
    private readonly TestLogger _logger;
    private readonly DateTime _collectionStartTime;

    public KafkaMessageCollector(string bootstrapServers, TestLogger logger)
    {
        _logger = logger;
        _collectionStartTime = DateTime.UtcNow;

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"dms-e2e-kafka-test-{Guid.NewGuid().ToString("N")[..8]}",
            AutoOffsetReset = AutoOffsetReset.Latest, // Only collect messages after we start
            EnableAutoCommit = false,
            SessionTimeoutMs = 6000,
            HeartbeatIntervalMs = 2000,
            // Add resolver for Docker container hostnames
            ClientDnsLookup = ClientDnsLookup.ResolveCanonicalBootstrapServersOnly,
        };

        _logger.log.Information($"KafkaMessageCollector: Connecting to {config.BootstrapServers}");
        _logger.log.Information($"KafkaMessageCollector: Consumer group: {config.GroupId}");
        _logger.log.Information($"KafkaMessageCollector: Topic: {DOCUMENTS_TOPIC}");
        _logger.log.Information(
            $"KafkaMessageCollector: Collection started at: {_collectionStartTime:yyyy-MM-dd HH:mm:ss.fff} UTC"
        );

        _consumer = new ConsumerBuilder<string, string>(config).Build();
        _consumer.Subscribe([DOCUMENTS_TOPIC]);

        _consumeTask = Task.Run(ConsumeMessages, _cancellationTokenSource.Token);

        // Wait briefly to ensure the consumer is ready and positioned at the latest offset
        WaitForConsumerReadyAsync().Wait();

        _logger.log.Debug($"KafkaMessageCollector initialized for topic: {DOCUMENTS_TOPIC}");
    }

    public IEnumerable<KafkaTestMessage> Messages => _messages.ToArray();

    public IEnumerable<KafkaTestMessage> GetDocumentMessages() =>
        _messages.Where(m => m.Topic == DOCUMENTS_TOPIC);

    /// <summary>
    /// Gets messages that were received after collection started, useful for filtering out pre-existing messages
    /// </summary>
    public IEnumerable<KafkaTestMessage> GetRecentDocumentMessages() =>
        _messages.Where(m => m.Topic == DOCUMENTS_TOPIC && m.Timestamp >= _collectionStartTime);

    private async Task WaitForConsumerReadyAsync()
    {
        // Wait briefly for the consumer to be assigned partitions and positioned at latest offset
        // This ensures we don't miss messages that are published immediately after collector creation
        var timeout = TimeSpan.FromSeconds(3);
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            var assignment = _consumer.Assignment;
            if (assignment.Count > 0)
            {
                _logger.log.Debug($"Consumer ready with {assignment.Count} assigned partition(s)");
                return;
            }
            await Task.Delay(50);
        }

        _logger.log.Warning("Consumer readiness timeout - proceeding anyway");
    }

    public void LogDiagnostics()
    {
        try
        {
            var assignment = _consumer.Assignment;
            _logger.log.Information($"Consumer diagnostics:");
            _logger.log.Information($"  Messages collected: {_messages.Count}");
            _logger.log.Information($"  Assigned partitions: {assignment.Count}");
            foreach (var partition in assignment)
            {
                try
                {
                    var position = _consumer.Position(partition);
                    _logger.log.Information(
                        $"    {partition.Topic}[{partition.Partition}]: position={position}"
                    );
                }
                catch (Exception ex)
                {
                    _logger.log.Warning(
                        $"    {partition.Topic}[{partition.Partition}]: failed to get position - {ex.Message}"
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.log.Warning($"Failed to get consumer diagnostics: {ex.Message}");
        }
    }

    private Task ConsumeMessages()
    {
        return Task.Run(
            () =>
            {
                try
                {
                    while (!_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        var consumeResult = _consumer.Consume(_cancellationTokenSource.Token);

                        if (consumeResult?.Message != null)
                        {
                            JsonNode? valueAsJson = null;
                            try
                            {
                                valueAsJson = JsonSerializer.Deserialize<JsonNode>(
                                    consumeResult.Message.Value
                                );
                            }
                            catch (JsonException)
                            {
                                // Value is not JSON, keep as string
                            }

                            var message = new KafkaTestMessage
                            {
                                Topic = consumeResult.Topic,
                                Key = consumeResult.Message.Key,
                                Value = consumeResult.Message.Value,
                                ValueAsJson = valueAsJson,
                                Timestamp = consumeResult.Message.Timestamp.UtcDateTime,
                                Partition = consumeResult.Partition.Value,
                                Offset = consumeResult.Offset.Value,
                            };

                            _messages.Add(message);
                            _logger.log.Debug(
                                $"Collected Kafka message from topic {message.Topic}, partition {message.Partition}, offset {message.Offset}"
                            );
                        }
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.log.Error($"Kafka consume error: {ex.Error.Reason}");
                }
                catch (OperationCanceledException)
                {
                    _logger.log.Debug("Kafka message consumption cancelled");
                }
                catch (Exception ex)
                {
                    _logger.log.Error($"Unexpected error in Kafka message consumption: {ex}");
                }
            },
            _cancellationTokenSource.Token
        );
    }

    public void Dispose()
    {
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _cancellationTokenSource.Cancel();

        try
        {
            _consumeTask.Wait(5000);
        }
        catch (AggregateException)
        {
            // Expected when cancellation occurs
        }

        _consumer.Close();
        _consumer.Dispose();
        _cancellationTokenSource.Dispose();

        _logger.log.Debug("KafkaMessageCollector disposed");
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;

namespace EdFi.DataManagementService.Tests.E2E.Management;

/// <summary>
/// A test utility class that collects Kafka messages from the "edfi.dms.document" topic during E2E test execution.
/// This class subscribes to Kafka as a consumer, continuously collects messages in the background, and provides
/// methods to retrieve and filter messages for test assertions. It automatically starts message collection from
/// the latest offset to avoid capturing pre-existing messages and includes diagnostic capabilities for
/// troubleshooting test failures. Used exclusively in E2E tests to verify that DMS operations properly publish
/// messages to Kafka.
/// </summary>
public sealed class KafkaMessageCollector : IDisposable
{
    private const string DOCUMENTS_TOPIC = "edfi.dms.document";

    private readonly ConcurrentBag<KafkaTestMessage> _messages = [];
    private readonly IConsumer<string, string> _consumer;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _consumeTask;
    private readonly TestLogger _logger;
    private readonly DateTime _collectionStartTime;

    /// <summary>
    /// Initialize and begin collecting messages from Kafka.
    /// </summary>
    /// <param name="bootstrapServers">The Kafka bootstrap servers connection string (e.g., "localhost:9092")</param>
    /// <param name="logger">The test logger instance for diagnostic output</param>
    public KafkaMessageCollector(string bootstrapServers, TestLogger logger)
    {
        _logger = logger;
        _collectionStartTime = DateTime.UtcNow;

        ConsumerConfig config = new()
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

        // Start the message collection
        _consumeTask = Task.Run(ConsumeMessages, _cancellationTokenSource.Token);

        // Wait briefly to ensure the consumer is ready and positioned at the latest offset
        var isReady = WaitForConsumerReadyAsync().Result;
        if (!isReady)
        {
            _logger.log.Warning("Consumer not fully ready but proceeding with collection");
        }

        _logger.log.Debug($"KafkaMessageCollector initialized for topic: {DOCUMENTS_TOPIC}");
    }

    /// <summary>
    /// Gets all collected messages as a snapshot array to avoid concurrency issues
    /// </summary>
    public IEnumerable<KafkaTestMessage> Messages => _messages.ToArray();

    /// <summary>
    /// Gets the current count of collected messages for debugging purposes
    /// </summary>
    public int MessageCount => _messages.Count;

    /// <summary>
    /// Gets all messages from the edfi.dms.document topic
    /// </summary>
    public IEnumerable<KafkaTestMessage> GetDocumentMessages() =>
        _messages.Where(m => m.Topic == DOCUMENTS_TOPIC);

    /// <summary>
    /// Gets messages that were received after collection started, useful for filtering out pre-existing messages
    /// </summary>
    public IEnumerable<KafkaTestMessage> GetRecentDocumentMessages() =>
        _messages.Where(m => m.Topic == DOCUMENTS_TOPIC && m.Timestamp >= _collectionStartTime);

    /// <summary>
    /// Waits for the consumer to be fully ready with a public synchronous interface
    /// </summary>
    public bool WaitForConsumerReady(TimeSpan timeout)
    {
        return WaitForConsumerReadyAsync(timeout).Result;
    }

    /// <summary>
    /// Waits asynchronously for the consumer to be assigned partitions and positioned at the latest offset.
    /// This ensures we don't miss messages that are published immediately after collector creation.
    /// </summary>
    /// <param name="timeout">Optional timeout for waiting (defaults to 3 seconds)</param>
    /// <returns>True if consumer is ready, false if timeout occurred</returns>
    private async Task<bool> WaitForConsumerReadyAsync(TimeSpan? timeout = null)
    {
        var timeoutValue = timeout ?? TimeSpan.FromSeconds(3);
        var start = DateTime.UtcNow;
        var lastLogTime = start;

        _logger.log.Debug($"Waiting for consumer readiness (timeout: {timeoutValue.TotalSeconds}s)");

        while (DateTime.UtcNow - start < timeoutValue)
        {
            List<TopicPartition> assignment = _consumer.Assignment;

            // Log progress every second
            if (DateTime.UtcNow - lastLogTime > TimeSpan.FromSeconds(1))
            {
                _logger.log.Debug(
                    $"Consumer readiness check: {assignment.Count} assigned partition(s) after {(DateTime.UtcNow - start).TotalSeconds:F1}s"
                );
                lastLogTime = DateTime.UtcNow;
            }

            if (assignment.Count > 0)
            {
                // Verify we have valid partition assignments and positions
                bool allPartitionsReady = true;
                foreach (var partition in assignment)
                {
                    try
                    {
                        Offset position = _consumer.Position(partition);
                        _logger.log.Debug(
                            $"Partition {partition.Topic}[{partition.Partition}] ready at position {position}"
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.log.Debug(
                            $"Partition {partition.Topic}[{partition.Partition}] not ready: {ex.Message}"
                        );
                        allPartitionsReady = false;
                        break;
                    }
                }

                if (allPartitionsReady)
                {
                    _logger.log.Information(
                        $"Consumer ready with {assignment.Count} assigned partition(s) after {(DateTime.UtcNow - start).TotalSeconds:F1}s"
                    );
                    return true;
                }
            }

            await Task.Delay(50);
        }

        _logger.log.Warning($"Consumer readiness timeout after {(DateTime.UtcNow - start).TotalSeconds:F1}s");
        return false;
    }

    /// <summary>
    /// Logs diagnostic information about the consumer state including assigned partitions and message counts.
    /// Useful for debugging test failures related to Kafka message collection.
    /// </summary>
    public void LogDiagnostics()
    {
        try
        {
            List<TopicPartition> assignment = _consumer.Assignment;
            _logger.log.Information($"Consumer diagnostics:");
            _logger.log.Information($"  Messages collected: {_messages.Count}");
            _logger.log.Information($"  Assigned partitions: {assignment.Count}");
            foreach (TopicPartition partition in assignment)
            {
                try
                {
                    Offset position = _consumer.Position(partition);
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

    /// <summary>
    /// Background task that continuously consumes messages from Kafka and adds them to the internal collection.
    /// Runs until cancellation is requested via the disposal of this instance.
    /// </summary>
    private Task ConsumeMessages()
    {
        return Task.Run(
            () =>
            {
                try
                {
                    while (!_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        ConsumeResult<string, string> consumeResult = _consumer.Consume(
                            _cancellationTokenSource.Token
                        );

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

                            KafkaTestMessage message = new()
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

                            var timeSinceCollectionStart = message.Timestamp - _collectionStartTime;
                            var currentTime = DateTime.UtcNow;
                            var processingDelay = currentTime - message.Timestamp;

                            _logger.log.Debug(
                                $"Collected Kafka message from topic {message.Topic}, partition {message.Partition}, offset {message.Offset}"
                            );
                            _logger.log.Debug(
                                $"Message timing - Arrived: {message.Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC, "
                                    + $"Time since collection start: {timeSinceCollectionStart.TotalSeconds:F3}s, "
                                    + $"Processing delay: {processingDelay.TotalMilliseconds:F1}ms, "
                                    + $"Total messages collected: {_messages.Count}"
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

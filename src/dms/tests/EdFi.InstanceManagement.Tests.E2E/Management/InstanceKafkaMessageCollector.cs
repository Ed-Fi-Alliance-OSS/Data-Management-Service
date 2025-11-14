// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// A test utility class that collects Kafka messages from multiple instance-specific topics
/// during E2E test execution. This class subscribes to Kafka as a consumer, continuously
/// collects messages in the background from all configured instance topics, and provides
/// methods to retrieve and filter messages for test assertions.
///
/// Supports topic-per-instance architecture where each DMS instance publishes to its own
/// dedicated Kafka topic (e.g., edfi.dms.123.document, edfi.dms.456.document).
/// </summary>
public sealed class InstanceKafkaMessageCollector : IDisposable
{
    private readonly ConcurrentBag<KafkaTestMessage> _messages = [];
    private readonly IConsumer<string, string> _consumer;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _consumeTask;
    private readonly ILogger _logger;
    private readonly DateTime _collectionStartTime;
    private readonly InstanceKafkaTestConfiguration _configuration;
    private readonly string[] _topics;

    /// <summary>
    /// Initialize and begin collecting messages from Kafka for all specified instance topics.
    /// </summary>
    /// <param name="instanceIds">The DMS instance IDs to collect messages for</param>
    /// <param name="logger">The logger instance for diagnostic output</param>
    /// <param name="configuration">Optional Kafka configuration (uses defaults if not provided)</param>
    public InstanceKafkaMessageCollector(
        IEnumerable<long> instanceIds,
        ILogger logger,
        InstanceKafkaTestConfiguration? configuration = null
    )
    {
        _logger = logger;
        _configuration =
            configuration
            ?? new InstanceKafkaTestConfiguration
            {
                BootstrapServers =
                    Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092",
            };
        _collectionStartTime = DateTime.UtcNow;

        // Generate topic names for all instances
        _topics = _configuration.GetTopicNamesForInstances(instanceIds);

        if (_topics.Length == 0)
        {
            throw new ArgumentException("At least one instance ID must be provided", nameof(instanceIds));
        }

        ConsumerConfig config = new()
        {
            BootstrapServers = _configuration.BootstrapServers,
            GroupId = _configuration.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Latest, // Only collect messages after we start
            EnableAutoCommit = false,
            SessionTimeoutMs = 6000,
            HeartbeatIntervalMs = 2000,
            ClientDnsLookup = ClientDnsLookup.ResolveCanonicalBootstrapServersOnly,
        };

        _logger.LogInformation(
            "InstanceKafkaMessageCollector: Connecting to {BootstrapServers}",
            SanitizeForLog(config.BootstrapServers)
        );
        _logger.LogInformation(
            "InstanceKafkaMessageCollector: Consumer group: {ConsumerGroup}",
            SanitizeForLog(config.GroupId)
        );
        _logger.LogInformation(
            "InstanceKafkaMessageCollector: Topics: {Topics}",
            SanitizeForLog(string.Join(", ", _topics))
        );
        _logger.LogInformation(
            "InstanceKafkaMessageCollector: Collection started at: {StartTime} UTC",
            _collectionStartTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
        );

        _consumer = new ConsumerBuilder<string, string>(config).Build();
        _consumer.Subscribe(_topics);

        // Start the message collection
        _consumeTask = Task.Run(ConsumeMessages, _cancellationTokenSource.Token);

        // Wait briefly to ensure the consumer is ready and positioned at the latest offset
        var isReady = WaitForConsumerReadyAsync().Result;
        if (!isReady)
        {
            _logger.LogWarning("Consumer not fully ready but proceeding with collection");
        }

        _logger.LogDebug(
            "InstanceKafkaMessageCollector initialized for {TopicCount} topic(s)",
            _topics.Length
        );
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
    /// Gets all messages for a specific instance
    /// </summary>
    /// <param name="instanceId">The DMS instance ID</param>
    /// <returns>Messages from the instance-specific topic</returns>
    public IEnumerable<KafkaTestMessage> GetMessagesForInstance(long instanceId) =>
        _messages.Where(m => m.InstanceId == instanceId);

    /// <summary>
    /// Gets messages that were received after collection started, useful for filtering out pre-existing messages
    /// </summary>
    /// <param name="instanceId">Optional instance ID to filter by</param>
    /// <returns>Recent messages, optionally filtered by instance</returns>
    public IEnumerable<KafkaTestMessage> GetRecentMessages(long? instanceId = null)
    {
        var recentMessages = _messages.Where(m => m.Timestamp >= _collectionStartTime);

        return instanceId.HasValue
            ? recentMessages.Where(m => m.InstanceId == instanceId.Value)
            : recentMessages;
    }

    /// <summary>
    /// Gets messages grouped by instance ID
    /// </summary>
    /// <returns>Dictionary mapping instance IDs to their messages</returns>
    public Dictionary<long, List<KafkaTestMessage>> GetMessagesByInstance() =>
        KafkaTopicHelper.GroupMessagesByInstance(_messages);

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
    /// <param name="timeout">Optional timeout for waiting (defaults to 5 seconds)</param>
    /// <returns>True if consumer is ready, false if timeout occurred</returns>
    private async Task<bool> WaitForConsumerReadyAsync(TimeSpan? timeout = null)
    {
        var timeoutValue = timeout ?? TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;
        var lastLogTime = start;

        _logger.LogDebug(
            "Waiting for consumer readiness (timeout: {TimeoutSeconds}s)",
            timeoutValue.TotalSeconds
        );

        while (DateTime.UtcNow - start < timeoutValue)
        {
            List<TopicPartition> assignment = _consumer.Assignment;

            // Log progress every second
            if (DateTime.UtcNow - lastLogTime > TimeSpan.FromSeconds(1))
            {
                _logger.LogDebug(
                    "Consumer readiness check: {AssignedPartitions} assigned partition(s) after {ElapsedSeconds}s",
                    assignment.Count,
                    (DateTime.UtcNow - start).TotalSeconds
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
                        _logger.LogDebug(
                            "Partition {Topic}[{Partition}] ready at position {Position}",
                            SanitizeForLog(partition.Topic),
                            partition.Partition.Value,
                            position.Value
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(
                            "Partition {Topic}[{Partition}] not ready: {ErrorMessage}",
                            SanitizeForLog(partition.Topic),
                            partition.Partition.Value,
                            SanitizeForLog(ex.Message)
                        );
                        allPartitionsReady = false;
                        break;
                    }
                }

                if (allPartitionsReady)
                {
                    _logger.LogInformation(
                        "Consumer ready with {AssignedPartitions} assigned partition(s) after {ElapsedSeconds}s",
                        assignment.Count,
                        (DateTime.UtcNow - start).TotalSeconds
                    );
                    return true;
                }
            }

            await Task.Delay(50);
        }

        _logger.LogWarning(
            "Consumer readiness timeout after {ElapsedSeconds}s",
            (DateTime.UtcNow - start).TotalSeconds
        );
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
            _logger.LogInformation("Consumer diagnostics:");
            _logger.LogInformation("  Messages collected: {MessageCount}", _messages.Count);
            _logger.LogInformation("  Assigned partitions: {PartitionCount}", assignment.Count);

            var messagesByInstance = GetMessagesByInstance();
            foreach (var kvp in messagesByInstance)
            {
                _logger.LogInformation(
                    "  Instance {InstanceId}: {MessageCount} message(s)",
                    kvp.Key,
                    kvp.Value.Count
                );
            }

            foreach (TopicPartition partition in assignment)
            {
                try
                {
                    Offset position = _consumer.Position(partition);
                    _logger.LogInformation(
                        "    {Topic}[{Partition}]: position={Position}",
                        SanitizeForLog(partition.Topic),
                        partition.Partition.Value,
                        position.Value
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "    {Topic}[{Partition}]: failed to get position - {ErrorMessage}",
                        SanitizeForLog(partition.Topic),
                        partition.Partition.Value,
                        SanitizeForLog(ex.Message)
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to get consumer diagnostics: {ErrorMessage}",
                SanitizeForLog(ex.Message)
            );
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

                            // Extract instance ID from topic name
                            long? instanceId = _configuration.ParseInstanceIdFromTopic(consumeResult.Topic);

                            KafkaTestMessage message = new()
                            {
                                Topic = consumeResult.Topic,
                                InstanceId = instanceId,
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

                            _logger.LogDebug(
                                "Collected Kafka message from topic {Topic}, partition {Partition}, offset {Offset}, instance {InstanceId}",
                                SanitizeForLog(message.Topic),
                                message.Partition,
                                message.Offset,
                                message.InstanceId ?? -1
                            );
                            _logger.LogDebug(
                                "Message timing - Arrived: {Timestamp} UTC, Time since collection start: {TimeSinceStart}s, Processing delay: {ProcessingDelay}ms, Total messages collected: {TotalMessages}",
                                message.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                                timeSinceCollectionStart.TotalSeconds,
                                processingDelay.TotalMilliseconds,
                                _messages.Count
                            );
                        }
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError("Kafka consume error: {ErrorReason}", SanitizeForLog(ex.Error.Reason));
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Kafka message consumption cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "Unexpected error in Kafka message consumption: {Exception}",
                        SanitizeForLog(ex.ToString())
                    );
                }
            },
            _cancellationTokenSource.Token
        );
    }

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
    /// Allows: letters, digits, spaces, and safe punctuation (_-.:/)
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }
        return new string(
            input
                .Where(c =>
                    char.IsLetterOrDigit(c)
                    || c == ' '
                    || c == '_'
                    || c == '-'
                    || c == '.'
                    || c == ':'
                    || c == '/'
                )
                .ToArray()
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

        _logger.LogDebug("InstanceKafkaMessageCollector disposed");
    }
}

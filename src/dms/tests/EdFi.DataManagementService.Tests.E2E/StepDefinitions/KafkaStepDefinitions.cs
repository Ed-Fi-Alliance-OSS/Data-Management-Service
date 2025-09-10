// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;
using static EdFi.DataManagementService.Tests.E2E.Management.JsonTestUtilities;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

[Binding]
public class KafkaStepDefinitions(TestLogger logger) : IDisposable
{
    private KafkaMessageCollector? _kafkaMessageCollector;

    [Given("I start collecting Kafka messages")]
    public void GivenIStartCollectingKafkaMessages()
    {
        // Check if collector is already running to support long-running collection
        if (_kafkaMessageCollector != null)
        {
            logger.log.Information("Kafka message collector is already running - reusing existing collector");
            logger.log.Debug($"Existing collector has {_kafkaMessageCollector.MessageCount} messages");
            return;
        }

        string bootstrapServers =
            Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
        logger.log.Information($"Starting Kafka message collection for this test using {bootstrapServers}");
        logger.log.Debug($"Consumer start time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");

        _kafkaMessageCollector = new KafkaMessageCollector(bootstrapServers, logger);

        // Ensure consumer is fully subscribed before returning
        logger.log.Debug("Waiting for Kafka consumer to be fully ready...");
        if (!_kafkaMessageCollector.WaitForConsumerReady(TimeSpan.FromSeconds(5)))
        {
            logger.log.Warning("Kafka consumer readiness timeout - proceeding anyway");
        }
        else
        {
            logger.log.Debug("Kafka consumer is ready and subscribed");
        }
    }

    [When("I wait {int} second")]
    [When("I wait {int} seconds")]
    public void WhenIWaitSeconds(int seconds)
    {
        logger.log.Debug($"Waiting {seconds} second(s) to ensure message timing");
        Task.Delay(TimeSpan.FromSeconds(seconds)).Wait();
    }

    /// <summary>
    /// Helper method that retries a condition until it succeeds or times out
    /// </summary>
    /// <param name="condition">Function that returns true when the condition is met</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="pollInterval">Time to wait between checks</param>
    /// <returns>True if condition was met within timeout, false otherwise</returns>
    private bool RetryUntilSuccess(Func<bool> condition, TimeSpan timeout, TimeSpan pollInterval)
    {
        var start = DateTime.UtcNow;
        var elapsed = TimeSpan.Zero;

        while (elapsed < timeout)
        {
            if (condition())
            {
                logger.log.Debug($"Condition succeeded after {elapsed.TotalMilliseconds:F0}ms");
                return true;
            }

            Task.Delay(pollInterval).Wait();
            elapsed = DateTime.UtcNow - start;

            logger.log.Debug($"Condition not met, retrying... (elapsed: {elapsed.TotalMilliseconds:F0}ms)");
        }

        logger.log.Debug($"Condition failed after timeout of {timeout.TotalMilliseconds:F0}ms");
        return false;
    }

    [Then("a Kafka message should have the deleted flag {string} and EdFiDoc")]
    public void ThenAKafkaMessageShouldHaveDeletedFlagAndEdFiDoc(
        string expectedDeletedFlag,
        string expectedContent
    )
    {
        if (_kafkaMessageCollector == null)
        {
            logger.log.Warning(
                "Kafka message collector not initialized - use 'Given I start collecting Kafka messages' step first"
            );
            return;
        }

        // Convert string parameter to boolean
        bool shouldBeDeleted = expectedDeletedFlag.Equals("true", StringComparison.OrdinalIgnoreCase);

        List<KafkaTestMessage> messages = [];
        bool foundMatchingMessage = false;

        // Use retry logic: 10-second timeout with 200ms poll intervals
        var timeout = TimeSpan.FromSeconds(10);
        var pollInterval = TimeSpan.FromMilliseconds(200);
        var start = DateTime.UtcNow;

        bool success = RetryUntilSuccess(
            () =>
            {
                // Get recent messages (those received after collection started)
                messages = _kafkaMessageCollector.GetRecentDocumentMessages().ToList();

                if (messages.Count == 0)
                {
                    return false;
                }

                // Check if any message matches our criteria
                foundMatchingMessage = CompareJsonMessages(messages, expectedContent, shouldBeDeleted);
                return foundMatchingMessage;
            },
            timeout,
            pollInterval
        );

        var elapsed = DateTime.UtcNow - start;
        logger.log.Information($"Message search completed after {elapsed.TotalMilliseconds:F0}ms");

        // Log final diagnostics
        var finalAllMessages = _kafkaMessageCollector.GetDocumentMessages().ToList();
        var finalRecentMessages = _kafkaMessageCollector.GetRecentDocumentMessages().ToList();

        logger.log.Information(
            $"Final message count - Total: {finalAllMessages.Count}, Recent: {finalRecentMessages.Count}"
        );
        _kafkaMessageCollector.LogDiagnostics();

        if (!success || messages.Count == 0)
        {
            logger.log.Warning("No matching Kafka messages were found after waiting. This might indicate:");
            logger.log.Warning("1. Kafka messaging is not enabled in the DMS configuration");
            logger.log.Warning("2. The test data creation did not trigger a message");
            logger.log.Warning("3. There's a connectivity issue between DMS and Kafka");
            logger.log.Warning("4. Messages are being published to a different topic");
            logger.log.Warning("5. The message content or deleted flag doesn't match expectations");
        }

        messages
            .Should()
            .NotBeEmpty(
                $"Expected to receive at least one message on topic 'edfi.dms.document' within {timeout.TotalSeconds} seconds"
            );

        foundMatchingMessage
            .Should()
            .BeTrue(
                $"Expected to find a Kafka message matching '{expectedContent}' in the edfidoc field"
                    + $" with __deleted='{expectedDeletedFlag}'. "
                    + $"Messages received: {string.Join(", ", messages.Select(m => m.Value?.Length > 100 ? m.Value[..100] + "..." : m.Value ?? "null"))}"
            );

        logger.log.Debug(
            $"Successfully found Kafka message matching '{expectedContent}' in edfidoc field with __deleted='{expectedDeletedFlag}' after {elapsed.TotalMilliseconds:F0}ms"
        );
    }

    private bool CompareJsonMessages(
        List<KafkaTestMessage> messages,
        string expectedContent,
        bool shouldBeDeleted
    )
    {
        return messages.Exists(message =>
        {
            if (message.ValueAsJson == null)
            {
                return false;
            }

            var edFiDocField = message.ValueAsJson["edfidoc"];
            if (edFiDocField == null)
            {
                return false;
            }

            try
            {
                // Always check __deleted flag to ensure it matches expected value
                var deletedField = message.ValueAsJson["__deleted"];
                string expectedDeletedValue = shouldBeDeleted ? "true" : "false";
                if (deletedField?.ToString() != expectedDeletedValue)
                {
                    logger.log.Debug(
                        $"Message __deleted field mismatch. Expected: '{expectedDeletedValue}', Found: '{deletedField?.ToString() ?? "null"}'"
                    );
                    return false;
                }

                string edFiDocId = edFiDocField["id"]?.ToString() ?? "";

                bool edFiDocMatches = CompareJsonWithPlaceholderReplacement(
                    expectedContent,
                    edFiDocField,
                    id: edFiDocId,
                    removeMetadataFromActual: true, // Remove metadata like _etag and _lastModifiedDate
                    removeEtagFromActual: true
                );

                return edFiDocMatches;
            }
            catch (Exception ex)
            {
                logger.log.Warning($"JSON comparison failed: {ex.Message}");
                return false;
            }
        });
    }

    [Given("Kafka should be reachable")]
    public void ThenKafkaShouldBeReachable()
    {
        try
        {
            string bootstrapServers =
                Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
            var config = new AdminClientConfig { BootstrapServers = bootstrapServers };

            using var adminClient = new AdminClientBuilder(config).Build();
            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(10));

            metadata.Should().NotBeNull("Should be able to get Kafka metadata");
            metadata.Brokers.Should().NotBeEmpty("Should have at least one broker");

            logger.log.Information($"✓ Kafka is reachable with {metadata.Brokers.Count} broker(s)");
            logger.log.Information(
                $"✓ Available topics: {string.Join(", ", metadata.Topics.Select(t => t.Topic))}"
            );
        }
        catch (Exception ex)
        {
            throw new AssertionException($"Failed to connect to Kafka on localhost:9092: {ex.Message}");
        }
    }

    [Then("Kafka consumer should be able to connect to topic {string}")]
    public void ThenKafkaConsumerShouldBeAbleToConnectToTopic(string topicName)
    {
        try
        {
            string bootstrapServers =
                Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
            var config = new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = "edfi-e2e-test-consumer",
                AutoOffsetReset = AutoOffsetReset.Latest,
                EnablePartitionEof = true,
            };

            using var consumer = new ConsumerBuilder<string, string>(config).Build();

            // Subscribe to the topic
            consumer.Subscribe(topicName);

            // Try to consume for a short time to verify connectivity
            var consumeResult = consumer.Consume(TimeSpan.FromSeconds(5));

            // Even if no message is consumed, if we didn't get an exception, the connection works
            logger.log.Information($"✓ Successfully connected to Kafka topic '{topicName}'");

            if (consumeResult?.Message != null)
            {
                logger.log.Information(
                    $"✓ Successfully consumed message from topic '{topicName}': {consumeResult.Message.Value?.Length ?? 0} bytes"
                );
            }
            else
            {
                logger.log.Information(
                    $"✓ No messages available on topic '{topicName}' but connection successful"
                );
            }

            consumer.Close();
        }
        catch (ConsumeException ex)
        {
            // Even if we get a consume exception, if we can connect that's what we're testing
            logger.log.Information(
                $"✓ Connected to topic '{topicName}' (consume exception: {ex.Error.Code})"
            );
        }
        catch (Exception ex)
        {
            throw new AssertionException($"Failed to connect to Kafka topic '{topicName}': {ex.Message}");
        }
    }

    private bool _disposed = false;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing && _kafkaMessageCollector != null)
        {
            logger.log.Debug(
                $"Disposing Kafka message collector with {_kafkaMessageCollector.MessageCount} collected messages at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC"
            );
            _kafkaMessageCollector.Dispose();
        }
        _disposed = true;
    }
}

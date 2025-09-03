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
        string bootstrapServers =
            Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
        logger.log.Information($"Starting Kafka message collection for this test using {bootstrapServers}");
        _kafkaMessageCollector = new KafkaMessageCollector(bootstrapServers, logger);

        // Small delay to ensure consumer is fully ready and positioned at latest offset
        Thread.Sleep(200);
    }

    [Then("a Kafka message received on topic {string} should contain in the edfidoc field")]
    public void ThenAKafkaMessageReceivedOnTopicShouldContainInTheEdfidocField(
        string expectedTopic,
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

        // Wait up to 10 seconds for messages to arrive, checking every 500ms
        var timeout = TimeSpan.FromSeconds(10);
        var start = DateTime.UtcNow;
        var checkInterval = TimeSpan.FromMilliseconds(500);

        List<KafkaTestMessage> messages = [];

        while (DateTime.UtcNow - start < timeout && messages.Count == 0)
        {
            // Get recent messages (those received after collection started)
            messages = _kafkaMessageCollector.GetRecentDocumentMessages().ToList();

            if (messages.Count > 0)
            {
                logger.log.Information($"Found {messages.Count} recent messages");
                break;
            }

            logger.log.Debug($"No recent messages yet, waiting...");

            // Wait before checking again
            Thread.Sleep((int)checkInterval.TotalMilliseconds);
        }

        // Log final diagnostics
        var finalAllMessages = _kafkaMessageCollector.GetDocumentMessages().ToList();
        var finalRecentMessages = _kafkaMessageCollector.GetRecentDocumentMessages().ToList();

        logger.log.Information(
            $"Final message count - Total: {finalAllMessages.Count}, Recent: {finalRecentMessages.Count}"
        );
        _kafkaMessageCollector.LogDiagnostics();

        if (messages.Count == 0)
        {
            logger.log.Warning("No Kafka messages were found after waiting. This might indicate:");
            logger.log.Warning("1. Kafka messaging is not enabled in the DMS configuration");
            logger.log.Warning("2. The test data creation did not trigger a message");
            logger.log.Warning("3. There's a connectivity issue between DMS and Kafka");
            logger.log.Warning("4. Messages are being published to a different topic");
        }

        messages
            .Should()
            .NotBeEmpty(
                $"Expected to receive at least one message on topic '{expectedTopic}' within {timeout.TotalSeconds} seconds"
            );

        // Do full JSON comparison
        bool foundMessage = CompareJsonMessages(messages, expectedContent);

        foundMessage
            .Should()
            .BeTrue(
                $"Expected to find a Kafka message on topic '{expectedTopic}' matching '{expectedContent}' in the edfidoc field. "
                    + $"Messages received: {string.Join(", ", messages.Select(m => m.Value?.Length > 100 ? m.Value[..100] + "..." : m.Value ?? "null"))}"
            );

        logger.log.Debug(
            $"Successfully found Kafka message on topic '{expectedTopic}' matching '{expectedContent}' in edfidoc field"
        );
    }

    private bool CompareJsonMessages(List<KafkaTestMessage> messages, string expectedContent)
    {
        return messages.Exists(message =>
        {
            if (message.ValueAsJson == null)
            {
                return false;
            }

            var edfidocField = message.ValueAsJson["edfidoc"];
            if (edfidocField == null)
            {
                return false;
            }

            try
            {
                string edfidocId = edfidocField["id"]?.ToString() ?? "";

                return CompareJsonWithPlaceholderReplacement(
                    expectedContent,
                    edfidocField,
                    id: edfidocId,
                    removeMetadataFromActual: true, // Remove metadata like _etag and _lastModifiedDate
                    removeEtagFromActual: true
                );
            }
            catch (Exception ex)
            {
                logger.log.Warning($"JSON comparison failed: {ex.Message}");
                return false;
            }
        });
    }

    [Given("Kafka should be reachable on localhost:9092")]
    public void ThenKafkaShouldBeReachableOnLocalhost9092()
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

        if (disposing)
        {
            _kafkaMessageCollector?.Dispose();
        }
        _disposed = true;
    }
}

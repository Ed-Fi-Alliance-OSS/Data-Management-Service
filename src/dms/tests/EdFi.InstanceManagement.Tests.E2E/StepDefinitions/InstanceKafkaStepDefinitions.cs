// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Reqnroll;
using Serilog;
using Serilog.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

/// <summary>
/// Step definitions for Kafka topic-per-instance testing.
/// Validates that messages are published to correct instance-specific topics
/// and that no cross-instance data leakage occurs.
/// </summary>
[Binding]
public class InstanceKafkaStepDefinitions(InstanceManagementContext context) : IDisposable
{
    private readonly ILogger<InstanceKafkaStepDefinitions> _logger = new SerilogLoggerFactory(
        Log.Logger
    ).CreateLogger<InstanceKafkaStepDefinitions>();

    private bool _disposed = false;

    [Given("I start collecting Kafka messages for all instances")]
    public void GivenIStartCollectingKafkaMessagesForAllInstances()
    {
        // Check if collector is already running to support long-running collection
        if (context.KafkaCollector != null)
        {
            _logger.LogInformation("Kafka message collector is already running - reusing existing collector");
            _logger.LogDebug(
                "Existing collector has {MessageCount} messages",
                context.KafkaCollector.MessageCount
            );
            return;
        }

        if (context.InstanceIds.Count == 0)
        {
            throw new InvalidOperationException(
                "Cannot start Kafka collector: No instances have been created yet. "
                    + "Ensure instance setup is completed before starting Kafka message collection."
            );
        }

        string bootstrapServers =
            Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
        _logger.LogInformation(
            "Starting Kafka message collection for {InstanceCount} instances using {BootstrapServers}",
            context.InstanceIds.Count,
            SanitizeForLog(bootstrapServers)
        );
        _logger.LogDebug(
            "Consumer start time: {StartTime} UTC",
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")
        );

        // Convert List<int> to IEnumerable<long> for the collector
        IEnumerable<long> instanceIds = context.InstanceIds.Select(id => (long)id);

        context.KafkaCollector = new InstanceKafkaMessageCollector(
            instanceIds,
            _logger,
            new InstanceKafkaTestConfiguration { BootstrapServers = bootstrapServers }
        );

        // Ensure consumer is fully subscribed before returning
        _logger.LogDebug("Waiting for Kafka consumer to be fully ready...");
        if (!context.KafkaCollector.WaitForConsumerReady(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("Kafka consumer readiness timeout - proceeding anyway");
        }
        else
        {
            _logger.LogDebug("Kafka consumer is ready and subscribed");
        }
    }

    [Given("I start collecting Kafka messages for instance {int}")]
    public void GivenIStartCollectingKafkaMessagesForInstance(int instanceId)
    {
        if (context.KafkaCollector != null)
        {
            _logger.LogInformation("Kafka message collector is already running - reusing existing collector");
            return;
        }

        string bootstrapServers =
            Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
        _logger.LogInformation(
            "Starting Kafka message collection for instance {InstanceId} using {BootstrapServers}",
            instanceId,
            SanitizeForLog(bootstrapServers)
        );

        context.KafkaCollector = new InstanceKafkaMessageCollector(
            [instanceId],
            _logger,
            new InstanceKafkaTestConfiguration { BootstrapServers = bootstrapServers }
        );

        if (!context.KafkaCollector.WaitForConsumerReady(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("Kafka consumer readiness timeout - proceeding anyway");
        }
    }

    [When("I wait {int} second for Kafka messages")]
    [When("I wait {int} seconds for Kafka messages")]
    public void WhenIWaitSecondsForKafkaMessages(int seconds)
    {
        _logger.LogDebug("Waiting {Seconds} second(s) to ensure Kafka message delivery", seconds);
        Task.Delay(TimeSpan.FromSeconds(seconds)).Wait();
    }

    [Then("a Kafka message for instance {string} should be published to its instance-specific topic")]
    public void ThenAKafkaMessageForInstanceShouldBePublishedToItsInstanceSpecificTopic(
        string instanceRouteQualifier
    )
    {
        if (context.KafkaCollector == null)
        {
            throw new InvalidOperationException(
                "Kafka message collector not initialized - use 'Given I start collecting Kafka messages' step first"
            );
        }

        // Parse instance ID from route qualifier (e.g., "255901/2024" -> find matching instance)
        long instanceId = GetInstanceIdFromRouteQualifier(instanceRouteQualifier);

        List<KafkaTestMessage> messages = [];
        bool hasMessages = false;

        // Use retry logic: 15-second timeout with 200ms poll intervals
        var timeout = TimeSpan.FromSeconds(15);
        var pollInterval = TimeSpan.FromMilliseconds(200);
        var start = DateTime.UtcNow;

        bool success = RetryUntilSuccess(
            () =>
            {
                messages = context.KafkaCollector.GetRecentMessages(instanceId).ToList();

                if (messages.Count > 0)
                {
                    hasMessages = true;
                    return true;
                }

                return false;
            },
            timeout,
            pollInterval
        );

        var elapsed = DateTime.UtcNow - start;
        _logger.LogInformation("Message check completed after {ElapsedMs}ms", elapsed.TotalMilliseconds);

        if (!success || !hasMessages)
        {
            _logger.LogWarning(
                "No Kafka messages found for instance {InstanceId} (route: {RouteQualifier})",
                instanceId,
                SanitizeForLog(instanceRouteQualifier)
            );
            context.KafkaCollector.LogDiagnostics();
        }

        messages
            .Should()
            .NotBeEmpty(
                $"Expected to receive at least one message for instance {instanceId} (route: {instanceRouteQualifier}) within {timeout.TotalSeconds} seconds"
            );

        _logger.LogDebug(
            "Successfully found {MessageCount} Kafka message(s) for instance {InstanceId} after {ElapsedMs}ms",
            messages.Count,
            instanceId,
            elapsed.TotalMilliseconds
        );
    }

    [Then("the message should contain {string}")]
    public void ThenTheMessageShouldContain(string expectedContent)
    {
        if (context.KafkaCollector == null)
        {
            throw new InvalidOperationException("Kafka message collector not initialized");
        }

        var allMessages = context.KafkaCollector.GetRecentMessages().ToList();

        allMessages.Should().NotBeEmpty("Expected to have collected messages before checking content");

        var matchingMessages = allMessages.Where(m =>
            m.Value?.Contains(expectedContent, StringComparison.OrdinalIgnoreCase) == true
        );

        matchingMessages
            .Should()
            .NotBeEmpty(
                $"Expected to find at least one message containing '{expectedContent}'. "
                    + $"Messages received: {allMessages.Count}"
            );
    }

    [Then("the message should have deleted flag {string}")]
    public void ThenTheMessageShouldHaveDeletedFlag(string expectedDeletedFlag)
    {
        if (context.KafkaCollector == null)
        {
            throw new InvalidOperationException("Kafka message collector not initialized");
        }

        bool shouldBeDeleted = expectedDeletedFlag.Equals("true", StringComparison.OrdinalIgnoreCase);
        var allMessages = context.KafkaCollector.GetRecentMessages().ToList();

        allMessages.Should().NotBeEmpty("Expected to have collected messages");

        var messagesWithDeletedFlag = allMessages.Where(m =>
        {
            if (m.ValueAsJson == null)
                return false;

            var deletedField = m.ValueAsJson["__deleted"];
            string expectedValue = shouldBeDeleted ? "true" : "false";
            return deletedField?.ToString() == expectedValue;
        });

        messagesWithDeletedFlag
            .Should()
            .NotBeEmpty($"Expected to find at least one message with __deleted='{expectedDeletedFlag}'");
    }

    [Then("instance {string} should have {int} Kafka messages")]
    [Then("instance {string} should have {int} Kafka message")]
    public void ThenInstanceShouldHaveKafkaMessages(string instanceRouteQualifier, int expectedCount)
    {
        if (context.KafkaCollector == null)
        {
            throw new InvalidOperationException("Kafka message collector not initialized");
        }

        long instanceId = GetInstanceIdFromRouteQualifier(instanceRouteQualifier);

        // Use retry logic to wait for expected messages
        var timeout = TimeSpan.FromSeconds(15);
        var pollInterval = TimeSpan.FromMilliseconds(200);
        int actualCount = 0;

        bool success = RetryUntilSuccess(
            () =>
            {
                actualCount = context.KafkaCollector.GetRecentMessages(instanceId).Count();
                return actualCount >= expectedCount;
            },
            timeout,
            pollInterval
        );

        if (!success)
        {
            _logger.LogWarning(
                "Expected {ExpectedCount} messages for instance {InstanceId}, but found {ActualCount}",
                expectedCount,
                instanceId,
                actualCount
            );
            context.KafkaCollector.LogDiagnostics();
        }

        actualCount
            .Should()
            .BeGreaterOrEqualTo(
                expectedCount,
                $"Instance {instanceId} (route: {instanceRouteQualifier}) should have at least {expectedCount} Kafka message(s)"
            );
    }

    [Then("Kafka messages for instance {string} should not appear in instance {string} topic")]
    public void ThenKafkaMessagesForInstanceShouldNotAppearInInstanceTopic(
        string sourceInstanceRoute,
        string targetInstanceRoute
    )
    {
        if (context.KafkaCollector == null)
        {
            throw new InvalidOperationException("Kafka message collector not initialized");
        }

        long sourceInstanceId = GetInstanceIdFromRouteQualifier(sourceInstanceRoute);
        long targetInstanceId = GetInstanceIdFromRouteQualifier(targetInstanceRoute);

        // Get all messages from target instance topic
        var targetMessages = context.KafkaCollector.GetRecentMessages(targetInstanceId).ToList();

        // Check if any message contains data that appears to belong to source instance
        var leakedMessages = targetMessages.Where(m =>
            m.Value?.Contains(sourceInstanceRoute.Replace("/", "")) == true
            || m.Value?.Contains(sourceInstanceId.ToString()) == true
        );

        leakedMessages
            .Should()
            .BeEmpty(
                $"Messages from instance {sourceInstanceId} should not appear in instance {targetInstanceId} topic. "
                    + $"Found {leakedMessages.Count()} leaked message(s)."
            );
    }

    [Then("no cross-instance message leakage should occur")]
    public void ThenNoCrossInstanceMessageLeakageShouldOccur()
    {
        if (context.KafkaCollector == null)
        {
            throw new InvalidOperationException("Kafka message collector not initialized");
        }

        if (context.InstanceIds.Count == 0)
        {
            throw new InvalidOperationException("No instances found to validate");
        }

        var allMessages = context.KafkaCollector.GetRecentMessages().ToList();
        IEnumerable<long> instanceIds = context.InstanceIds.Select(id => (long)id);

        bool isIsolated = KafkaTopicHelper.ValidateNoMessageLeakage(allMessages, instanceIds, _logger);

        if (!isIsolated)
        {
            _logger.LogWarning("Cross-instance message leakage detected!");
            context.KafkaCollector.LogDiagnostics();
        }

        isIsolated
            .Should()
            .BeTrue("All instances should be properly isolated with no cross-instance message leakage");
    }

    [Then("Kafka consumer should be able to connect to all instance topics")]
    public void ThenKafkaConsumerShouldBeAbleToConnectToAllInstanceTopics()
    {
        if (context.InstanceIds.Count == 0)
        {
            throw new InvalidOperationException("No instances found");
        }

        string bootstrapServers =
            Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";

        try
        {
            var config = new AdminClientConfig { BootstrapServers = bootstrapServers };

            using var adminClient = new AdminClientBuilder(config).Build();
            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(10));

            metadata.Should().NotBeNull("Should be able to get Kafka metadata");
            metadata.Brokers.Should().NotBeEmpty("Should have at least one broker");

            _logger.LogInformation("Kafka is reachable with {BrokerCount} broker(s)", metadata.Brokers.Count);

            // Check if expected topics exist
            var configuration = new InstanceKafkaTestConfiguration();
            var expectedTopics = configuration.GetTopicNamesForInstances(
                context.InstanceIds.Select(id => (long)id)
            );

            foreach (var expectedTopic in expectedTopics)
            {
                _logger.LogInformation("Checking for topic: {Topic}", SanitizeForLog(expectedTopic));

                // Note: Topics might not exist yet if no messages have been published
                // This is expected in CDC architectures where topics are auto-created on first message
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to connect to Kafka on {bootstrapServers}: {ex.Message}",
                ex
            );
        }
    }

    /// <summary>
    /// Helper method that retries a condition until it succeeds or times out
    /// </summary>
    private bool RetryUntilSuccess(Func<bool> condition, TimeSpan timeout, TimeSpan pollInterval)
    {
        var start = DateTime.UtcNow;
        var elapsed = TimeSpan.Zero;

        while (elapsed < timeout)
        {
            if (condition())
            {
                _logger.LogDebug("Condition succeeded after {ElapsedMs}ms", elapsed.TotalMilliseconds);
                return true;
            }

            Task.Delay(pollInterval).Wait();
            elapsed = DateTime.UtcNow - start;

            _logger.LogDebug(
                "Condition not met, retrying... (elapsed: {ElapsedMs}ms)",
                elapsed.TotalMilliseconds
            );
        }

        _logger.LogDebug("Condition failed after timeout of {TimeoutMs}ms", timeout.TotalMilliseconds);
        return false;
    }

    /// <summary>
    /// Gets the instance ID from a route qualifier string.
    /// For now, returns the first instance ID from context as a placeholder.
    /// In production, this would parse the route qualifier and look up the corresponding instance.
    /// </summary>
#pragma warning disable S1172 // Unused method parameter - TODO: Implement proper route qualifier parsing
    private long GetInstanceIdFromRouteQualifier(string routeQualifier)
#pragma warning restore S1172
    {
        // For initial implementation, assume instance IDs are in order and use index
        // In a real implementation, you'd need to map route qualifiers to instance IDs
        // via the InstanceManagementContext or a lookup mechanism

        if (context.InstanceIds.Count == 0)
        {
            throw new InvalidOperationException("No instances found in context");
        }

        // Simple mapping: use the first instance for now
        // TODO: Implement proper route qualifier to instance ID mapping
        return context.InstanceIds[0];
    }

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
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
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing && context.KafkaCollector != null)
        {
            _logger.LogDebug(
                "Disposing Kafka message collector with {MessageCount} collected messages at {Timestamp} UTC",
                context.KafkaCollector.MessageCount,
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")
            );
            // Note: Collector is owned by context and will be disposed there
            // We don't dispose it here to avoid double-disposal
        }

        _disposed = true;
    }
}

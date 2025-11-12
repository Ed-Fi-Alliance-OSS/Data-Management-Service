// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Configuration for Kafka testing in the Instance Management E2E tests.
/// Supports topic-per-instance architecture where each DMS instance publishes
/// to its own dedicated Kafka topic.
/// </summary>
public class InstanceKafkaTestConfiguration
{
    /// <summary>
    /// Kafka bootstrap servers connection string.
    /// Can be overridden via KAFKA_BOOTSTRAP_SERVERS environment variable.
    /// </summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>
    /// Consumer group ID for E2E test Kafka consumers.
    /// Each test run gets a unique group ID to avoid conflicts.
    /// </summary>
    public string ConsumerGroupId { get; set; } =
        $"instance-mgmt-e2e-tests-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// Topic prefix for instance-specific topics.
    /// Topics are named: {TopicPrefix}.{InstanceId}.document
    /// Example: edfi.dms.123.document
    /// </summary>
    public string TopicPrefix { get; set; } = "edfi.dms";

    /// <summary>
    /// Topic suffix for document messages.
    /// Combined with prefix and instance ID to form full topic name.
    /// </summary>
    public string TopicSuffix { get; set; } = "document";

    /// <summary>
    /// Whether Kafka testing is enabled.
    /// Can be set to false to skip Kafka tests in environments where Kafka is unavailable.
    /// </summary>
    public static bool IsEnabled => true;

    /// <summary>
    /// Gets the topic name for a specific DMS instance.
    /// </summary>
    /// <param name="instanceId">The DMS instance ID</param>
    /// <returns>The Kafka topic name (e.g., "edfi.dms.123.document")</returns>
    public string GetTopicNameForInstance(long instanceId) => $"{TopicPrefix}.{instanceId}.{TopicSuffix}";

    /// <summary>
    /// Gets all topic names for a collection of instance IDs.
    /// </summary>
    /// <param name="instanceIds">The collection of DMS instance IDs</param>
    /// <returns>Array of Kafka topic names</returns>
    public string[] GetTopicNamesForInstances(IEnumerable<long> instanceIds) =>
        instanceIds.Select(GetTopicNameForInstance).ToArray();

    /// <summary>
    /// Parses an instance ID from a topic name.
    /// </summary>
    /// <param name="topicName">The Kafka topic name (e.g., "edfi.dms.123.document")</param>
    /// <returns>The instance ID if successfully parsed, null otherwise</returns>
    public long? ParseInstanceIdFromTopic(string topicName)
    {
        if (string.IsNullOrWhiteSpace(topicName))
        {
            return null;
        }

        var expectedPrefix = $"{TopicPrefix}.";
        var expectedSuffix = $".{TopicSuffix}";

        if (!topicName.StartsWith(expectedPrefix) || !topicName.EndsWith(expectedSuffix))
        {
            return null;
        }

        var instanceIdPart = topicName[expectedPrefix.Length..^expectedSuffix.Length];

        return long.TryParse(instanceIdPart, out long instanceId) ? instanceId : null;
    }
}

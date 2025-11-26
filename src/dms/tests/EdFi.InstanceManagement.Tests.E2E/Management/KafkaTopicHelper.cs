// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Helper utilities for Kafka topic naming, validation, and isolation testing
/// in the Instance Management E2E tests.
/// </summary>
public static class KafkaTopicHelper
{
    /// <summary>
    /// Validates that messages for a specific instance only appear on that instance's topic
    /// and do not leak to other instance topics.
    /// </summary>
    /// <param name="allMessages">All collected Kafka messages across all topics</param>
    /// <param name="targetInstanceId">The instance ID to validate isolation for</param>
    /// <param name="logger">Logger for diagnostic output</param>
    /// <returns>Validation result with details about any violations found</returns>
    public static TopicIsolationValidationResult ValidateTopicIsolation(
        IEnumerable<KafkaTestMessage> allMessages,
        long targetInstanceId,
        ILogger? logger = null
    )
    {
        var messagesList = allMessages.ToList();
        var targetInstanceMessages = messagesList.Where(m => m.InstanceId == targetInstanceId).ToList();

        var leakedMessages = messagesList
            .Where(m => m.InstanceId != targetInstanceId && ContainsInstanceSpecificData(m, targetInstanceId))
            .ToList();

        var result = new TopicIsolationValidationResult
        {
            TargetInstanceId = targetInstanceId,
            MessageCountForTargetInstance = targetInstanceMessages.Count,
            LeakedMessages = leakedMessages,
            IsIsolated = !leakedMessages.Any(),
        };

        if (!result.IsIsolated && logger != null)
        {
            logger.LogWarning(
                "Topic isolation violation detected for instance {InstanceId}",
                SanitizeForLog(targetInstanceId.ToString())
            );
            foreach (var leaked in leakedMessages)
            {
                logger.LogWarning(
                    "  Message found on topic {Topic} (instance {FoundInstanceId}) that appears to contain data for instance {ExpectedInstanceId}",
                    SanitizeForLog(leaked.Topic),
                    leaked.InstanceId,
                    targetInstanceId
                );
            }
        }

        return result;
    }

    /// <summary>
    /// Validates that no cross-instance message leakage occurs across all instances.
    /// </summary>
    /// <param name="allMessages">All collected Kafka messages across all topics</param>
    /// <param name="instanceIds">All instance IDs being tested</param>
    /// <param name="logger">Logger for diagnostic output</param>
    /// <returns>True if all instances are properly isolated, false otherwise</returns>
    public static bool ValidateNoMessageLeakage(
        IEnumerable<KafkaTestMessage> allMessages,
        IEnumerable<long> instanceIds,
        ILogger? logger = null
    )
    {
        var allInstancesIsolated = true;

        foreach (long instanceId in instanceIds)
        {
            var result = ValidateTopicIsolation(allMessages, instanceId, logger);
            if (!result.IsIsolated)
            {
                allInstancesIsolated = false;
            }
        }

        return allInstancesIsolated;
    }

    /// <summary>
    /// Groups messages by their instance ID for analysis.
    /// </summary>
    /// <param name="messages">Collection of Kafka messages</param>
    /// <returns>Dictionary mapping instance IDs to their messages</returns>
    public static Dictionary<long, List<KafkaTestMessage>> GroupMessagesByInstance(
        IEnumerable<KafkaTestMessage> messages
    )
    {
        return messages
            .Where(m => m.InstanceId.HasValue)
            .GroupBy(m => m.InstanceId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Attempts to detect if a message contains data that appears to belong to a specific instance.
    /// Uses instance-specific test data markers (districtId and schoolYear combinations)
    /// to accurately identify which instance's data is in the message.
    /// </summary>
    private static bool ContainsInstanceSpecificData(KafkaTestMessage message, long instanceId)
    {
        // Map instance IDs to their unique test data identifiers
        // Based on the E2E test setup: each instance uses a unique codeValue pattern
        // that combines district and year (e.g., "District255901-2024")
        var instanceDataMarkers = new Dictionary<long, string>
        {
            { 1, "District255901-2024" }, // Instance 1: District 255901 - School Year 2024
            { 2, "District255901-2025" }, // Instance 2: District 255901 - School Year 2025
            { 3, "District255902-2024" }, // Instance 3: District 255902 - School Year 2024
        };

        if (!instanceDataMarkers.TryGetValue(instanceId, out var marker))
        {
            return false;
        }

        var messageContent = message.Value ?? string.Empty;

        // Check if the message contains the instance-specific marker pattern
        // This avoids false positives from timestamp fields that might contain
        // the current year (e.g., "2025-11-21" in Kafka message metadata)
        return messageContent.Contains(marker, StringComparison.OrdinalIgnoreCase);
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
}

/// <summary>
/// Result of topic isolation validation for a specific instance.
/// </summary>
public class TopicIsolationValidationResult
{
    /// <summary>
    /// The instance ID that was validated.
    /// </summary>
    public long TargetInstanceId { get; init; }

    /// <summary>
    /// Number of messages found on the correct topic for this instance.
    /// </summary>
    public int MessageCountForTargetInstance { get; init; }

    /// <summary>
    /// Messages that leaked to other instance topics (should be empty for proper isolation).
    /// </summary>
    public List<KafkaTestMessage> LeakedMessages { get; init; } = [];

    /// <summary>
    /// Whether the instance is properly isolated (no leaked messages).
    /// </summary>
    public bool IsIsolated { get; init; }

    /// <summary>
    /// Number of messages that leaked to other topics.
    /// </summary>
    public int LeakedMessageCount => LeakedMessages.Count;
}

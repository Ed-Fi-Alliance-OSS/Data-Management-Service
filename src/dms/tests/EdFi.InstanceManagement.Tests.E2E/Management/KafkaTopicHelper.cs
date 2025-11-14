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
    /// This is a heuristic check looking for instance-specific identifiers in the message payload.
    /// </summary>
    private static bool ContainsInstanceSpecificData(KafkaTestMessage message, long instanceId)
    {
        // Check if the message value contains the instance ID as a string
        // This is a simple heuristic - in production you might want more sophisticated checks
        if (message.Value?.Contains(instanceId.ToString()) == true)
        {
            return true;
        }

        // Check JSON payload for instance-specific markers
        if (message.ValueAsJson != null)
        {
            var jsonString = message.ValueAsJson.ToJsonString();
            if (jsonString.Contains(instanceId.ToString()))
            {
                return true;
            }
        }

        return false;
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

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.InstanceManagement.Tests.E2E.Configuration;
using Microsoft.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.Infrastructure;

/// <summary>
/// Manages Kafka topic lifecycle for E2E tests.
/// Uses docker exec to run Kafka commands inside the container.
/// </summary>
public class KafkaAdminClient(ILogger<KafkaAdminClient> logger)
{
    private const string KafkaContainer = "dms-kafka1";
    private const string TopicPrefix = "edfi.dms";
    private const string TopicSuffix = "document";

    /// <summary>
    /// Create a Kafka topic for the specified instance ID.
    /// Topic name format: edfi.dms.{instanceId}.document
    /// </summary>
    public async Task CreateTopicAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        var topicName = GetTopicName(instanceId);
        logger.LogInformation("Creating Kafka topic: {TopicName}", LogSanitizer.Sanitize(topicName));

        var result = await RunKafkaCommandAsync(
            $"--create --if-not-exists --topic {topicName} --bootstrap-server {TestConstants.KafkaBootstrapServersInternal} --partitions 1 --replication-factor 1",
            cancellationToken
        );

        if (result.ExitCode != 0 && !result.Output.Contains("already exists"))
        {
            throw new InvalidOperationException(
                $"Failed to create Kafka topic {LogSanitizer.Sanitize(topicName)}: {LogSanitizer.Sanitize(result.Output)}"
            );
        }

        logger.LogInformation(
            "Kafka topic created successfully: {TopicName}",
            LogSanitizer.Sanitize(topicName)
        );
    }

    /// <summary>
    /// Delete a Kafka topic for the specified instance ID.
    /// </summary>
    public async Task DeleteTopicAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        var topicName = GetTopicName(instanceId);
        logger.LogInformation("Deleting Kafka topic: {TopicName}", LogSanitizer.Sanitize(topicName));

        var result = await RunKafkaCommandAsync(
            $"--delete --topic {topicName} --bootstrap-server {TestConstants.KafkaBootstrapServersInternal}",
            cancellationToken
        );

        // Topic deletion may fail if topic doesn't exist - that's OK
        if (result.ExitCode != 0 && !result.Output.Contains("does not exist"))
        {
            logger.LogWarning(
                "Failed to delete Kafka topic {TopicName}: {Output}",
                LogSanitizer.Sanitize(topicName),
                LogSanitizer.Sanitize(result.Output)
            );
        }
        else
        {
            logger.LogInformation("Kafka topic deleted: {TopicName}", LogSanitizer.Sanitize(topicName));
        }
    }

    /// <summary>
    /// Check if a topic exists
    /// </summary>
    public async Task<bool> TopicExistsAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        var topicName = GetTopicName(instanceId);
        logger.LogDebug("Checking if Kafka topic exists: {TopicName}", LogSanitizer.Sanitize(topicName));

        var result = await RunKafkaCommandAsync(
            $"--list --bootstrap-server {TestConstants.KafkaBootstrapServersInternal}",
            cancellationToken
        );

        var lines = result.Output.Split('\n');
        return Array.Exists(lines, line => line.Trim() == topicName);
    }

    /// <summary>
    /// Wait for topic to be ready for consumption
    /// </summary>
    public async Task WaitForTopicReadyAsync(
        int instanceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        var topicName = GetTopicName(instanceId);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await TopicExistsAsync(instanceId, cancellationToken))
            {
                logger.LogInformation("Kafka topic is ready: {TopicName}", LogSanitizer.Sanitize(topicName));
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException(
            $"Kafka topic {LogSanitizer.Sanitize(topicName)} did not become ready within {timeout}"
        );
    }

    public static string GetTopicName(int instanceId) => $"{TopicPrefix}.{instanceId}.{TopicSuffix}";

    private static async Task<(int ExitCode, string Output)> RunKafkaCommandAsync(
        string arguments,
        CancellationToken cancellationToken
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"exec {KafkaContainer} /opt/kafka/bin/kafka-topics.sh {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, output + error);
    }
}

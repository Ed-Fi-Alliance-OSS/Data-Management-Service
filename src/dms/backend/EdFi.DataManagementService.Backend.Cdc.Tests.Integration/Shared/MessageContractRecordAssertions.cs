// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class MessageContractRecordAssertions
{
    public static Task AssertTopicInventoryAsync(
        string bootstrapServers,
        CdcConnectorTemplateRequest request,
        CancellationToken token
    ) => AssertTopicInventoryAsync(bootstrapServers, request.Binding, token);

    public static async Task AssertTopicInventoryAsync(
        string bootstrapServers,
        CdcBinding binding,
        CancellationToken token
    )
    {
        string progressTopic = CdcArtifactNameGenerator
            .RecoverFromBinding(binding)
            .Inventory!.ProgressTopicName;
        using IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = bootstrapServers }
        )
            .SetLogHandler((_, _) => { })
            .Build();
        foreach (string topic in new[] { binding.TopicName, progressTopic })
        {
            var config = await admin
                .DescribeConfigsAsync(
                    [new ConfigResource { Type = ResourceType.Topic, Name = topic }],
                    new DescribeConfigsOptions { RequestTimeout = TimeSpan.FromSeconds(10) }
                )
                .WaitAsync(token);
            config.Single().Entries["cleanup.policy"].Value.Should().Be("compact");
            long.Parse(config.Single().Entries["delete.retention.ms"].Value)
                .Should()
                .BeGreaterThanOrEqualTo(604800000);
            var bounds = await new MessageContractKafkaObserver(bootstrapServers).CaptureKafkaBoundariesAsync(
                topic,
                token
            );
            bounds.Should().HaveCount(topic == binding.TopicName ? binding.PartitionCount : 1);
        }
        token.ThrowIfCancellationRequested();
        AssertNoRawTopics(admin.GetMetadata(TimeSpan.FromSeconds(10)), binding.ConnectorName);
    }

    // Inspect the complete broker inventory after a provider fence; public/progress scans alone
    // cannot demonstrate that the connector did not publish raw source topics.
    internal static void AssertNoRawTopics(Metadata metadata, string connectorName) =>
        metadata
            .Topics.Select(t => t.Topic)
            .Where(t =>
                t.StartsWith(connectorName + ".", StringComparison.Ordinal)
                || t.StartsWith("__debezium-heartbeat.", StringComparison.Ordinal)
            )
            .Should()
            .BeEmpty("raw source topics must not be produced");

    public static void AssertUpsert(
        CdcConnectorTemplateRequest request,
        MessageContractKafkaRecord record,
        string expectedUuid,
        int expectedPartition,
        JsonElement expectedEnvelope
    ) => AssertUpsert(request.Binding, record, expectedUuid, expectedPartition, expectedEnvelope);

    public static void AssertUpsert(
        CdcBinding binding,
        MessageContractKafkaRecord record,
        string expectedUuid,
        int expectedPartition,
        JsonElement expectedEnvelope
    )
    {
        expectedPartition
            .Should()
            .Be(
                MessageContractPartition.ForUuid(
                    expectedUuid,
                    binding.PartitionCount,
                    binding.PartitionerAlgorithm
                )
            );
        record.Topic.Should().Be(binding.TopicName);
        record.Partition.Should().Be(expectedPartition);
        record.Key.IsNull.Should().BeFalse();
        record.Key.Bytes.Should().Equal(Encoding.UTF8.GetBytes(expectedUuid));
        record.Value.IsNull.Should().BeFalse();
        record.Headers.Should().BeEmpty();
        MessageContractJson.ShouldEqual(
            JsonSerializer.Deserialize<JsonElement>(record.Value.Bytes),
            expectedEnvelope
        );
    }
}

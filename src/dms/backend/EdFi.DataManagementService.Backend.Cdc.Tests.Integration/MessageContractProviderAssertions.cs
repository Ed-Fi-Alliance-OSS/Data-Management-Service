// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class MessageContractProviderAssertions
{
    public static async Task AssertTopicInventoryAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        CdcConnectorTemplateRequest request,
        CancellationToken token
    )
    {
        using IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = fixture.HostKafkaBootstrapServers }
        )
            .SetLogHandler((_, _) => { })
            .Build();
        foreach (string topic in new[] { request.PublicTopicName, request.ProgressTopicName })
        {
            var config = await admin.DescribeConfigsAsync([
                new ConfigResource { Type = ResourceType.Topic, Name = topic },
            ]);
            config.Single().Entries["cleanup.policy"].Value.Should().Be("compact");
            long.Parse(config.Single().Entries["delete.retention.ms"].Value)
                .Should()
                .BeGreaterThanOrEqualTo(604800000);
            var bounds = await fixture.CaptureKafkaBoundariesAsync(topic, token);
            bounds.Should().HaveCount(topic == request.PublicTopicName ? request.Binding.PartitionCount : 1);
        }
        Metadata metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
        metadata
            .Topics.Select(t => t.Topic)
            .Where(t =>
                t.StartsWith(request.ConnectorName.Value + ".", StringComparison.Ordinal)
                || t.StartsWith("__debezium-heartbeat.", StringComparison.Ordinal)
            )
            .Should()
            .BeEmpty("raw source topics must not be produced");
    }

    public static void AssertUpsert(
        CdcConnectorTemplateRequest request,
        MessageContractKafkaRecord record,
        MessageContractProviderRow row
    )
    {
        record.Topic.Should().Be(request.PublicTopicName);
        record.Partition.Should().Be(row.Partition);
        record.Key.IsNull.Should().BeFalse();
        record.Key.Bytes.Should().Equal(Encoding.UTF8.GetBytes(row.Uuid));
        record.Value.IsNull.Should().BeFalse();
        record.Headers.Should().BeEmpty();
        MessageContractJson.ShouldEqual(
            JsonSerializer.Deserialize<JsonElement>(record.Value.Bytes),
            row.Expected
        );
    }

    public static void AssertSchema(JsonElement schema, string type, string name, int version)
    {
        schema.GetProperty("type").GetString().Should().Be(type);
        schema.GetProperty("name").GetString().Should().Be(name);
        schema.GetProperty("version").GetInt32().Should().Be(version);
        schema.GetProperty("optional").GetBoolean().Should().BeFalse();
    }
}

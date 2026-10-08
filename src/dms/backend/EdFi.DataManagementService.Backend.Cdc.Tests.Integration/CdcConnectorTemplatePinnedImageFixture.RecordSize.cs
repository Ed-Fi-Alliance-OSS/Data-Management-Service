// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    public static CdcConnectorTemplateRequest WithRecordBudget(
        CdcConnectorTemplateRequest request,
        int budget
    ) =>
        new(
            request.Binding,
            request.ProviderSetupEvidence,
            new(
                request.DeploymentPolicy.KafkaBootstrapServers,
                budget,
                producerBufferBytes: 33_554_432,
                heartbeatInterval: request.DeploymentPolicy.HeartbeatInterval,
                sqlServerPollInterval: request.DeploymentPolicy.SqlServerPollInterval
            ),
            request.ProviderConnectionProperties,
            request.KafkaClientSecurityProperties
        );

    public async Task<MessageContractSizeLimits> AlignRecordSizeLimitsAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken token
    )
    {
        int budget = request.DeploymentPolicy.MaxRecordBytes;
        foreach (string topic in new[] { request.PublicTopicName, request.ProgressTopicName })
        {
            await _docker.RunAsync(
                [
                    "exec",
                    BrokerContainerName,
                    "rpk",
                    "topic",
                    "alter-config",
                    topic,
                    "--set",
                    $"max.message.bytes={budget.ToString(CultureInfo.InvariantCulture)}",
                    "--brokers",
                    KafkaBootstrapServers,
                ],
                token
            );
        }

        using IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = HostKafkaBootstrapServers }
        )
            .SetLogHandler((_, _) => { })
            .Build();
        int topicLimit = 0;
        foreach (string topic in new[] { request.PublicTopicName, request.ProgressTopicName })
        {
            var config = await admin.DescribeConfigsAsync(
                [new ConfigResource { Type = ResourceType.Topic, Name = topic }],
                new DescribeConfigsOptions { RequestTimeout = TimeSpan.FromSeconds(10) }
            );
            topicLimit = int.Parse(
                config.Single().Entries["max.message.bytes"].Value,
                CultureInfo.InvariantCulture
            );
            topicLimit.Should().Be(budget);
            admin
                .GetMetadata(topic, TimeSpan.FromSeconds(10))
                .Topics.Single()
                .Partitions.Should()
                .OnlyContain(
                    p => p.Replicas.Length == 1 && p.InSyncReplicas.Length == 1,
                    "this isolated broker has one in-sync replica and no inter-broker replication hop"
                );
        }
        long batchLimit = await ReadBrokerLimitAsync("kafka_batch_max_bytes");
        long requestLimit = await ReadBrokerLimitAsync("kafka_request_max_bytes");
        batchLimit.Should().BeGreaterThanOrEqualTo(budget);
        requestLimit.Should().BeGreaterThan(budget, "the broker request path has framing headroom");
        ConsumerConfig consumer = CreateByteConsumerConfig();
        consumer.MaxPartitionFetchBytes.Should().BeGreaterThanOrEqualTo(budget);
        consumer.FetchMaxBytes.Should().BeGreaterThanOrEqualTo(budget);
        return new(
            budget,
            topicLimit,
            batchLimit,
            requestLimit,
            consumer.MaxPartitionFetchBytes!.Value,
            consumer.FetchMaxBytes!.Value,
            1
        );

        async Task<long> ReadBrokerLimitAsync(string name)
        {
            var result = await _docker.RunAsync(
                ["exec", BrokerContainerName, "rpk", "cluster", "config", "get", name],
                token
            );
            bool parsed = long.TryParse(
                result.StandardOutput.Trim(),
                CultureInfo.InvariantCulture,
                out long value
            );
            parsed.Should().BeTrue($"broker size property {name} must be numeric (output redacted)");
            return value;
        }
    }

    public async Task<string> ReadRecordSizeFailureCategoryAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken token
    )
    {
        using var response = await _httpClient.GetAsync(
            $"/connectors/{Uri.EscapeDataString(request.ConnectorName.Value)}/status",
            token
        );
        response.IsSuccessStatusCode.Should().BeTrue();
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        // Inspect only the typed exception marker. Never retain or return task traces, messages or source bodies.
        foreach (var task in body.RootElement.GetProperty("tasks").EnumerateArray())
        {
            if (
                ReadState(task) == "FAILED"
                && task.TryGetProperty("trace", out var trace)
                && (trace.GetString() ?? "").Contains(
                    "org.apache.kafka.common.errors.RecordTooLargeException:",
                    StringComparison.Ordinal
                )
            )
            {
                return "RecordTooLargeException";
            }
        }
        return "NoRecordTooLargeFailure";
    }

    public async Task UpdateRetainedConnectorSizeConfigAsync(
        CdcConnectorTemplateResult rendered,
        CancellationToken token
    )
    {
        // Preserve the observer and the exact registered connector identity. The normal renderer owns the new limits.
        string path = $"/connectors/{Uri.EscapeDataString(rendered.ConnectorName.Value)}/config";
        using var current = await _httpClient.GetAsync(path, token);
        current.IsSuccessStatusCode.Should().BeTrue();
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await current.Content.ReadAsStringAsync(token)
        )!;
        foreach (var (key, value) in rendered.Config)
        {
            config[key] = key == "transforms" ? $"contractObserver,{value}" : value;
        }
        using var content = new StringContent(
            JsonSerializer.Serialize(config),
            Encoding.UTF8,
            "application/json"
        );
        using var updated = await _httpClient.PutAsync(path, content, token);
        updated
            .IsSuccessStatusCode.Should()
            .BeTrue("retained connector config update must succeed (response redacted)");
    }
}

internal sealed record MessageContractSizeLimits(
    int MaxRecordBytes,
    int TopicMaxMessageBytes,
    long BrokerBatchMaxBytes,
    long BrokerRequestMaxBytes,
    int ConsumerPartitionFetchBytes,
    int ConsumerFetchBytes,
    int InSyncReplicas
);

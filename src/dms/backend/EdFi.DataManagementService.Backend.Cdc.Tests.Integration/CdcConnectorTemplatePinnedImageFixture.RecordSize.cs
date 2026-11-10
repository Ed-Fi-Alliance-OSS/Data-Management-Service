// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
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
                    "/opt/kafka/bin/kafka-configs.sh",
                    "--bootstrap-server",
                    KafkaBootstrapServers,
                    "--entity-type",
                    "topics",
                    "--entity-name",
                    topic,
                    "--alter",
                    "--add-config",
                    $"max.message.bytes={budget.ToString(CultureInfo.InvariantCulture)}",
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
        var broker = admin.GetMetadata(TimeSpan.FromSeconds(10)).Brokers.Single();
        var brokerConfigs = await admin
            .DescribeConfigsAsync(
                [
                    new ConfigResource
                    {
                        Type = ResourceType.Broker,
                        Name = broker.BrokerId.ToString(CultureInfo.InvariantCulture),
                    },
                ],
                new DescribeConfigsOptions { RequestTimeout = TimeSpan.FromSeconds(10) }
            )
            .WaitAsync(token);
        return ObserveRecordSizeLimits(
            budget,
            topicLimit,
            brokerConfigs.Single().Entries,
            CreateByteConsumerConfig()
        );
    }

    internal static MessageContractSizeLimits ObserveRecordSizeLimits(
        int budget,
        int topicLimit,
        IReadOnlyDictionary<string, ConfigEntryResult> brokerConfig,
        ConsumerConfig consumer
    )
    {
        // Kafka's message.max.bytes is the broker default, overridden by each topic's
        // max.message.bytes. It is not a second batch ceiling on explicitly configured topics.
        long defaultMessageLimit = ReadBrokerLimit("message.max.bytes");
        long requestLimit = ReadBrokerLimit("socket.request.max.bytes");
        topicLimit.Should().Be(budget);
        requestLimit.Should().BeGreaterThan(budget, "the broker request path has framing headroom");
        consumer.MaxPartitionFetchBytes.Should().BeGreaterThanOrEqualTo(budget);
        consumer.FetchMaxBytes.Should().BeGreaterThanOrEqualTo(budget);
        return new(
            budget,
            topicLimit,
            defaultMessageLimit,
            requestLimit,
            consumer.MaxPartitionFetchBytes!.Value,
            consumer.FetchMaxBytes!.Value,
            1
        );

        long ReadBrokerLimit(string name)
        {
            bool parsed = long.TryParse(
                brokerConfig[name].Value,
                CultureInfo.InvariantCulture,
                out long value
            );
            parsed.Should().BeTrue($"broker size property {name} must be numeric (output redacted)");
            value.Should().BePositive();
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
        var status = await CdcConnectorFixtureMutation.RunAsync(
            _httpClient,
            () =>
                new HttpRequestMessage(HttpMethod.Put, path)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(config),
                        Encoding.UTF8,
                        "application/json"
                    ),
                },
            CdcFixtureMutation.ConfigurationUpdate,
            token
        );
        status
            .Should()
            .Be(HttpStatusCode.OK, "retained connector config update must succeed (response redacted)");
    }
}

internal sealed record MessageContractSizeLimits(
    int MaxRecordBytes,
    int TopicMaxMessageBytes,
    long BrokerDefaultMessageMaxBytes,
    long BrokerRequestMaxBytes,
    int ConsumerPartitionFetchBytes,
    int ConsumerFetchBytes,
    int InSyncReplicas
);

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Confluent.Kafka;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    // Admission uses the production local Kafka authority adapter. The existing Redpanda
    // template/telemetry smoke profile remains available to its original callers.
    private Task<DockerCommandResult> StartControllerKafkaAsync(CancellationToken token) =>
        _docker.RunAsync(
            [
                "run",
                "--detach",
                "--name",
                BrokerContainerName,
                "--network",
                NetworkName,
                "-p",
                $"127.0.0.1:{_controllerBrokerPort}:29092",
                "--label",
                $"com.docker.compose.project={_resourcePrefix}",
                "--label",
                "com.docker.compose.service=kafka",
                "-e",
                "KAFKA_NODE_ID=1",
                "-e",
                "KAFKA_PROCESS_ROLES=broker,controller",
                "-e",
                "KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER",
                "-e",
                $"KAFKA_CONTROLLER_QUORUM_VOTERS=1@{BrokerContainerName}:9093",
                "-e",
                $"KAFKA_LISTENERS=INTERNAL://0.0.0.0:9092,EXTERNAL://0.0.0.0:29092,CONTROLLER://{BrokerContainerName}:9093",
                "-e",
                $"KAFKA_ADVERTISED_LISTENERS=INTERNAL://{BrokerContainerName}:9092,EXTERNAL://127.0.0.1:{_controllerBrokerPort}",
                "-e",
                "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,INTERNAL:PLAINTEXT,EXTERNAL:PLAINTEXT",
                "-e",
                "KAFKA_INTER_BROKER_LISTENER_NAME=INTERNAL",
                "-e",
                "KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1",
                "-e",
                "KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1",
                "-e",
                "KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1",
                "-e",
                "KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0",
                "-e",
                "KAFKA_NUM_PARTITIONS=1",
                CdcComposeBrokerSizeDeployment.BrokerImage,
            ],
            token
        );

    // Test-owned deployment of static settings. Restart the same isolated container, retaining
    // its KRaft log, topic IDs, data and ports. The ordinary admin adapter verifies live values.
    internal async Task ApplyControllerBrokerSizesAsync(
        IReadOnlyList<CdcKafkaBrokerCapacity> limits,
        CancellationToken token
    )
    {
        var limit = limits.Single();
        limit.BrokerId.Should().Be(1);
        string exports = string.Join(
            "\n",
            "export KAFKA_SOCKET_REQUEST_MAX_BYTES="
                + limit.SocketRequestMaxBytes.ToString(CultureInfo.InvariantCulture),
            "export KAFKA_REPLICA_FETCH_MAX_BYTES="
                + limit.ReplicaFetchMaxBytes.ToString(CultureInfo.InvariantCulture),
            "export KAFKA_REPLICA_FETCH_RESPONSE_MAX_BYTES="
                + limit.ReplicaFetchResponseMaxBytes.ToString(CultureInfo.InvariantCulture)
        );
        // Only validated numeric limits enter this script; the file is sourced on every restart.
        await _docker.RunAsync(
            [
                "exec",
                "--user",
                "root",
                BrokerContainerName,
                "bash",
                "-c",
                "printf '%s\n' '" + exports + "' >> /etc/kafka/docker/bash-config",
            ],
            token
        );
        await _docker.RunAsync(["restart", BrokerContainerName], token);
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = ControllerKafkaBootstrapServers }
        )
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        await CdcControllerFixture.WaitAsync(
            _ =>
            {
                try
                {
                    return Task.FromResult(admin.GetMetadata(TimeSpan.FromSeconds(2)).Brokers.Count == 1);
                }
                catch (KafkaException)
                {
                    return Task.FromResult(false);
                }
            },
            TimeSpan.FromSeconds(25),
            TimeSpan.FromMilliseconds(250),
            token
        );
    }
}

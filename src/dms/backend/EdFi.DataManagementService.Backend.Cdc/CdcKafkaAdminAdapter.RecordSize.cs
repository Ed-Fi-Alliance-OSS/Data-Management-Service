// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Confluent.Kafka.Admin;
using static EdFi.DataManagementService.Backend.Cdc.CdcRecordSizeRollout;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Explicit size-only effects, separate from setup/validation administration.</summary>
public interface ICdcKafkaRecordSizeAdministration
{
    Task<CdcTransportResult<CdcKafkaBrokerEvidence>> IncreaseBrokerLimitsAsync(
        CdcDeploymentRequest desired,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CdcKafkaTopicEvidence>> IncreasePublicTopicLimitAsync(
        CdcDeploymentRequest desired,
        int previousMaxRecordBytes,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Deployment authority for static Kafka broker settings. Apply exactly the supplied monotonic
/// per-broker limits while preserving all other configuration and durable broker state. Acknowledgement
/// never proves effectiveness; the admin adapter independently reads every live broker afterward.
/// </summary>
public interface ICdcKafkaBrokerSizeDeployment
{
    Task ApplyAsync(
        CdcDeploymentRequest request,
        IReadOnlyList<CdcKafkaBrokerCapacity> limits,
        CancellationToken cancellationToken
    );
}

public sealed partial class CdcKafkaAdminAdapter : ICdcKafkaRecordSizeAdministration
{
    public Task<CdcTransportResult<CdcKafkaBrokerEvidence>> IncreaseBrokerLimitsAsync(
        CdcDeploymentRequest desired,
        CancellationToken cancellationToken
    ) =>
        GuardAsync(
            async () =>
            {
                var before = Observed(await InspectBrokersAsync(desired, cancellationToken));
                if (!before.InventoryComplete || before.Brokers.Count == 0)
                {
                    throw new InvalidOperationException();
                }

                int ceiling = desired.ConnectorPolicy.MaxRecordBytes;
                var changes = before
                    .Brokers.Where(b =>
                        b.SocketRequestMaxBytes < ceiling
                        || b.ReplicaFetchMaxBytes < ceiling
                        || b.ReplicaFetchResponseMaxBytes < ceiling
                    )
                    .Select(b =>
                        b with
                        {
                            SocketRequestMaxBytes = Math.Max(b.SocketRequestMaxBytes, ceiling),
                            ReplicaFetchMaxBytes = Math.Max(b.ReplicaFetchMaxBytes, ceiling),
                            ReplicaFetchResponseMaxBytes = Math.Max(b.ReplicaFetchResponseMaxBytes, ceiling),
                        }
                    )
                    .ToArray();
                if (changes.Length > 0)
                {
                    if (_brokerSizes is null)
                    {
                        return Failure<CdcKafkaBrokerEvidence>(CdcDeploymentFailure.Unavailable);
                    }
                    // These are static Kafka settings. Deployment owns persistence/restart; Kafka
                    // incremental AlterConfigs is reserved for the dynamic public-topic override.
                    await GuardAsync<CdcTransportAcknowledgement>(
                        async () =>
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken
                            );
                            timeout.CancelAfter(desired.Timing.CallTimeout);
                            await _brokerSizes
                                .ApplyAsync(desired, changes, timeout.Token)
                                .WaitAsync(timeout.Token);
                            return new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());
                        },
                        cancellationToken
                    );
                }
                var after = await InspectBrokersAsync(desired, cancellationToken);
                var live = Observed(after);
                RequireBrokerCapacity(live, ceiling);
                if (
                    !before
                        .Brokers.Select(b => b.BrokerId)
                        .Order()
                        .SequenceEqual(live.Brokers.Select(b => b.BrokerId).Order())
                )
                {
                    throw new InvalidOperationException();
                }

                foreach (var prior in before.Brokers)
                {
                    var current = live.Brokers.Single(b => b.BrokerId == prior.BrokerId);
                    if (
                        current.SocketRequestMaxBytes < prior.SocketRequestMaxBytes
                        || current.ReplicaFetchMaxBytes < prior.ReplicaFetchMaxBytes
                        || current.ReplicaFetchResponseMaxBytes < prior.ReplicaFetchResponseMaxBytes
                    )
                    {
                        throw new InvalidOperationException();
                    }
                }
                return after;
            },
            cancellationToken
        );

    public Task<CdcTransportResult<CdcKafkaTopicEvidence>> IncreasePublicTopicLimitAsync(
        CdcDeploymentRequest desired,
        int previousMaxRecordBytes,
        CancellationToken cancellationToken
    ) =>
        GuardAsync(
            async () =>
            {
                int ceiling = desired.ConnectorPolicy.MaxRecordBytes;
                if (previousMaxRecordBytes <= 0 || ceiling <= previousMaxRecordBytes)
                {
                    throw new InvalidOperationException();
                }

                RequireBrokerCapacity(
                    Observed(await InspectBrokersAsync(desired, cancellationToken)),
                    ceiling
                );
                var before = Observed(
                    await InspectTopicAsync(desired, desired.Binding.TopicName, cancellationToken)
                );
                var current = before.Configuration["max.message.bytes"];
                if (
                    !current.IsTopicOverride
                    || (Number(current.Value) != previousMaxRecordBytes && Number(current.Value) != ceiling)
                )
                {
                    throw new InvalidOperationException();
                }

                if (before.PartitionReplicas.Count != desired.Binding.PartitionCount)
                {
                    throw new InvalidOperationException();
                }

                Dictionary<ConfigResource, List<ConfigEntry>> changes = [];
                if (Number(current.Value) != ceiling)
                {
                    changes.Add(
                        new() { Type = ResourceType.Topic, Name = desired.Binding.TopicName },
                        [
                            new()
                            {
                                Name = "max.message.bytes",
                                Value = ceiling.ToString(CultureInfo.InvariantCulture),
                                IncrementalOperation = AlterConfigOpType.Set,
                            },
                        ]
                    );
                }

                await AlterSizeAsync(desired, changes, cancellationToken);
                var after = await InspectTopicAsync(desired, desired.Binding.TopicName, cancellationToken);
                var live = Observed(after);
                if (
                    CdcDeploymentKafkaPolicy
                        .ObserveTopic(
                            desired,
                            CdcDeploymentKafkaPolicy.Build(desired).BindingTopics[0],
                            after
                        )
                        .State
                        != EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcKafkaPolicyItemState.Satisfied
                    || !before
                        .PartitionReplicas.OrderBy(p => p.Key)
                        .Select(p => (p.Key, string.Join(',', p.Value)))
                        .SequenceEqual(
                            live.PartitionReplicas.OrderBy(p => p.Key)
                                .Select(p => (p.Key, string.Join(',', p.Value)))
                        )
                )
                {
                    throw new InvalidOperationException();
                }

                return after;
            },
            cancellationToken
        );

    private async Task AlterSizeAsync(
        CdcDeploymentRequest request,
        Dictionary<ConfigResource, List<ConfigEntry>> changes,
        CancellationToken token
    )
    {
        if (changes.Count == 0)
        {
            return;
        }
        // Incremental SET preserves every unrelated topic property.
        await GuardAsync<CdcTransportAcknowledgement>(
            async () =>
            {
                await CallAsync(
                    () =>
                        _client.IncrementalAlterConfigsAsync(
                            changes,
                            new() { RequestTimeout = request.Timing.CallTimeout }
                        ),
                    request,
                    token
                );
                return new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());
            },
            token
        );
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Ephemeral authority for the acknowledged invocation; never supplied by ordinary commands.</summary>
internal sealed class CdcRecordSizeRollout(
    CdcRecordSizeAcknowledgementInvocation invocation,
    CdcDeploymentRequest previous,
    CdcDeploymentRequest desired
)
{
    internal CdcRecordSizeAcknowledgementInvocation Invocation { get; } = invocation;

    internal CdcDeploymentRequest Stage(IReadOnlyDictionary<string, string> live)
    {
        int request = Number(live["producer.override.max.request.size"]);
        int buffer = Number(live["producer.override.buffer.memory"]);
        Require(
            request == previous.ConnectorPolicy.MaxRecordBytes
                || request == desired.ConnectorPolicy.MaxRecordBytes
        );
        Require(
            buffer == previous.ConnectorPolicy.EffectiveProducerBufferBytes
                || buffer == desired.ConnectorPolicy.EffectiveProducerBufferBytes
        );
        Require(
            request != desired.ConnectorPolicy.MaxRecordBytes
                || buffer == desired.ConnectorPolicy.EffectiveProducerBufferBytes
        );
        return WithPolicy(previous, request, buffer);
    }

    internal CdcKafkaPolicyObservation ObservePolicy(
        CdcDeploymentRequest stage,
        string operation,
        DateTimeOffset at,
        CdcKafkaDeploymentEvidence evidence
    )
    {
        var topic = Observed(evidence.Topics[desired.Binding.TopicName]);
        Require(topic.Configuration.TryGetValue("max.message.bytes", out var value) && value.IsTopicOverride);
        int ceiling = Number(value!.Value);
        Require(
            ceiling == previous.ConnectorPolicy.MaxRecordBytes
                || ceiling == desired.ConnectorPolicy.MaxRecordBytes
        );
        // An advanced topic/buffer/request with a weaker prerequisite is out of order, not repairable here.
        var producer = Observed(evidence.Producer);
        if (ceiling == desired.ConnectorPolicy.MaxRecordBytes)
        {
            RequireBrokerCapacity(Observed(evidence.Brokers), ceiling);
        }
        if (
            producer.BufferBytes != previous.ConnectorPolicy.EffectiveProducerBufferBytes
            || producer.MaxRequestBytes == desired.ConnectorPolicy.MaxRecordBytes
        )
        {
            Require(ceiling == desired.ConnectorPolicy.MaxRecordBytes);
        }
        return CdcDeploymentKafkaPolicy.ObserveBindingForIncrease(stage, operation, at, evidence, ceiling);
    }

    internal static void RequireBrokerCapacity(CdcKafkaBrokerEvidence evidence, int ceiling)
    {
        Require(evidence.InventoryComplete && evidence.Brokers.Count > 0);
        Require(
            evidence.Brokers.All(b =>
                b.BrokerId >= 0
                && b.SocketRequestMaxBytes >= ceiling
                && b.ReplicaFetchMaxBytes >= ceiling
                && b.ReplicaFetchResponseMaxBytes >= ceiling
            )
        );
        Require(evidence.Brokers.Select(b => b.BrokerId).Distinct().Count() == evidence.Brokers.Count);
    }

    internal static int Number(string value)
    {
        Require(
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0
        );
        return number;
    }

    internal static T Observed<T>(CdcTransportResult<T> result)
        where T : notnull =>
        result switch
        {
            CdcTransportResult<T>.Observed found => found.Value,
            CdcTransportResult<T>.Unavailable failed => throw new CdcEstablishedValidation.EvidenceException(
                failed.Diagnostic
            ),
            _ => throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory),
        };

    // Policy cloning precedes preflight; projection inputs must remain deferred until after containment.
    internal static CdcDeploymentRequest WithPolicy(CdcDeploymentRequest request, int ceiling, int buffer) =>
        CdcDeploymentRequest.CreateDeferred(
            request.Binding,
            request.DmsSettings,
            () => request.ProviderSetup,
            request.ConnectEndpoint,
            request.WorkerMetricsEndpoint,
            new(
                request.ConnectorPolicy.KafkaBootstrapServers,
                ceiling,
                buffer,
                request.ConnectorPolicy.HeartbeatInterval,
                request.ConnectorPolicy.SqlServerPollInterval
            ),
            request.WorkerPolicy,
            request.ProviderConnectionProperties,
            request.KafkaClientSecurityProperties,
            request.Timing
        );
}

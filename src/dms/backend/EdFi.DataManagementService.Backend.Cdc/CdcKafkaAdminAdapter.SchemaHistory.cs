// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed partial class CdcKafkaAdminAdapter
{
    // Confluent exposes ListOffsets through an extension rather than IAdminClient. Keep the native
    // boundary replaceable in transport tests, while production always uses the owned admin client.
    internal delegate Task<ListOffsetsResult> ListOffsetsCall(
        IEnumerable<TopicPartitionOffsetSpec> specifications,
        ListOffsetsOptions options
    );
    internal ListOffsetsCall ListOffsets { get; init; }

    /// <summary>
    /// Require one nonempty schema-history partition retained from offset zero. The caller also
    /// validates infinite retention without compaction, the exact template, and streaming progress.
    /// This examines history retention only; broker offsets never replace a provider publication barrier.
    /// No consumer group is created, and no source data or progress-topic records are consumed.
    /// </summary>
    public Task<CdcTransportResult<CdcSqlServerSchemaHistoryState>> InspectSchemaHistoryAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        GuardAsync(
            async () =>
            {
                if (request.Binding.Provider != CdcProvider.SqlServer)
                {
                    return new CdcTransportResult<CdcSqlServerSchemaHistoryState>.Observed(
                        CdcSqlServerSchemaHistoryState.NotApplicable
                    );
                }
                var topic = CdcConnectorTemplateBindingArtifacts
                    .From(request.Binding, nameof(request))
                    .ArtifactInventory.SchemaHistoryTopicName!;
                var observed = await InspectTopicAsync(request, topic, cancellationToken);
                if (observed is CdcTransportResult<CdcKafkaTopicEvidence>.Absent)
                {
                    return new CdcTransportResult<CdcSqlServerSchemaHistoryState>.Observed(
                        CdcSqlServerSchemaHistoryState.Missing
                    );
                }
                if (observed is CdcTransportResult<CdcKafkaTopicEvidence>.Unavailable unavailable)
                {
                    return new CdcTransportResult<CdcSqlServerSchemaHistoryState>.Unavailable(
                        unavailable.Diagnostic
                    );
                }
                var evidence = ((CdcTransportResult<CdcKafkaTopicEvidence>.Observed)observed).Value;
                if (evidence.PartitionReplicas.Count != 1 || !evidence.PartitionReplicas.ContainsKey(0))
                {
                    return Failure<CdcSqlServerSchemaHistoryState>(CdcDeploymentFailure.ValidationFailed);
                }
                TopicPartition partition = new(topic, 0);
                var low = await ReadHistoryOffsetAsync(
                    request,
                    partition,
                    OffsetSpec.Earliest(),
                    cancellationToken
                );
                var high = await ReadHistoryOffsetAsync(
                    request,
                    partition,
                    OffsetSpec.Latest(),
                    cancellationToken
                );
                var finalLow = await ReadHistoryOffsetAsync(
                    request,
                    partition,
                    OffsetSpec.Earliest(),
                    cancellationToken
                );
                if (low != finalLow || high < low)
                {
                    return Failure<CdcSqlServerSchemaHistoryState>(CdcDeploymentFailure.Unavailable);
                }
                var state = CdcSqlServerSchemaHistoryState.Valid;
                if (low > 0)
                {
                    state = CdcSqlServerSchemaHistoryState.RequiredRecordLost;
                }
                else if (high == 0)
                {
                    state = CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset;
                }
                return new CdcTransportResult<CdcSqlServerSchemaHistoryState>.Observed(state);
            },
            cancellationToken
        );

    private async Task<long> ReadHistoryOffsetAsync(
        CdcDeploymentRequest request,
        TopicPartition partition,
        OffsetSpec specification,
        CancellationToken token
    )
    {
        var result = await CallAsync(
            () =>
                ListOffsets(
                    [new() { TopicPartition = partition, OffsetSpec = specification }],
                    new()
                    {
                        RequestTimeout = request.Timing.CallTimeout,
                        IsolationLevel = IsolationLevel.ReadCommitted,
                    }
                ),
            request,
            token
        );
        token.ThrowIfCancellationRequested();
        if (result.ResultInfos.Count != 1)
        {
            throw new InvalidOperationException("Schema-history offset evidence is incomplete.");
        }
        var observed = result.ResultInfos[0].TopicPartitionOffsetError;
        ThrowIfError(observed.Error);
        if (observed.TopicPartition != partition || observed.Offset.Value < 0)
        {
            throw new InvalidOperationException("Schema-history offset evidence is contradictory.");
        }
        return observed.Offset.Value;
    }
}

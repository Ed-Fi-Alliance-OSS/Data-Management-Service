// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

public interface ICdcKafkaAdmin
{
    /// <summary>
    /// Resolves the configured cluster-scoped Kafka Connect offset store, creating it when absent, and
    /// reports its observed cleanup policy, durability, and worker-only ACL evidence.
    /// </summary>
    /// <remarks>
    /// The offset store is shared source-position state for every binding registered with the worker.
    /// It is never deleted and never appears in per-binding teardown. Evidence that cannot be obtained
    /// yields <see cref="CdcConnectOffsetStorePolicyState.Unknown"/>, never
    /// <see cref="CdcConnectOffsetStorePolicyState.Satisfied"/>.
    /// </remarks>
    Task<CdcConnectOffsetStorePolicyObservation> EnsureConnectOffsetStoreAsync(
        CdcObservationContext context,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reports the same Connect offset-store evidence without provisioning it: a store that does not
    /// exist is reported as absent rather than created.
    /// </summary>
    /// <remarks>
    /// This is what a status read observes the store through. A read that created what it found absent
    /// would report the policy of a topic the pass itself had just created, which is not an
    /// observation of what the deployment holds.
    /// </remarks>
    Task<CdcConnectOffsetStorePolicyObservation> DescribeConnectOffsetStoreAsync(
        CdcObservationContext context,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Verifies that the broker request, record-batch, and replica-fetch limits accept the configured
    /// record-size budget, and derives the producer buffer the connector is rendered with.
    /// </summary>
    /// <remarks>
    /// Kafka's own defaults are never assumed: a limit that cannot be read is reported as
    /// <see cref="CdcKafkaPolicyItemState.Unknown"/> rather than presumed adequate.
    /// </remarks>
    Task<CdcKafkaRecordSizeEvidence> VerifyRecordSizeAsync(
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Provisions and validates every binding-governed Kafka artifact — topics, ACLs, and the
    /// record-size budget — and composes them into one observation validated by
    /// <see cref="CdcKafkaPolicyObservationValidator"/>.
    /// </summary>
    /// <remarks>
    /// ACL verification completes here, before connector registration, and never relies on
    /// consumer-side filtering as an isolation control.
    /// </remarks>
    Task<CdcKafkaPolicyObservation> EnsureBindingKafkaPolicyAsync(
        CdcObservationContext context,
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reports the same binding-governed Kafka evidence without provisioning any of it: an absent topic
    /// is reported as absent and a missing grant as missing, rather than being created or repaired.
    /// </summary>
    /// <remarks>
    /// This is what adoption verifies against. Adoption repairs missing deployment state around an
    /// already complete governed-artifact set and is not a first-time enablement path, so a pass that
    /// created what it found absent would turn a refused adoption into a partial provisioning.
    /// </remarks>
    Task<CdcKafkaPolicyObservation> DescribeBindingKafkaPolicyAsync(
        CdcObservationContext context,
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reads the binding's SQL Server schema-history topic and reports the evidence the shared
    /// source-history continuity classifier requires for that provider. A PostgreSQL binding has no
    /// schema-history topic and no evidence is composed for it.
    /// </summary>
    /// <remarks>
    /// The read is topic metadata plus earliest and latest partition offsets, never a consumed record:
    /// every state this evidence can prove is decidable from offsets, and consuming would require
    /// widening the connector-only history-topic grants. <see cref="CdcSqlServerSchemaHistoryState.RequiredRecordLost"/>
    /// is never reported — it is not decidable from offsets, and the history topic's own
    /// delete-with-infinite-retention policy is what prevents that state.
    /// </remarks>
    /// <param name="enablementPhase">
    /// The phase the caller is observing in. It is load-bearing: the classifier leaves a first
    /// enablement's non-continuous state unknown and latches nothing, while the same state observed
    /// after initial admission latches a terminal loss.
    /// </param>
    /// <param name="connectorCommittedStreamingOffset">
    /// Whether the connector has committed a streaming offset under the binding's own source partition.
    /// An empty history topic is a loss only against a retained offset that would need replaying.
    /// </param>
    Task<CdcSqlServerSchemaHistoryEvidence?> ReadSqlServerSchemaHistoryAsync(
        CdcArtifactInventory inventory,
        CdcSqlServerSchemaHistoryEnablementPhase enablementPhase,
        bool connectorCommittedStreamingOffset,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes the binding's governed Kafka topics and their literal ACLs, reporting each as a
    /// <see cref="CdcGovernedArtifact"/> that was deleted or already absent.
    /// </summary>
    /// <remarks>
    /// The shared cluster-scoped Connect offset store is never touched and never appears in the result:
    /// it is worker state for every binding, not a binding artifact. A broker failure propagates rather
    /// than being reported as removal, so a partial teardown leaves the binding record intact and the
    /// retry stays idempotent.
    /// </remarks>
    Task<IReadOnlyList<CdcGovernedArtifact>> DeleteBindingArtifactsAsync(
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Per-topic policy evidence for one binding. <see cref="SchemaHistoryTopic"/> is SQL Server-only
/// evidence and is null for PostgreSQL, matching the shared observation contract.
/// </summary>
public sealed record CdcKafkaBindingTopicPolicies(
    CdcKafkaTopicPolicy PublicTopic,
    CdcKafkaTopicPolicy ProgressTopic,
    CdcKafkaTopicPolicy? SchemaHistoryTopic,
    IReadOnlyList<CdcDiagnostic> Diagnostics
);

/// <summary>
/// Per-bucket ACL evidence for one binding. Consumer-group grants are verified as part of the
/// public-topic evidence, because the shared observation exposes only three topic ACL buckets and the
/// invariant requires ACL evidence rather than a discrete group field.
/// </summary>
public sealed record CdcKafkaBindingAclPolicies(
    CdcKafkaAclPolicy PublicTopicAcls,
    CdcKafkaAclPolicy ProgressTopicAcls,
    CdcKafkaAclPolicy? SchemaHistoryTopicAcls,
    IReadOnlyList<CdcDiagnostic> Diagnostics
);

/// <summary>
/// Record-size policy evidence together with the producer buffer the connector is rendered with.
/// Deployment must provision Connect worker heap beyond <see cref="ProducerBufferBytes"/>, which Kafka
/// documents as approximate producer buffer capacity rather than a hard total-memory bound; that is a
/// deployment obligation the admin client cannot observe.
/// </summary>
public sealed record CdcKafkaRecordSizeEvidence(
    CdcKafkaRecordSizePolicy Policy,
    int ProducerBufferBytes,
    IReadOnlyList<CdcDiagnostic> Diagnostics
);

/// <summary>
/// Whether a Kafka policy pass may provision what it finds absent, or must only report it.
/// </summary>
internal enum CdcKafkaProvisioningMode
{
    /// <summary>Create an absent topic and repair a missing required grant, then validate.</summary>
    CreateOrValidate,

    /// <summary>
    /// Report what the broker holds and change nothing. An absent topic and a missing required grant
    /// are each reported as the nonconformance they are.
    /// </summary>
    ValidateOnly,
}

/// <summary>
/// Replication and in-sync-replica floors the active deployment durability profile requires of
/// governed Kafka topics.
/// </summary>
internal sealed record CdcKafkaDurabilityPolicy(short ReplicationFactor, int MinInSyncReplicas)
{
    public static CdcKafkaDurabilityPolicy For(CdcDurabilityProfile durabilityProfile) =>
        durabilityProfile == CdcDurabilityProfile.Production ? new(3, 2) : new(1, 1);
}

/// <summary>
/// Reads the offsets bounding a topic partition's retained records.
/// </summary>
/// <remarks>
/// Confluent.Kafka exposes <c>ListOffsets</c> as an extension over the concrete admin client rather
/// than as an <see cref="IAdminClient"/> member, and that extension refuses any other implementation,
/// so the call sits behind this seam. Without it the schema-history read could not be observed against
/// anything but a live broker.
/// </remarks>
internal interface ICdcKafkaTopicOffsetReader
{
    Task<IReadOnlyList<ListOffsetsResultInfo>> ListOffsetsAsync(
        IReadOnlyList<TopicPartitionOffsetSpec> offsetSpecs,
        TimeSpan timeout
    );
}

internal sealed class CdcKafkaTopicOffsetReader(IAdminClient adminClient) : ICdcKafkaTopicOffsetReader
{
    public async Task<IReadOnlyList<ListOffsetsResultInfo>> ListOffsetsAsync(
        IReadOnlyList<TopicPartitionOffsetSpec> offsetSpecs,
        TimeSpan timeout
    ) =>
        (
            await adminClient.ListOffsetsAsync(
                offsetSpecs,
                new ListOffsetsOptions { RequestTimeout = timeout }
            )
        ).ResultInfos
        ?? [];
}

internal sealed class CdcKafkaAdminAdapter(
    IAdminClient adminClient,
    IOptions<CdcControlOptions> options,
    TimeProvider timeProvider,
    ILogger<CdcKafkaAdminAdapter> logger,
    ICdcKafkaTopicOffsetReader? topicOffsetReader = null
) : ICdcKafkaAdmin
{
    /// <summary>
    /// Reads over the same admin client the adapter already holds unless a caller supplies its own.
    /// </summary>
    private readonly ICdcKafkaTopicOffsetReader _topicOffsetReader =
        topicOffsetReader ?? new CdcKafkaTopicOffsetReader(adminClient);

    internal const string CleanupPolicyConfigName = "cleanup.policy";
    internal const string MinInSyncReplicasConfigName = "min.insync.replicas";
    internal const string DeleteRetentionConfigName = "delete.retention.ms";
    internal const string MaxMessageBytesConfigName = "max.message.bytes";
    internal const string RetentionMillisecondsConfigName = "retention.ms";
    internal const string RetentionBytesConfigName = "retention.bytes";
    internal const string SocketRequestMaxBytesConfigName = "socket.request.max.bytes";
    internal const string MessageMaxBytesConfigName = "message.max.bytes";
    internal const string ReplicaFetchMaxBytesConfigName = "replica.fetch.max.bytes";
    internal const string ReplicaFetchResponseMaxBytesConfigName = "replica.fetch.response.max.bytes";
    internal const string CompactCleanupPolicy = "compact";
    internal const string DeleteCleanupPolicy = "delete";

    /// <summary>Kafka's any-host wildcard; host-level restriction is a deployment network control.</summary>
    internal const string AnyHost = "*";

    /// <summary>
    /// Stand-in durability-profile token for an observation composed while the configured profile is
    /// blank, so the evidence envelope stays a valid bounded token.
    /// </summary>
    private const string UnspecifiedDurabilityProfile = "unspecified";

    /// <summary>
    /// Seven days, the fixed v1 floor for the public topic's per-topic tombstone retention. A
    /// deployment may configure a higher value without changing the binding generation.
    /// </summary>
    internal const long MinimumDeleteRetentionMilliseconds = 604800000;

    /// <summary>Kafka's sentinel for unbounded time or size retention.</summary>
    internal const long InfiniteRetention = -1;

    /// <summary>
    /// Kafka Connect's own default for <c>offset.storage.partitions</c>. The offset store's partition
    /// count is not a binding property, so creation uses the worker default rather than the binding's
    /// public-topic partition count.
    /// </summary>
    internal const int OffsetStorePartitionCount = 25;

    private static readonly AclOperation[] RequiredWorkerOperations =
    [
        AclOperation.Read,
        AclOperation.Write,
        AclOperation.Describe,
    ];

    /// <summary>
    /// Broker-scoped limits the record-size budget must fit through. The effective
    /// <c>message.max.bytes</c> is resolved separately because a topic-level override wins over the
    /// broker value for that topic's produce path.
    /// </summary>
    private static readonly string[] BrokerRequestLimitConfigNames =
    [
        SocketRequestMaxBytesConfigName,
        ReplicaFetchMaxBytesConfigName,
        ReplicaFetchResponseMaxBytesConfigName,
    ];

    public Task<CdcConnectOffsetStorePolicyObservation> EnsureConnectOffsetStoreAsync(
        CdcObservationContext context,
        CancellationToken cancellationToken
    ) => ConnectOffsetStoreAsync(context, CdcKafkaProvisioningMode.CreateOrValidate, cancellationToken);

    public Task<CdcConnectOffsetStorePolicyObservation> DescribeConnectOffsetStoreAsync(
        CdcObservationContext context,
        CancellationToken cancellationToken
    ) => ConnectOffsetStoreAsync(context, CdcKafkaProvisioningMode.ValidateOnly, cancellationToken);

    private async Task<CdcConnectOffsetStorePolicyObservation> ConnectOffsetStoreAsync(
        CdcObservationContext context,
        CdcKafkaProvisioningMode mode,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        CdcControlOptions controlOptions = options.Value;
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        string topicName = controlOptions.ConnectOffsetStorageTopic;
        List<CdcDiagnostic> diagnostics = [];

        if (
            !CdcControlOptions.TryParseDurabilityProfile(
                controlOptions.DurabilityProfile,
                out CdcDurabilityProfile? durabilityProfile
            )
        )
        {
            diagnostics.Add(
                OffsetStoreUnavailable(
                    "$.durabilityProfile",
                    observedAt,
                    "CDC Connect offset-store durability profile is unrecognized.",
                    $"{CdcControlOptions.LocalDurabilityProfile}|{CdcControlOptions.ProductionDurabilityProfile}"
                )
            );
            return Unresolved(context, controlOptions, observedAt, diagnostics);
        }

        CdcKafkaDurabilityPolicy durability = CdcKafkaDurabilityPolicy.For(durabilityProfile.Value);
        TimeSpan timeout = controlOptions.Timeouts.KafkaAdmin;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            TopicMetadata? topicMetadata = FindTopic(topicName, timeout);
            if (topicMetadata is null && mode == CdcKafkaProvisioningMode.CreateOrValidate)
            {
                await CreateTopicAsync(
                    topicName,
                    OffsetStorePartitionCount,
                    CompactCleanupPolicy,
                    durability,
                    [],
                    timeout
                );
                topicMetadata = FindTopic(topicName, timeout);
            }

            if (topicMetadata is null && mode == CdcKafkaProvisioningMode.ValidateOnly)
            {
                // The store is reported absent rather than created, for the same reason the governed
                // topics are: a pass that provisions is not a verification of what the deployment holds.
                diagnostics.Add(
                    OffsetStoreUnavailable(
                        "$.offsetStorageTopic",
                        observedAt,
                        "CDC Connect offset store does not exist.",
                        observed: "absent"
                    )
                );
                return Unresolved(context, controlOptions, observedAt, diagnostics);
            }

            cancellationToken.ThrowIfCancellationRequested();

            int? replicationFactor = ResolveReplicationFactor(topicMetadata);
            CdcOffsetStoreConfigEvidence configEvidence = ToOffsetStoreConfigEvidence(
                topicMetadata is null ? null : await ReadTopicConfigAsync(topicName, timeout)
            );
            CdcConnectOffsetStoreItemState aclState = await ResolveAclStateAsync(
                topicName,
                controlOptions,
                timeout,
                observedAt,
                diagnostics
            );

            return Evaluate(
                context,
                controlOptions,
                durability,
                replicationFactor,
                configEvidence,
                aclState,
                observedAt,
                diagnostics
            );
        }
        catch (KafkaException exception)
        {
            // The broker response body is never surfaced verbatim; the error code is bounded evidence.
            diagnostics.Add(
                OffsetStoreUnavailable(
                    "$.offsetStorageTopic",
                    observedAt,
                    "CDC Connect offset-store evidence is unavailable from the broker.",
                    observed: exception.Error.Code.ToString()
                )
            );
            return Unresolved(context, controlOptions, observedAt, diagnostics);
        }
    }

    /// <summary>
    /// Resolves the binding-governed Kafka topics named by <paramref name="inventory"/>, creating each
    /// when absent under <see cref="CdcKafkaProvisioningMode.CreateOrValidate"/> and validating the
    /// actual partition count, cleanup policy, durability, and explicit per-topic overrides against the
    /// deployment policy. Repeated execution is idempotent.
    /// </summary>
    /// <remarks>
    /// Broker defaults are never relied on: every governed override must be an explicit topic-level
    /// value. A topic whose evidence cannot be obtained reports
    /// <see cref="CdcKafkaPolicyItemState.Unknown"/>, never <see cref="CdcKafkaPolicyItemState.Satisfied"/>.
    ///
    /// Internal rather than part of <see cref="ICdcKafkaAdmin"/>: no control-plane path resolves topics
    /// on their own, and every production pass reaches this through
    /// <see cref="EnsureBindingKafkaPolicyAsync"/> or <see cref="DescribeBindingKafkaPolicyAsync"/>,
    /// which compose topics, ACLs, and the record-size budget into one validated observation. Keeping it
    /// on the interface obliged every implementer - including two integration-test fakes - to supply a
    /// method the control plane never calls. It stays reachable so topic policy can be verified against
    /// a broker on its own, with the caller naming the provisioning mode it is exercising.
    /// </remarks>
    internal async Task<CdcKafkaBindingTopicPolicies> BindingTopicsAsync(
        CdcArtifactInventory inventory,
        CdcKafkaProvisioningMode mode,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);

        CdcControlOptions controlOptions = options.Value;
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        List<CdcDiagnostic> diagnostics = [];

        if (
            !CdcControlOptions.TryParseDurabilityProfile(
                controlOptions.DurabilityProfile,
                out CdcDurabilityProfile? durabilityProfile
            )
        )
        {
            CdcKafkaTopicSpec publicSpec = PublicTopicSpec(inventory, controlOptions);
            CdcKafkaTopicSpec progressSpec = ProgressTopicSpec(inventory);
            CdcKafkaTopicSpec? historySpec = SchemaHistoryTopicSpec(inventory);

            foreach (
                CdcKafkaTopicSpec spec in (
                    (CdcKafkaTopicSpec?[])[publicSpec, progressSpec, historySpec]
                ).OfType<CdcKafkaTopicSpec>()
            )
            {
                diagnostics.Add(
                    TopicUnavailable(
                        spec,
                        "$.durabilityProfile",
                        observedAt,
                        "CDC Kafka topic durability profile is unrecognized."
                    )
                );
            }

            return new(
                UnresolvedTopic(publicSpec),
                UnresolvedTopic(progressSpec),
                historySpec is null ? null : UnresolvedTopic(historySpec),
                CdcDiagnostic.NormalizeDiagnostics(diagnostics)
            );
        }

        CdcKafkaDurabilityPolicy durability = CdcKafkaDurabilityPolicy.For(durabilityProfile.Value);
        TimeSpan timeout = controlOptions.Timeouts.KafkaAdmin;

        CdcKafkaTopicPolicy publicTopic = await EnsureTopicAsync(
            PublicTopicSpec(inventory, controlOptions),
            durability,
            mode,
            timeout,
            observedAt,
            diagnostics,
            cancellationToken
        );
        CdcKafkaTopicPolicy progressTopic = await EnsureTopicAsync(
            ProgressTopicSpec(inventory),
            durability,
            mode,
            timeout,
            observedAt,
            diagnostics,
            cancellationToken
        );
        CdcKafkaTopicPolicy? schemaHistoryTopic = SchemaHistoryTopicSpec(inventory) is { } schemaHistorySpec
            ? await EnsureTopicAsync(
                schemaHistorySpec,
                durability,
                mode,
                timeout,
                observedAt,
                diagnostics,
                cancellationToken
            )
            : null;

        return new(
            publicTopic,
            progressTopic,
            schemaHistoryTopic,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics)
        );
    }

    public async Task<CdcKafkaRecordSizeEvidence> VerifyRecordSizeAsync(
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);

        CdcControlOptions controlOptions = options.Value;
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        int maxRecordBytes = controlOptions.MaxRecordBytes;
        int producerBufferBytes = DeriveProducerBufferBytes(controlOptions);
        TimeSpan timeout = controlOptions.Timeouts.KafkaAdmin;
        List<CdcDiagnostic> diagnostics = [];

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyDictionary<string, int?> brokerLimits = await ReadBrokerLimitsAsync(timeout);
            IReadOnlyDictionary<string, ConfigEntryResult>? topicEntries = await ReadTopicConfigAsync(
                inventory.TopicName,
                timeout
            );

            int? effectiveMessageMaxBytes =
                (topicEntries is null ? null : ReadInt(topicEntries, MaxMessageBytesConfigName))
                ?? brokerLimits[MessageMaxBytesConfigName];

            // The record-size policy may not carry a budget larger than the message limit it reports,
            // so an effective limit below the budget is reported as unknown rather than invalid. The
            // fault is not lost: the public topic's own policy rejects a missing or wrong per-topic
            // max.message.bytes override, which is where that value is governed.
            if (effectiveMessageMaxBytes is null || effectiveMessageMaxBytes < maxRecordBytes)
            {
                diagnostics.Add(
                    RecordSizeUnavailable(
                        observedAt,
                        "CDC Kafka effective message size limit does not verifiably accept the record-size budget.",
                        MessageMaxBytesConfigName,
                        effectiveMessageMaxBytes?.ToString(CultureInfo.InvariantCulture)
                    )
                );

                return new(
                    new(CdcKafkaPolicyItemState.Unknown, maxRecordBytes, effectiveMessageMaxBytes),
                    producerBufferBytes,
                    CdcDiagnostic.NormalizeDiagnostics(diagnostics)
                );
            }

            bool unknown = false;
            bool invalid = false;

            foreach (string configName in BrokerRequestLimitConfigNames)
            {
                if (brokerLimits[configName] is not { } limit)
                {
                    unknown = true;
                    diagnostics.Add(
                        RecordSizeUnavailable(
                            observedAt,
                            "CDC Kafka broker limit could not be read, so the record-size budget is unverified.",
                            configName,
                            null
                        )
                    );
                    continue;
                }

                if (limit < maxRecordBytes)
                {
                    invalid = true;
                    diagnostics.Add(
                        RecordSizeInvalid(
                            observedAt,
                            "CDC Kafka broker limit is below the record-size budget.",
                            $"{configName}>={maxRecordBytes.ToString(CultureInfo.InvariantCulture)}",
                            limit.ToString(CultureInfo.InvariantCulture)
                        )
                    );
                }
            }

            CdcKafkaPolicyItemState state = CdcKafkaPolicyItemState.Satisfied;
            if (unknown)
            {
                state = CdcKafkaPolicyItemState.Unknown;
            }

            if (invalid)
            {
                state = CdcKafkaPolicyItemState.Invalid;
            }

            return new(
                new(state, maxRecordBytes, effectiveMessageMaxBytes),
                producerBufferBytes,
                CdcDiagnostic.NormalizeDiagnostics(diagnostics)
            );
        }
        catch (KafkaException exception)
        {
            // The broker response body is never surfaced verbatim; the error code is bounded evidence.
            diagnostics.Add(
                RecordSizeUnavailable(
                    observedAt,
                    "CDC Kafka record-size evidence is unavailable from the broker.",
                    null,
                    exception.Error.Code.ToString()
                )
            );

            return new(
                new(CdcKafkaPolicyItemState.Unknown, maxRecordBytes, null),
                producerBufferBytes,
                CdcDiagnostic.NormalizeDiagnostics(diagnostics)
            );
        }
    }

    public Task<CdcKafkaPolicyObservation> EnsureBindingKafkaPolicyAsync(
        CdcObservationContext context,
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    ) =>
        BindingKafkaPolicyAsync(
            context,
            inventory,
            CdcKafkaProvisioningMode.CreateOrValidate,
            cancellationToken
        );

    public Task<CdcKafkaPolicyObservation> DescribeBindingKafkaPolicyAsync(
        CdcObservationContext context,
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    ) =>
        BindingKafkaPolicyAsync(context, inventory, CdcKafkaProvisioningMode.ValidateOnly, cancellationToken);

    private async Task<CdcKafkaPolicyObservation> BindingKafkaPolicyAsync(
        CdcObservationContext context,
        CdcArtifactInventory inventory,
        CdcKafkaProvisioningMode mode,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inventory);

        CdcControlOptions controlOptions = options.Value;
        DateTimeOffset observedAt = timeProvider.GetUtcNow();

        CdcKafkaBindingTopicPolicies topics = await BindingTopicsAsync(inventory, mode, cancellationToken);
        CdcKafkaRecordSizeEvidence recordSize = await VerifyRecordSizeAsync(inventory, cancellationToken);
        CdcKafkaBindingAclPolicies acls = await BindingAclsAsync(inventory, mode, cancellationToken);

        CdcKafkaPolicyItemState[] states =
        [
            topics.PublicTopic.State,
            topics.ProgressTopic.State,
            topics.SchemaHistoryTopic?.State ?? CdcKafkaPolicyItemState.NotApplicable,
            acls.PublicTopicAcls.State,
            acls.ProgressTopicAcls.State,
            acls.SchemaHistoryTopicAcls?.State ?? CdcKafkaPolicyItemState.NotApplicable,
            recordSize.Policy.State,
        ];

        CdcKafkaPolicyState policyState = CdcKafkaPolicyState.Satisfied;
        if (Array.Exists(states, state => state == CdcKafkaPolicyItemState.Unknown))
        {
            policyState = CdcKafkaPolicyState.Unknown;
        }

        if (Array.Exists(states, state => state == CdcKafkaPolicyItemState.Invalid))
        {
            policyState = CdcKafkaPolicyState.Invalid;
        }

        IReadOnlyList<CdcDiagnostic> diagnostics = CdcDiagnostic.NormalizeDiagnostics([
            .. topics.Diagnostics,
            .. acls.Diagnostics,
            .. recordSize.Diagnostics,
        ]);

        CdcKafkaPolicyObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            context.PhysicalSourceFingerprint,
            policyState,
            NullIfBlank(controlOptions.DurabilityProfile) ?? UnspecifiedDurabilityProfile,
            topics.PublicTopic,
            topics.ProgressTopic,
            topics.SchemaHistoryTopic,
            acls.PublicTopicAcls,
            acls.ProgressTopicAcls,
            acls.SchemaHistoryTopicAcls,
            recordSize.Policy,
            diagnostics
        );

        CdcContractValidationResult validation = CdcKafkaPolicyObservationValidator.Validate(
            observation,
            new(context.OperationId, context.TargetIdentity, context.PhysicalSourceFingerprint, observedAt)
        );

        if (validation.Succeeded)
        {
            return observation;
        }

        // An observation that cannot pass its own contract is never returned as a policy verdict.
        return observation with
        {
            PolicyState = CdcKafkaPolicyState.Unknown,
            Diagnostics = CdcDiagnostic.NormalizeDiagnostics([.. diagnostics, .. validation.Diagnostics]),
        };
    }

    public async Task<CdcSqlServerSchemaHistoryEvidence?> ReadSqlServerSchemaHistoryAsync(
        CdcArtifactInventory inventory,
        CdcSqlServerSchemaHistoryEnablementPhase enablementPhase,
        bool connectorCommittedStreamingOffset,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);

        if (
            inventory.Provider != CdcProvider.SqlServer
            || inventory.SchemaHistoryTopicName is not { } topicName
        )
        {
            // A PostgreSQL binding has no schema-history topic, and the classifier returns before it
            // consults the field for that provider, so the topic is never read and no evidence is
            // composed rather than a not-applicable evidence record being invented.
            return null;
        }

        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        TimeSpan timeout = options.Value.Timeouts.KafkaAdmin;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FindTopic(topicName, timeout) is not { } topicMetadata)
            {
                return Evidence(
                    enablementPhase,
                    CdcSqlServerSchemaHistoryState.Missing,
                    SchemaHistoryLost(
                        topicName,
                        observedAt,
                        "CDC SQL Server schema-history topic is absent.",
                        "absent"
                    )
                );
            }

            cancellationToken.ThrowIfCancellationRequested();

            bool? retainsRecords = await RetainsSchemaHistoryRecordsAsync(topicMetadata, timeout);

            if (retainsRecords is null)
            {
                return Evidence(
                    enablementPhase,
                    CdcSqlServerSchemaHistoryState.Unreadable,
                    SchemaHistoryUnavailable(
                        topicName,
                        observedAt,
                        "CDC SQL Server schema-history topic offsets are unavailable from the broker.",
                        topicName
                    )
                );
            }

            if (retainsRecords.Value)
            {
                // The connector writes its schema history during the snapshot, so a topic holding
                // records is the continuous state and carries no diagnostic of its own.
                return Evidence(enablementPhase, CdcSqlServerSchemaHistoryState.Valid);
            }

            // An empty history topic is a loss only against a committed streaming offset the connector
            // would have to replay history to resume from. Without one it has simply not written its
            // history yet, which no offset can decide either way.
            return connectorCommittedStreamingOffset
                ? Evidence(
                    enablementPhase,
                    CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset,
                    SchemaHistoryLost(
                        topicName,
                        observedAt,
                        "CDC SQL Server schema-history topic is empty while the connector has committed a "
                            + "streaming offset.",
                        "empty"
                    )
                )
                : Evidence(
                    enablementPhase,
                    CdcSqlServerSchemaHistoryState.Unknown,
                    SchemaHistoryUnavailable(
                        topicName,
                        observedAt,
                        "CDC SQL Server schema-history topic is empty and the connector has committed no "
                            + "streaming offset to decide it against.",
                        "empty"
                    )
                );
        }
        catch (KafkaException exception)
        {
            // The broker response body is never surfaced verbatim; the error code is bounded evidence.
            return Evidence(
                enablementPhase,
                CdcSqlServerSchemaHistoryState.Unreadable,
                SchemaHistoryUnavailable(
                    topicName,
                    observedAt,
                    "CDC SQL Server schema-history evidence is unavailable from the broker.",
                    exception.Error.Code.ToString()
                )
            );
        }
    }

    /// <summary>
    /// Whether any partition of the topic still holds a record. Null means the bounding offsets could
    /// not be read, which is never reported as an empty topic.
    /// </summary>
    private async Task<bool?> RetainsSchemaHistoryRecordsAsync(TopicMetadata topicMetadata, TimeSpan timeout)
    {
        // Earliest and latest are requested separately so each result set is correlated by the request
        // that produced it: one flat response carrying both would only be separable by comparing values.
        IReadOnlyDictionary<int, long>? earliest = await ReadPartitionOffsetsAsync(
            topicMetadata,
            OffsetSpec.Earliest(),
            timeout
        );
        IReadOnlyDictionary<int, long>? latest = await ReadPartitionOffsetsAsync(
            topicMetadata,
            OffsetSpec.Latest(),
            timeout
        );

        if (earliest is null || latest is null)
        {
            return null;
        }

        bool retainsRecords = false;
        foreach (KeyValuePair<int, long> partition in latest)
        {
            if (!earliest.TryGetValue(partition.Key, out long start))
            {
                return null;
            }

            retainsRecords |= partition.Value > start;
        }

        return retainsRecords;
    }

    /// <summary>
    /// The offset each partition reports for one bound. Null means at least one partition did not
    /// answer with a usable offset, so the topic's contents cannot be decided.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, long>?> ReadPartitionOffsetsAsync(
        TopicMetadata topicMetadata,
        OffsetSpec offsetSpec,
        TimeSpan timeout
    )
    {
        if (topicMetadata.Partitions.Exists(partition => partition.Error.IsError))
        {
            return null;
        }

        List<TopicPartitionOffsetSpec> offsetSpecs =
        [
            .. topicMetadata.Partitions.Select(partition => new TopicPartitionOffsetSpec
            {
                TopicPartition = new(topicMetadata.Topic, new Partition(partition.PartitionId)),
                OffsetSpec = offsetSpec,
            }),
        ];

        if (offsetSpecs.Count == 0)
        {
            return null;
        }

        Dictionary<int, long> offsets = [];
        foreach (
            ListOffsetsResultInfo result in await _topicOffsetReader.ListOffsetsAsync(offsetSpecs, timeout)
        )
        {
            if (
                result?.TopicPartitionOffsetError is not { } entry
                || entry.Error.IsError
                || entry.Offset.IsSpecial
            )
            {
                return null;
            }

            offsets[entry.Partition.Value] = entry.Offset.Value;
        }

        return offsets.Count == offsetSpecs.Count ? offsets : null;
    }

    private static CdcSqlServerSchemaHistoryEvidence Evidence(
        CdcSqlServerSchemaHistoryEnablementPhase enablementPhase,
        CdcSqlServerSchemaHistoryState state,
        CdcDiagnostic? diagnostic = null
    ) =>
        new(enablementPhase, state)
        {
            Diagnostics = diagnostic is null ? [] : CdcDiagnostic.NormalizeDiagnostics([diagnostic]),
        };

    public async Task<IReadOnlyList<CdcGovernedArtifact>> DeleteBindingArtifactsAsync(
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);

        CdcControlOptions controlOptions = options.Value;
        TimeSpan timeout = controlOptions.Timeouts.KafkaAdmin;
        List<CdcGovernedArtifact> artifacts = [];

        await DeleteGovernedTopicAsync(
            inventory.TopicName,
            CdcGovernedArtifactKind.PublicTopic,
            CdcGovernedArtifactKind.PublicTopicAcls,
            controlOptions,
            timeout,
            artifacts,
            cancellationToken
        );
        await DeleteGovernedTopicAsync(
            inventory.ProgressTopicName,
            CdcGovernedArtifactKind.ProgressTopic,
            CdcGovernedArtifactKind.ProgressTopicAcls,
            controlOptions,
            timeout,
            artifacts,
            cancellationToken
        );

        if (inventory.SchemaHistoryTopicName is { } schemaHistoryTopicName)
        {
            await DeleteGovernedTopicAsync(
                schemaHistoryTopicName,
                CdcGovernedArtifactKind.SchemaHistoryTopic,
                CdcGovernedArtifactKind.SchemaHistoryTopicAcls,
                controlOptions,
                timeout,
                artifacts,
                cancellationToken
            );
        }

        return artifacts;
    }

    /// <summary>
    /// Removes one governed topic's grants and then the topic itself, so the topic is never left
    /// readable without its policy. Both outcomes are reported as governed artifacts.
    /// </summary>
    private async Task DeleteGovernedTopicAsync(
        string topicName,
        CdcGovernedArtifactKind topicKind,
        CdcGovernedArtifactKind aclKind,
        CdcControlOptions controlOptions,
        TimeSpan timeout,
        List<CdcGovernedArtifact> artifacts,
        CancellationToken cancellationToken
    )
    {
        if (string.Equals(topicName, controlOptions.ConnectOffsetStorageTopic, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A binding topic must never resolve to the shared Kafka Connect offset store."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        artifacts.Add(await DeleteTopicAclsAsync(topicName, aclKind, controlOptions, timeout));

        cancellationToken.ThrowIfCancellationRequested();

        artifacts.Add(await DeleteTopicAsync(topicName, topicKind, timeout));
    }

    /// <summary>
    /// Deletes only literal grants on this exact topic. A broader pattern is never removed here: it is
    /// not a binding artifact, and revoking it could strip access another binding still depends on.
    /// </summary>
    private async Task<CdcGovernedArtifact> DeleteTopicAclsAsync(
        string topicName,
        CdcGovernedArtifactKind aclKind,
        CdcControlOptions controlOptions,
        TimeSpan timeout
    )
    {
        if (!controlOptions.AclsEnabled)
        {
            return new(
                aclKind,
                topicName,
                CdcCleanupState.NotFound,
                "No governed grant existed because the deployment has no authorizer."
            );
        }

        List<DeleteAclsResult> results = await adminClient.DeleteAclsAsync(
            [
                new AclBindingFilter
                {
                    PatternFilter = new ResourcePatternFilter
                    {
                        Type = ResourceType.Topic,
                        Name = topicName,
                        ResourcePatternType = ResourcePatternType.Literal,
                    },
                    EntryFilter = new AccessControlEntryFilter
                    {
                        Operation = AclOperation.Any,
                        PermissionType = AclPermissionType.Any,
                    },
                },
            ],
            new DeleteAclsOptions { RequestTimeout = timeout }
        );

        int deletedCount = results?.Sum(result => result.AclBindings?.Count ?? 0) ?? 0;

        return deletedCount == 0
            ? new(aclKind, topicName, CdcCleanupState.NotFound, "No governed grant remained.")
            : new(
                aclKind,
                topicName,
                CdcCleanupState.Deleted,
                $"Removed {deletedCount.ToString(CultureInfo.InvariantCulture)} literal governed grants."
            );
    }

    private async Task<CdcGovernedArtifact> DeleteTopicAsync(
        string topicName,
        CdcGovernedArtifactKind topicKind,
        TimeSpan timeout
    )
    {
        if (FindTopic(topicName, timeout) is null)
        {
            return new(topicKind, topicName, CdcCleanupState.NotFound, "Governed topic was already absent.");
        }

        try
        {
            await adminClient.DeleteTopicsAsync(
                [topicName],
                new DeleteTopicsOptions { RequestTimeout = timeout, OperationTimeout = timeout }
            );
        }
        catch (DeleteTopicsException exception)
            when (exception.Results.TrueForAll(result =>
                    result.Error.Code is ErrorCode.NoError or ErrorCode.UnknownTopicOrPart
                )
            )
        {
            // A concurrent teardown removed it first; the artifact is gone either way.
            return new(topicKind, topicName, CdcCleanupState.NotFound, "Governed topic was already absent.");
        }

        return new(topicKind, topicName, CdcCleanupState.Deleted, "Governed topic deleted.");
    }

    /// <summary>
    /// Idempotently provisions and validates the binding's literal topic ACLs and the consumer-group
    /// grants its consumers require, repairing a missing required grant and failing closed on any grant
    /// broader than the binding owns.
    /// </summary>
    /// <remarks>
    /// Internal rather than part of <see cref="ICdcKafkaAdmin"/>: no control-plane path resolves ACLs on
    /// their own, and every production pass reaches this through
    /// <see cref="EnsureBindingKafkaPolicyAsync"/> or <see cref="DescribeBindingKafkaPolicyAsync"/>,
    /// which compose topics, ACLs, and the record-size budget into one validated observation. It stays
    /// reachable so ACL policy can be verified on its own, with the caller naming the provisioning mode
    /// it is exercising.
    /// </remarks>
    internal async Task<CdcKafkaBindingAclPolicies> BindingAclsAsync(
        CdcArtifactInventory inventory,
        CdcKafkaProvisioningMode mode,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);

        CdcControlOptions controlOptions = options.Value;
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        List<CdcDiagnostic> diagnostics = [];

        if (!controlOptions.AclsEnabled)
        {
            diagnostics.Add(
                new CdcDiagnostic(
                    "kafkaAclsNotEnforced",
                    CdcDiagnosticCategory.None,
                    CdcDiagnosticSeverity.Info,
                    CdcDiagnosticComponent.KafkaPolicy,
                    observedAt,
                    "CDC Kafka ACLs were not verified because the deployment has no authorizer.",
                    false,
                    artifactKind: "bindingAcls",
                    artifactName: inventory.TopicName
                ).WithPath("$.publicTopicAcls")
            );

            return BindingAcls(
                inventory,
                CdcKafkaPolicyItemState.NotApplicable,
                CdcKafkaPolicyItemState.NotApplicable,
                CdcKafkaPolicyItemState.NotApplicable,
                diagnostics
            );
        }

        TimeSpan timeout = controlOptions.Timeouts.KafkaAdmin;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            CdcKafkaPolicyItemState publicState = await EnsureResourceAclsAsync(
                ResourceType.Topic,
                inventory.TopicName,
                "publicTopicAcls",
                PublicTopicRequirements(controlOptions),
                mode,
                timeout,
                observedAt,
                diagnostics
            );

            foreach (CdcConsumerOptions consumer in controlOptions.Consumers)
            {
                publicState = Worse(
                    publicState,
                    await EnsureResourceAclsAsync(
                        ResourceType.Group,
                        consumer.ConsumerGroup,
                        "consumerGroupAcls",
                        [new(consumer.Principal, AnyHost, AclOperation.Read)],
                        mode,
                        timeout,
                        observedAt,
                        diagnostics
                    )
                );
            }

            publicState = Worse(
                publicState,
                await VerifyConsumerIsolationAsync(
                    inventory,
                    controlOptions,
                    timeout,
                    observedAt,
                    diagnostics
                )
            );

            cancellationToken.ThrowIfCancellationRequested();

            CdcKafkaPolicyItemState progressState = await EnsureResourceAclsAsync(
                ResourceType.Topic,
                inventory.ProgressTopicName,
                "progressTopicAcls",
                ProducerRequirements(controlOptions),
                mode,
                timeout,
                observedAt,
                diagnostics
            );

            CdcKafkaPolicyItemState? schemaHistoryState = inventory.SchemaHistoryTopicName
                is { } schemaHistoryTopicName
                ? await EnsureResourceAclsAsync(
                    ResourceType.Topic,
                    schemaHistoryTopicName,
                    "schemaHistoryTopicAcls",
                    SchemaHistoryRequirements(controlOptions),
                    mode,
                    timeout,
                    observedAt,
                    diagnostics
                )
                : null;

            return BindingAcls(inventory, publicState, progressState, schemaHistoryState, diagnostics);
        }
        catch (KafkaException exception)
        {
            // The broker response body is never surfaced verbatim; the error code is bounded evidence.
            diagnostics.Add(
                AclUnavailable(
                    "bindingAcls",
                    inventory.TopicName,
                    "$.publicTopicAcls",
                    observedAt,
                    "CDC Kafka ACL evidence is unavailable from the broker.",
                    exception.Error.Code.ToString()
                )
            );

            return BindingAcls(
                inventory,
                CdcKafkaPolicyItemState.Unknown,
                CdcKafkaPolicyItemState.Unknown,
                CdcKafkaPolicyItemState.Unknown,
                diagnostics
            );
        }
    }

    /// <summary>Connector producer access: write the record and describe the topic it writes to.</summary>
    private static CdcKafkaAclGrant[] ProducerRequirements(CdcControlOptions controlOptions) =>
        [
            new(controlOptions.ConnectorPrincipal, AnyHost, AclOperation.Write),
            new(controlOptions.ConnectorPrincipal, AnyHost, AclOperation.Describe),
        ];

    /// <summary>
    /// The public topic carries the connector's producer grants plus read access for each
    /// deployment-supplied instance consumer, and nothing else.
    /// </summary>
    private static CdcKafkaAclGrant[] PublicTopicRequirements(CdcControlOptions controlOptions) =>
        [
            .. ProducerRequirements(controlOptions),
            .. controlOptions.Consumers.SelectMany(consumer =>
                new CdcKafkaAclGrant[]
                {
                    new(consumer.Principal, AnyHost, AclOperation.Read),
                    new(consumer.Principal, AnyHost, AclOperation.Describe),
                }
            ),
        ];

    /// <summary>
    /// The SQL Server schema-history topic is connector-internal state: the connector replays it, so it
    /// needs read, write, and both describe forms, and no instance consumer receives any access.
    /// </summary>
    private static CdcKafkaAclGrant[] SchemaHistoryRequirements(CdcControlOptions controlOptions) =>
        [
            new(controlOptions.ConnectorPrincipal, AnyHost, AclOperation.Read),
            new(controlOptions.ConnectorPrincipal, AnyHost, AclOperation.Write),
            new(controlOptions.ConnectorPrincipal, AnyHost, AclOperation.Describe),
            new(controlOptions.ConnectorPrincipal, AnyHost, AclOperation.DescribeConfigs),
        ];

    private async Task<CdcKafkaPolicyItemState> EnsureResourceAclsAsync(
        ResourceType resourceType,
        string resourceName,
        string artifactKind,
        IReadOnlyList<CdcKafkaAclGrant> required,
        CdcKafkaProvisioningMode mode,
        TimeSpan timeout,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        // A MATCH filter also returns wildcard and prefixed patterns covering this resource, so an
        // over-broad grant cannot hide behind a literal-only query.
        DescribeAclsResult result = await adminClient.DescribeAclsAsync(
            MatchFilter(resourceType, resourceName),
            new DescribeAclsOptions { RequestTimeout = timeout }
        );

        if (result?.AclBindings is not { } bindings)
        {
            diagnostics.Add(
                AclUnavailable(
                    artifactKind,
                    resourceName,
                    $"$.{artifactKind}",
                    observedAt,
                    "CDC Kafka ACL evidence is unavailable for a governed resource.",
                    null
                )
            );
            return CdcKafkaPolicyItemState.Unknown;
        }

        HashSet<CdcKafkaAclGrant> observed = [];
        bool invalid = false;

        foreach (AclBinding binding in bindings)
        {
            if (
                binding.Pattern.ResourcePatternType != ResourcePatternType.Literal
                || !string.Equals(binding.Pattern.Name, resourceName, StringComparison.Ordinal)
            )
            {
                invalid = true;
                diagnostics.Add(
                    AclInvalid(
                        artifactKind,
                        binding.Pattern.Name,
                        $"$.{artifactKind}",
                        observedAt,
                        "CDC Kafka ACLs must not include an over-broad resource pattern.",
                        resourceName,
                        binding.Pattern.ResourcePatternType.ToString()
                    )
                );
                continue;
            }

            CdcKafkaAclGrant grant = new(
                binding.Entry.Principal,
                binding.Entry.Host,
                binding.Entry.Operation
            );

            if (binding.Entry.PermissionType != AclPermissionType.Allow || !required.Contains(grant))
            {
                invalid = true;
                diagnostics.Add(
                    AclInvalid(
                        artifactKind,
                        resourceName,
                        $"$.{artifactKind}",
                        observedAt,
                        "CDC Kafka resource carries a grant the binding does not require.",
                        resourceName,
                        binding.Entry.Operation.ToString()
                    )
                );
                continue;
            }

            observed.Add(grant);
        }

        List<AclBinding> missing =
        [
            .. required
                .Where(grant => !observed.Contains(grant))
                .Select(grant => ToAclBinding(resourceType, resourceName, grant)),
        ];

        if (missing.Count != 0 && mode == CdcKafkaProvisioningMode.ValidateOnly)
        {
            // A grant the binding requires and the resource does not hold is reported as the
            // nonconformance it is, because this pass verifies rather than repairs.
            invalid = true;
            diagnostics.Add(
                AclInvalid(
                    artifactKind,
                    resourceName,
                    $"$.{artifactKind}",
                    observedAt,
                    "CDC Kafka resource is missing a grant the binding requires.",
                    resourceName,
                    "missing grant"
                )
            );
        }
        else if (missing.Count != 0)
        {
            await adminClient.CreateAclsAsync(missing, new CreateAclsOptions { RequestTimeout = timeout });
            logger.LogDebug(
                "Repaired {GrantCount} missing CDC Kafka grants on {ResourceName}.",
                missing.Count,
                resourceName
            );
        }

        return invalid ? CdcKafkaPolicyItemState.Invalid : CdcKafkaPolicyItemState.Satisfied;
    }

    /// <summary>
    /// Sweeps every topic and group grant held by each configured instance consumer. The only grants a
    /// consumer may hold are read and describe on this binding's public topic and read on its own
    /// consumer group; access to another instance's topic, to any progress topic, or through a
    /// non-literal pattern fails closed.
    /// </summary>
    private async Task<CdcKafkaPolicyItemState> VerifyConsumerIsolationAsync(
        CdcArtifactInventory inventory,
        CdcControlOptions controlOptions,
        TimeSpan timeout,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        CdcKafkaPolicyItemState state = CdcKafkaPolicyItemState.Satisfied;

        foreach (CdcConsumerOptions consumer in controlOptions.Consumers)
        {
            state = Worse(
                state,
                await VerifyConsumerResourceIsolationAsync(
                    ResourceType.Topic,
                    inventory.TopicName,
                    consumer.Principal,
                    timeout,
                    observedAt,
                    diagnostics
                )
            );
            state = Worse(
                state,
                await VerifyConsumerResourceIsolationAsync(
                    ResourceType.Group,
                    consumer.ConsumerGroup,
                    consumer.Principal,
                    timeout,
                    observedAt,
                    diagnostics
                )
            );
        }

        return state;
    }

    private async Task<CdcKafkaPolicyItemState> VerifyConsumerResourceIsolationAsync(
        ResourceType resourceType,
        string permittedResourceName,
        string principal,
        TimeSpan timeout,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        DescribeAclsResult result = await adminClient.DescribeAclsAsync(
            new AclBindingFilter
            {
                PatternFilter = new ResourcePatternFilter
                {
                    Type = resourceType,
                    Name = null,
                    ResourcePatternType = ResourcePatternType.Any,
                },
                EntryFilter = new AccessControlEntryFilter
                {
                    Principal = principal,
                    Operation = AclOperation.Any,
                    PermissionType = AclPermissionType.Any,
                },
            },
            new DescribeAclsOptions { RequestTimeout = timeout }
        );

        if (result?.AclBindings is not { } bindings)
        {
            diagnostics.Add(
                AclUnavailable(
                    "publicTopicAcls",
                    permittedResourceName,
                    "$.publicTopicAcls",
                    observedAt,
                    "CDC Kafka consumer isolation evidence is unavailable.",
                    null
                )
            );
            return CdcKafkaPolicyItemState.Unknown;
        }

        CdcKafkaPolicyItemState state = CdcKafkaPolicyItemState.Satisfied;

        foreach (ResourcePattern pattern in bindings.Select(binding => binding.Pattern))
        {
            if (
                pattern.ResourcePatternType == ResourcePatternType.Literal
                && string.Equals(pattern.Name, permittedResourceName, StringComparison.Ordinal)
            )
            {
                continue;
            }

            state = CdcKafkaPolicyItemState.Invalid;
            diagnostics.Add(
                AclInvalid(
                    "publicTopicAcls",
                    pattern.Name,
                    "$.publicTopicAcls",
                    observedAt,
                    "CDC Kafka instance consumer must hold no grant outside its own binding.",
                    permittedResourceName,
                    pattern.Name
                )
            );
        }

        return state;
    }

    private static CdcKafkaBindingAclPolicies BindingAcls(
        CdcArtifactInventory inventory,
        CdcKafkaPolicyItemState publicState,
        CdcKafkaPolicyItemState progressState,
        CdcKafkaPolicyItemState? schemaHistoryState,
        List<CdcDiagnostic> diagnostics
    ) =>
        new(
            new(inventory.TopicName, publicState),
            new(inventory.ProgressTopicName, progressState),
            inventory.SchemaHistoryTopicName is { } schemaHistoryTopicName
                ? new(schemaHistoryTopicName, schemaHistoryState ?? CdcKafkaPolicyItemState.Unknown)
                : null,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics)
        );

    private static AclBindingFilter MatchFilter(ResourceType resourceType, string resourceName) =>
        new()
        {
            PatternFilter = new ResourcePatternFilter
            {
                Type = resourceType,
                Name = resourceName,
                ResourcePatternType = ResourcePatternType.Match,
            },
            EntryFilter = new AccessControlEntryFilter
            {
                Operation = AclOperation.Any,
                PermissionType = AclPermissionType.Any,
            },
        };

    private static AclBinding ToAclBinding(
        ResourceType resourceType,
        string resourceName,
        CdcKafkaAclGrant grant
    ) =>
        new()
        {
            Pattern = new ResourcePattern
            {
                Type = resourceType,
                Name = resourceName,
                ResourcePatternType = ResourcePatternType.Literal,
            },
            Entry = new AccessControlEntry
            {
                Principal = grant.Principal,
                Host = grant.Host,
                Operation = grant.Operation,
                PermissionType = AclPermissionType.Allow,
            },
        };

    private static CdcKafkaPolicyItemState Worse(
        CdcKafkaPolicyItemState current,
        CdcKafkaPolicyItemState candidate
    )
    {
        if (current == CdcKafkaPolicyItemState.Invalid || candidate == CdcKafkaPolicyItemState.Invalid)
        {
            return CdcKafkaPolicyItemState.Invalid;
        }

        return current == CdcKafkaPolicyItemState.Unknown || candidate == CdcKafkaPolicyItemState.Unknown
            ? CdcKafkaPolicyItemState.Unknown
            : current;
    }

    /// <summary>
    /// The producer buffer the connector is rendered with: the operator's value when supplied,
    /// otherwise the greater of the fixed minimum and the record-size budget.
    /// </summary>
    internal static int DeriveProducerBufferBytes(CdcControlOptions controlOptions) =>
        controlOptions.ProducerBufferBytes
        ?? Math.Max(
            CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes,
            controlOptions.MaxRecordBytes
        );

    /// <summary>
    /// The least value each limit takes across every broker, so one under-configured broker cannot be
    /// masked by its peers. A limit that any broker fails to report is left unresolved.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int?>> ReadBrokerLimitsAsync(TimeSpan timeout)
    {
        string[] configNames = [MessageMaxBytesConfigName, .. BrokerRequestLimitConfigNames];
        Dictionary<string, int?> limits = new(StringComparer.Ordinal);

        List<IReadOnlyDictionary<string, ConfigEntryResult>?> brokerConfigs = [];
        foreach (BrokerMetadata broker in adminClient.GetMetadata(timeout).Brokers ?? [])
        {
            brokerConfigs.Add(
                await ReadConfigAsync(
                    ResourceType.Broker,
                    broker.BrokerId.ToString(CultureInfo.InvariantCulture),
                    timeout
                )
            );
        }

        foreach (string configName in configNames)
        {
            int? minimum = null;
            foreach (IReadOnlyDictionary<string, ConfigEntryResult>? entries in brokerConfigs)
            {
                if ((entries is null ? null : ReadInt(entries, configName)) is not { } value)
                {
                    minimum = null;
                    break;
                }

                minimum = minimum is null ? value : Math.Min(minimum.Value, value);
            }

            limits[configName] = brokerConfigs.Count == 0 ? null : minimum;
        }

        return limits;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, ConfigEntryResult> entries, string configName) =>
        entries.TryGetValue(configName, out ConfigEntryResult? entry)
        && int.TryParse(entry?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    /// <summary>
    /// The public instance document topic: compact-only, with the binding's fixed partition count, an
    /// explicit tombstone-retention floor of seven days, and the operational record-size ceiling.
    /// </summary>
    private static CdcKafkaTopicSpec PublicTopicSpec(
        CdcArtifactInventory inventory,
        CdcControlOptions controlOptions
    ) =>
        new(
            inventory.TopicName,
            "publicTopic",
            controlOptions.PartitionCount,
            CompactCleanupPolicy,
            [
                new(
                    DeleteRetentionConfigName,
                    MinimumDeleteRetentionMilliseconds,
                    CdcKafkaConfigComparison.AtLeast
                ),
                new(
                    MaxMessageBytesConfigName,
                    controlOptions.MaxRecordBytes,
                    CdcKafkaConfigComparison.Exactly
                ),
            ]
        );

    /// <summary>
    /// The derived internal progress topic: one partition and compaction, so the latest published
    /// progress record is retained. It carries no record-size or consumer-bootstrap contract.
    /// </summary>
    private static CdcKafkaTopicSpec ProgressTopicSpec(CdcArtifactInventory inventory) =>
        new(inventory.ProgressTopicName, "progressTopic", 1, CompactCleanupPolicy, []);

    /// <summary>
    /// The derived SQL Server schema-history topic: one partition, delete cleanup, and infinite
    /// retention. Compaction or any finite retention could drop a DDL record the connector must replay
    /// to reconstruct the schema at a retained source offset.
    /// </summary>
    private static CdcKafkaTopicSpec? SchemaHistoryTopicSpec(CdcArtifactInventory inventory) =>
        inventory.Provider == CdcProvider.SqlServer && inventory.SchemaHistoryTopicName is { } topicName
            ? new(
                topicName,
                "schemaHistoryTopic",
                1,
                DeleteCleanupPolicy,
                [
                    new(RetentionMillisecondsConfigName, InfiniteRetention, CdcKafkaConfigComparison.Exactly),
                    new(RetentionBytesConfigName, InfiniteRetention, CdcKafkaConfigComparison.Exactly),
                ]
            )
            : null;

    private async Task<CdcKafkaTopicPolicy> EnsureTopicAsync(
        CdcKafkaTopicSpec spec,
        CdcKafkaDurabilityPolicy durability,
        CdcKafkaProvisioningMode mode,
        TimeSpan timeout,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            TopicMetadata? topicMetadata = FindTopic(spec.TopicName, timeout);
            if (topicMetadata is null && mode == CdcKafkaProvisioningMode.CreateOrValidate)
            {
                await CreateTopicAsync(
                    spec.TopicName,
                    spec.PartitionCount,
                    spec.CleanupPolicy,
                    durability,
                    spec.ConfigRules,
                    timeout
                );
                topicMetadata = FindTopic(spec.TopicName, timeout);
            }

            if (topicMetadata is null && mode == CdcKafkaProvisioningMode.ValidateOnly)
            {
                // The topic is reported absent rather than created: a pass that provisions is not a
                // verification of what the deployment already holds.
                diagnostics.Add(
                    TopicUnavailable(
                        spec,
                        "$.topicName",
                        observedAt,
                        "CDC Kafka governed topic does not exist.",
                        "absent"
                    )
                );

                return UnresolvedTopic(spec);
            }

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyDictionary<string, ConfigEntryResult>? entries = topicMetadata is null
                ? null
                : await ReadTopicConfigAsync(spec.TopicName, timeout);

            return EvaluateTopic(
                spec,
                durability,
                topicMetadata?.Partitions.Count,
                ResolveReplicationFactor(topicMetadata),
                entries,
                observedAt,
                diagnostics
            );
        }
        catch (KafkaException exception)
        {
            // The broker response body is never surfaced verbatim; the error code is bounded evidence.
            diagnostics.Add(
                TopicUnavailable(
                    spec,
                    "$.topicName",
                    observedAt,
                    "CDC Kafka topic evidence is unavailable from the broker.",
                    exception.Error.Code.ToString()
                )
            );
            return UnresolvedTopic(spec);
        }
    }

    private static CdcKafkaTopicPolicy EvaluateTopic(
        CdcKafkaTopicSpec spec,
        CdcKafkaDurabilityPolicy durability,
        int? partitionCount,
        int? replicationFactor,
        IReadOnlyDictionary<string, ConfigEntryResult>? entries,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        string? cleanupPolicy = entries is null ? null : ReadCleanupPolicy(entries);
        (int? minInSyncReplicas, bool minInSyncReplicasIsTopicLevel) = entries is null
            ? (null, false)
            : ReadMinInSyncReplicas(entries);

        if (
            entries is null
            || partitionCount is null
            || replicationFactor is null
            || cleanupPolicy is null
            || minInSyncReplicas is null
        )
        {
            diagnostics.Add(
                TopicUnavailable(
                    spec,
                    "$.state",
                    observedAt,
                    "CDC Kafka topic policy evidence is incomplete."
                )
            );
            return UnresolvedTopic(spec);
        }

        string reportedCleanupPolicy = ToReportedCleanupPolicy(cleanupPolicy, spec.CleanupPolicy);
        bool invalid = false;

        if (partitionCount != spec.PartitionCount)
        {
            invalid = true;
            diagnostics.Add(
                TopicInvalid(
                    spec,
                    "$.partitionCount",
                    observedAt,
                    "CDC Kafka topic partition count does not match the governed value.",
                    spec.PartitionCount.ToString(CultureInfo.InvariantCulture),
                    partitionCount.Value.ToString(CultureInfo.InvariantCulture)
                )
            );
        }

        if (!string.Equals(reportedCleanupPolicy, spec.CleanupPolicy, StringComparison.Ordinal))
        {
            invalid = true;
            diagnostics.Add(
                TopicInvalid(
                    spec,
                    "$.cleanupPolicy",
                    observedAt,
                    "CDC Kafka topic cleanup policy does not match the governed value.",
                    spec.CleanupPolicy,
                    cleanupPolicy
                )
            );
        }

        if (replicationFactor < durability.ReplicationFactor)
        {
            invalid = true;
            diagnostics.Add(
                TopicInvalid(
                    spec,
                    "$.replicationFactor",
                    observedAt,
                    "CDC Kafka topic replica count is below the active durability profile.",
                    durability.ReplicationFactor.ToString(CultureInfo.InvariantCulture),
                    replicationFactor.Value.ToString(CultureInfo.InvariantCulture)
                )
            );
        }

        if (!minInSyncReplicasIsTopicLevel)
        {
            invalid = true;
            diagnostics.Add(
                TopicInvalid(
                    spec,
                    "$.minInSyncReplicas",
                    observedAt,
                    "CDC Kafka topic requires an explicit topic-level minimum in-sync replica override.",
                    MinInSyncReplicasConfigName,
                    spec.TopicName
                )
            );
        }
        else if (minInSyncReplicas < durability.MinInSyncReplicas)
        {
            invalid = true;
            diagnostics.Add(
                TopicInvalid(
                    spec,
                    "$.minInSyncReplicas",
                    observedAt,
                    "CDC Kafka topic minimum in-sync replicas is below the active durability profile.",
                    durability.MinInSyncReplicas.ToString(CultureInfo.InvariantCulture),
                    minInSyncReplicas.Value.ToString(CultureInfo.InvariantCulture)
                )
            );
        }

        foreach (CdcKafkaTopicConfigRule rule in spec.ConfigRules)
        {
            invalid |= !SatisfiesConfigRule(spec, rule, entries, observedAt, diagnostics);
        }

        return new(
            spec.TopicName,
            invalid ? CdcKafkaPolicyItemState.Invalid : CdcKafkaPolicyItemState.Satisfied,
            partitionCount,
            reportedCleanupPolicy,
            replicationFactor,
            minInSyncReplicas
        );
    }

    /// <summary>
    /// A governed override must be present as an explicit topic-level value. A missing override is
    /// rejected even when the current broker default would satisfy the rule, because a later broker
    /// change would silently move the topic out of policy.
    /// </summary>
    private static bool SatisfiesConfigRule(
        CdcKafkaTopicSpec spec,
        CdcKafkaTopicConfigRule rule,
        IReadOnlyDictionary<string, ConfigEntryResult> entries,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        string expected =
            rule.Comparison == CdcKafkaConfigComparison.AtLeast
                ? $">={rule.Value.ToString(CultureInfo.InvariantCulture)}"
                : rule.Value.ToString(CultureInfo.InvariantCulture);

        if (
            !entries.TryGetValue(rule.Name, out ConfigEntryResult? entry)
            || entry?.Source != ConfigSource.DynamicTopicConfig
        )
        {
            diagnostics.Add(
                TopicInvalid(
                    spec,
                    $"$.{rule.Name}",
                    observedAt,
                    "CDC Kafka topic requires an explicit topic-level override.",
                    $"{rule.Name}{expected}",
                    spec.TopicName
                )
            );
            return false;
        }

        if (
            !long.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            || (rule.Comparison == CdcKafkaConfigComparison.Exactly && value != rule.Value)
            || (rule.Comparison == CdcKafkaConfigComparison.AtLeast && value < rule.Value)
        )
        {
            diagnostics.Add(
                TopicInvalid(
                    spec,
                    $"$.{rule.Name}",
                    observedAt,
                    "CDC Kafka topic override does not satisfy the governed value.",
                    $"{rule.Name}{expected}",
                    entry.Value
                )
            );
            return false;
        }

        return true;
    }

    private static CdcKafkaTopicPolicy UnresolvedTopic(CdcKafkaTopicSpec spec) =>
        new(spec.TopicName, CdcKafkaPolicyItemState.Unknown, null, null, null, null);

    /// <summary>
    /// Reads whole-cluster metadata rather than single-topic metadata: a topic-scoped request can
    /// provoke broker auto-creation, and a governed topic must never be created by a side effect.
    /// </summary>
    private TopicMetadata? FindTopic(string topicName, TimeSpan timeout)
    {
        Metadata metadata = adminClient.GetMetadata(timeout);

        return metadata.Topics?.Find(topic =>
            string.Equals(topic.Topic, topicName, StringComparison.Ordinal)
            && !topic.Error.IsError
            && topic.Partitions is { Count: > 0 }
        );
    }

    /// <summary>
    /// Creates a governed topic with every policy value set explicitly, so no governed property is ever
    /// left to a broker default.
    /// </summary>
    private async Task CreateTopicAsync(
        string topicName,
        int partitionCount,
        string cleanupPolicy,
        CdcKafkaDurabilityPolicy durability,
        IReadOnlyList<CdcKafkaTopicConfigRule> configRules,
        TimeSpan timeout
    )
    {
        Dictionary<string, string> configs = new(StringComparer.Ordinal)
        {
            [CleanupPolicyConfigName] = cleanupPolicy,
            [MinInSyncReplicasConfigName] = durability.MinInSyncReplicas.ToString(
                CultureInfo.InvariantCulture
            ),
        };

        foreach (CdcKafkaTopicConfigRule rule in configRules)
        {
            configs[rule.Name] = rule.Value.ToString(CultureInfo.InvariantCulture);
        }

        TopicSpecification specification = new()
        {
            Name = topicName,
            NumPartitions = partitionCount,
            ReplicationFactor = durability.ReplicationFactor,
            Configs = configs,
        };

        try
        {
            await adminClient.CreateTopicsAsync(
                [specification],
                new CreateTopicsOptions { RequestTimeout = timeout, OperationTimeout = timeout }
            );
            logger.LogDebug("Created the governed Kafka topic {TopicName}.", topicName);
        }
        catch (CreateTopicsException exception)
            when (exception.Results.TrueForAll(result =>
                    result.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists
                )
            )
        {
            // A concurrent worker or control-plane run won the race, and validation of the existing
            // topic follows. The exception itself is not logged: its message carries broker detail.
        }
    }

    /// <summary>
    /// The conservative replica count across partitions, so a single under-replicated partition cannot
    /// be masked by a better-replicated sibling.
    /// </summary>
    private static int? ResolveReplicationFactor(TopicMetadata? topicMetadata)
    {
        if (topicMetadata?.Partitions is not { Count: > 0 } partitions)
        {
            return null;
        }

        int replicationFactor = int.MaxValue;
        foreach (PartitionMetadata partition in partitions)
        {
            if (partition.Error.IsError || partition.Replicas is null)
            {
                return null;
            }

            replicationFactor = Math.Min(replicationFactor, partition.Replicas.Length);
        }

        return replicationFactor > 0 ? replicationFactor : null;
    }

    private Task<IReadOnlyDictionary<string, ConfigEntryResult>?> ReadTopicConfigAsync(
        string topicName,
        TimeSpan timeout
    ) => ReadConfigAsync(ResourceType.Topic, topicName, timeout);

    private async Task<IReadOnlyDictionary<string, ConfigEntryResult>?> ReadConfigAsync(
        ResourceType resourceType,
        string resourceName,
        TimeSpan timeout
    )
    {
        // One resource per request: the 2.6 result carries entries only, with no resource correlation.
        List<DescribeConfigsResult> results = await adminClient.DescribeConfigsAsync(
            [new ConfigResource { Type = resourceType, Name = resourceName }],
            new DescribeConfigsOptions { RequestTimeout = timeout }
        );

        return results is { Count: > 0 } ? results[0]?.Entries : null;
    }

    private static CdcOffsetStoreConfigEvidence ToOffsetStoreConfigEvidence(
        IReadOnlyDictionary<string, ConfigEntryResult>? entries
    )
    {
        if (entries is null)
        {
            return CdcOffsetStoreConfigEvidence.Unresolved;
        }

        (int? minInSyncReplicas, bool isTopicLevel) = ReadMinInSyncReplicas(entries);

        return new(ReadCleanupPolicy(entries), minInSyncReplicas, isTopicLevel);
    }

    private static string? ReadCleanupPolicy(IReadOnlyDictionary<string, ConfigEntryResult> entries) =>
        entries.TryGetValue(CleanupPolicyConfigName, out ConfigEntryResult? cleanupPolicy)
            ? NullIfBlank(cleanupPolicy?.Value?.Trim())
            : null;

    /// <summary>
    /// Reads the minimum in-sync replica override. The second value separates an explicit topic-level
    /// override from a value inherited from a broker default, which no governed topic may rely on.
    /// </summary>
    private static (int? Value, bool IsTopicLevel) ReadMinInSyncReplicas(
        IReadOnlyDictionary<string, ConfigEntryResult> entries
    )
    {
        if (!entries.TryGetValue(MinInSyncReplicasConfigName, out ConfigEntryResult? minInSyncReplicas))
        {
            return (null, false);
        }

        return (
            int.TryParse(
                minInSyncReplicas?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed
            )
                ? parsed
                : null,
            minInSyncReplicas?.Source == ConfigSource.DynamicTopicConfig
        );
    }

    /// <summary>
    /// Verifies that the deployment grants the Connect worker principal literal <c>READ</c>,
    /// <c>WRITE</c>, and <c>DESCRIBE</c> on the offset store and grants no other principal any access.
    /// ACL administration stays with the deployment control plane, so this validates and never repairs.
    /// </summary>
    private async Task<CdcConnectOffsetStoreItemState> ResolveAclStateAsync(
        string topicName,
        CdcControlOptions controlOptions,
        TimeSpan timeout,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        if (!controlOptions.AclsEnabled)
        {
            // The offset-store item state has no not-applicable value, so the informational diagnostic
            // is the only record that no authorizer was available to verify against.
            diagnostics.Add(
                new CdcDiagnostic(
                    "connectOffsetStoreAclsNotEnforced",
                    CdcDiagnosticCategory.None,
                    CdcDiagnosticSeverity.Info,
                    CdcDiagnosticComponent.ConnectOffsetStore,
                    observedAt,
                    "CDC Connect offset-store ACLs were not verified because the deployment has no authorizer.",
                    false,
                    artifactKind: "connectOffsetStore",
                    artifactName: topicName
                ).WithPath("$.aclState")
            );
            return CdcConnectOffsetStoreItemState.Satisfied;
        }

        // A MATCH filter also returns wildcard and prefixed patterns covering this topic, so an
        // over-broad grant cannot hide behind a literal-only query.
        DescribeAclsResult result = await adminClient.DescribeAclsAsync(
            new AclBindingFilter
            {
                PatternFilter = new ResourcePatternFilter
                {
                    Type = ResourceType.Topic,
                    Name = topicName,
                    ResourcePatternType = ResourcePatternType.Match,
                },
                EntryFilter = new AccessControlEntryFilter
                {
                    Operation = AclOperation.Any,
                    PermissionType = AclPermissionType.Any,
                },
            },
            new DescribeAclsOptions { RequestTimeout = timeout }
        );

        if (result?.AclBindings is not { } bindings)
        {
            diagnostics.Add(
                OffsetStoreUnavailable(
                    "$.aclState",
                    observedAt,
                    "CDC Connect offset-store ACL evidence is unavailable.",
                    observed: topicName
                )
            );
            return CdcConnectOffsetStoreItemState.Unknown;
        }

        HashSet<AclOperation> workerOperations = [];
        bool invalid = false;

        foreach (AclBinding binding in bindings)
        {
            if (
                binding.Pattern.ResourcePatternType != ResourcePatternType.Literal
                || !string.Equals(binding.Pattern.Name, topicName, StringComparison.Ordinal)
            )
            {
                invalid = true;
                diagnostics.Add(
                    OffsetStoreInvalid(
                        "$.aclState",
                        observedAt,
                        "CDC Connect offset-store ACLs must not include an over-broad topic pattern.",
                        topicName,
                        binding.Pattern.ResourcePatternType.ToString()
                    )
                );
                continue;
            }

            if (
                binding.Entry.PermissionType != AclPermissionType.Allow
                || !string.Equals(
                    binding.Entry.Principal,
                    controlOptions.ConnectWorkerPrincipal,
                    StringComparison.Ordinal
                )
            )
            {
                invalid = true;
                diagnostics.Add(
                    OffsetStoreInvalid(
                        "$.aclState",
                        observedAt,
                        "CDC Connect offset-store grants only the Connect worker principal.",
                        topicName,
                        binding.Entry.Operation.ToString()
                    )
                );
                continue;
            }

            // The host belongs to the grant as much as the principal does. A grant admitting the worker
            // from one host does not admit the worker running anywhere else, so counting it toward the
            // required operations would report a satisfied offset store the worker cannot actually
            // reach. This pass validates and never repairs, so the nonconformance is all it can report.
            if (!string.Equals(binding.Entry.Host, AnyHost, StringComparison.Ordinal))
            {
                invalid = true;
                diagnostics.Add(
                    OffsetStoreInvalid(
                        "$.aclState",
                        observedAt,
                        "CDC Connect offset-store grants must admit the Connect worker from any host.",
                        AnyHost,
                        binding.Entry.Host
                    )
                );
                continue;
            }

            workerOperations.Add(binding.Entry.Operation);
        }

        foreach (AclOperation operation in RequiredWorkerOperations)
        {
            if (workerOperations.Remove(operation))
            {
                continue;
            }

            invalid = true;
            diagnostics.Add(
                OffsetStoreInvalid(
                    "$.aclState",
                    observedAt,
                    "CDC Connect offset-store is missing a required Connect worker grant.",
                    operation.ToString(),
                    topicName
                )
            );
        }

        foreach (AclOperation operation in workerOperations)
        {
            invalid = true;
            diagnostics.Add(
                OffsetStoreInvalid(
                    "$.aclState",
                    observedAt,
                    "CDC Connect offset-store grants the Connect worker more than read, write, and describe.",
                    topicName,
                    operation.ToString()
                )
            );
        }

        return invalid ? CdcConnectOffsetStoreItemState.Invalid : CdcConnectOffsetStoreItemState.Satisfied;
    }

    private CdcConnectOffsetStorePolicyObservation Evaluate(
        CdcObservationContext context,
        CdcControlOptions controlOptions,
        CdcKafkaDurabilityPolicy durability,
        int? replicationFactor,
        CdcOffsetStoreConfigEvidence configEvidence,
        CdcConnectOffsetStoreItemState aclState,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        string topicName = controlOptions.ConnectOffsetStorageTopic;

        if (
            configEvidence.CleanupPolicy is null
            || replicationFactor is null
            || configEvidence.MinInSyncReplicas is null
            || aclState == CdcConnectOffsetStoreItemState.Unknown
        )
        {
            diagnostics.Add(
                OffsetStoreUnavailable(
                    "$.policyState",
                    observedAt,
                    "CDC Connect offset-store policy evidence is incomplete.",
                    observed: topicName
                )
            );
            return Build(
                context,
                controlOptions,
                CdcConnectOffsetStorePolicyState.Unknown,
                configEvidence.CleanupPolicy,
                replicationFactor,
                configEvidence.MinInSyncReplicas,
                aclState,
                observedAt,
                diagnostics
            );
        }

        bool invalid = aclState == CdcConnectOffsetStoreItemState.Invalid;

        // The contract's cleanupPolicy field carries a single bounded token, so a multi-policy topic
        // reports the disqualifying policy and keeps the declared list in the diagnostic.
        string reportedCleanupPolicy = ToReportedCleanupPolicy(
            configEvidence.CleanupPolicy,
            CompactCleanupPolicy
        );

        if (!string.Equals(reportedCleanupPolicy, CompactCleanupPolicy, StringComparison.Ordinal))
        {
            invalid = true;
            diagnostics.Add(
                OffsetStoreInvalid(
                    "$.cleanupPolicy",
                    observedAt,
                    "CDC Connect offset-store must be compacted.",
                    CompactCleanupPolicy,
                    configEvidence.CleanupPolicy
                )
            );
        }

        if (replicationFactor < durability.ReplicationFactor)
        {
            invalid = true;
            diagnostics.Add(
                OffsetStoreInvalid(
                    "$.replicationFactor",
                    observedAt,
                    "CDC Connect offset-store replica count is below the active durability profile.",
                    durability.ReplicationFactor.ToString(CultureInfo.InvariantCulture),
                    replicationFactor.Value.ToString(CultureInfo.InvariantCulture)
                )
            );
        }

        if (!configEvidence.MinInSyncReplicasIsTopicLevel)
        {
            invalid = true;
            diagnostics.Add(
                OffsetStoreInvalid(
                    "$.minInSyncReplicas",
                    observedAt,
                    "CDC Connect offset-store requires an explicit topic-level minimum in-sync replica override.",
                    MinInSyncReplicasConfigName,
                    topicName
                )
            );
        }
        else if (configEvidence.MinInSyncReplicas < durability.MinInSyncReplicas)
        {
            invalid = true;
            diagnostics.Add(
                OffsetStoreInvalid(
                    "$.minInSyncReplicas",
                    observedAt,
                    "CDC Connect offset-store minimum in-sync replicas is below the active durability profile.",
                    durability.MinInSyncReplicas.ToString(CultureInfo.InvariantCulture),
                    configEvidence.MinInSyncReplicas.Value.ToString(CultureInfo.InvariantCulture)
                )
            );
        }

        return Build(
            context,
            controlOptions,
            invalid ? CdcConnectOffsetStorePolicyState.Invalid : CdcConnectOffsetStorePolicyState.Satisfied,
            reportedCleanupPolicy,
            replicationFactor,
            configEvidence.MinInSyncReplicas,
            aclState,
            observedAt,
            diagnostics
        );
    }

    private CdcConnectOffsetStorePolicyObservation Unresolved(
        CdcObservationContext context,
        CdcControlOptions controlOptions,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    ) =>
        Build(
            context,
            controlOptions,
            CdcConnectOffsetStorePolicyState.Unknown,
            null,
            null,
            null,
            CdcConnectOffsetStoreItemState.Unknown,
            observedAt,
            diagnostics
        );

    /// <summary>
    /// Builds the observation and runs it through its own validator. An observation that cannot pass
    /// validation is degraded to unresolved evidence rather than returned as a policy verdict.
    /// </summary>
    private CdcConnectOffsetStorePolicyObservation Build(
        CdcObservationContext context,
        CdcControlOptions controlOptions,
        CdcConnectOffsetStorePolicyState policyState,
        string? cleanupPolicy,
        int? replicationFactor,
        int? minInSyncReplicas,
        CdcConnectOffsetStoreItemState aclState,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        CdcConnectOffsetStorePolicyObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            context.PhysicalSourceFingerprint,
            controlOptions.ConnectWorkerKey,
            controlOptions.ConnectOffsetStorageTopic,
            policyState,
            cleanupPolicy,
            replicationFactor,
            minInSyncReplicas,
            aclState,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics)
        );

        CdcContractValidationResult validation = CdcConnectOffsetStorePolicyObservationValidator.Validate(
            observation,
            new(context.OperationId, context.TargetIdentity, context.PhysicalSourceFingerprint, observedAt)
        );

        if (validation.Succeeded)
        {
            return observation;
        }

        return observation with
        {
            PolicyState = CdcConnectOffsetStorePolicyState.Unknown,
            CleanupPolicy = null,
            ReplicationFactor = null,
            MinInSyncReplicas = null,
            AclState = CdcConnectOffsetStoreItemState.Unknown,
            Diagnostics = CdcDiagnostic.NormalizeDiagnostics([.. diagnostics, .. validation.Diagnostics]),
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Reduces a declared cleanup-policy list to the single token the observation contract carries: the
    /// first policy other than the expected one, or the expected policy when nothing else is declared.
    /// A topic declaring more than its expected policy therefore always reads as nonconforming.
    /// </summary>
    private static string ToReportedCleanupPolicy(string cleanupPolicy, string expectedCleanupPolicy)
    {
        string[] policies = cleanupPolicy.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        return Array.Find(
                policies,
                policy => !string.Equals(policy, expectedCleanupPolicy, StringComparison.Ordinal)
            ) ?? expectedCleanupPolicy;
    }

    private static CdcDiagnostic OffsetStoreInvalid(
        string path,
        DateTimeOffset observedAt,
        string message,
        string? expected = null,
        string? observed = null
    ) =>
        Diagnostic(
            "connectOffsetStore",
            CdcDiagnosticCategory.ConnectOffsetStoreInvalid,
            CdcDiagnosticComponent.ConnectOffsetStore,
            CdcDiagnosticSeverity.Error,
            false,
            path,
            observedAt,
            message,
            null,
            null,
            expected,
            observed
        );

    private static CdcDiagnostic OffsetStoreUnavailable(
        string path,
        DateTimeOffset observedAt,
        string message,
        string? expected = null,
        string? observed = null
    ) =>
        Diagnostic(
            "connectOffsetStore",
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticComponent.ConnectOffsetStore,
            CdcDiagnosticSeverity.Warning,
            true,
            path,
            observedAt,
            message,
            null,
            null,
            expected,
            observed
        );

    private static CdcDiagnostic TopicInvalid(
        CdcKafkaTopicSpec spec,
        string path,
        DateTimeOffset observedAt,
        string message,
        string? expected = null,
        string? observed = null
    ) =>
        Diagnostic(
            "kafkaTopic",
            CdcDiagnosticCategory.KafkaPolicyInvalid,
            CdcDiagnosticComponent.KafkaPolicy,
            CdcDiagnosticSeverity.Error,
            false,
            path,
            observedAt,
            message,
            spec.ArtifactKind,
            spec.TopicName,
            expected,
            observed
        );

    private static CdcDiagnostic TopicUnavailable(
        CdcKafkaTopicSpec spec,
        string path,
        DateTimeOffset observedAt,
        string message,
        string? observed = null
    ) =>
        Diagnostic(
            "kafkaTopic",
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticComponent.KafkaPolicy,
            CdcDiagnosticSeverity.Warning,
            true,
            path,
            observedAt,
            message,
            spec.ArtifactKind,
            spec.TopicName,
            null,
            observed
        );

    private static CdcDiagnostic AclInvalid(
        string artifactKind,
        string? artifactName,
        string path,
        DateTimeOffset observedAt,
        string message,
        string? expected,
        string? observed
    ) =>
        Diagnostic(
            "kafkaAcl",
            CdcDiagnosticCategory.KafkaPolicyInvalid,
            CdcDiagnosticComponent.KafkaPolicy,
            CdcDiagnosticSeverity.Error,
            false,
            path,
            observedAt,
            message,
            artifactKind,
            artifactName,
            expected,
            observed
        );

    private static CdcDiagnostic AclUnavailable(
        string artifactKind,
        string? artifactName,
        string path,
        DateTimeOffset observedAt,
        string message,
        string? observed
    ) =>
        Diagnostic(
            "kafkaAcl",
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticComponent.KafkaPolicy,
            CdcDiagnosticSeverity.Warning,
            true,
            path,
            observedAt,
            message,
            artifactKind,
            artifactName,
            null,
            observed
        );

    /// <summary>
    /// A schema-history state that would end continuity. The enablement phase decides whether the
    /// classifier latches it, so the diagnostic reports the observed state and never the verdict.
    /// </summary>
    private static CdcDiagnostic SchemaHistoryLost(
        string topicName,
        DateTimeOffset observedAt,
        string message,
        string observed
    ) =>
        Diagnostic(
            "sqlServerSchemaHistory",
            CdcDiagnosticCategory.SourceHistoryLost,
            CdcDiagnosticComponent.SourceHistory,
            CdcDiagnosticSeverity.Error,
            false,
            "$.sqlServerSchemaHistory.state",
            observedAt,
            message,
            "schemaHistoryTopic",
            topicName,
            "a schema history the connector can replay",
            observed
        );

    private static CdcDiagnostic SchemaHistoryUnavailable(
        string topicName,
        DateTimeOffset observedAt,
        string message,
        string observed
    ) =>
        Diagnostic(
            "sqlServerSchemaHistory",
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticComponent.SourceHistory,
            CdcDiagnosticSeverity.Warning,
            true,
            "$.sqlServerSchemaHistory.state",
            observedAt,
            message,
            "schemaHistoryTopic",
            topicName,
            null,
            observed
        );

    private static CdcDiagnostic RecordSizeInvalid(
        DateTimeOffset observedAt,
        string message,
        string? expected,
        string? observed
    ) =>
        Diagnostic(
            "kafkaRecordSize",
            CdcDiagnosticCategory.KafkaPolicyInvalid,
            CdcDiagnosticComponent.KafkaPolicy,
            CdcDiagnosticSeverity.Error,
            false,
            "$.recordSizePolicy",
            observedAt,
            message,
            "recordSizePolicy",
            null,
            expected,
            observed
        );

    private static CdcDiagnostic RecordSizeUnavailable(
        DateTimeOffset observedAt,
        string message,
        string? expected,
        string? observed
    ) =>
        Diagnostic(
            "kafkaRecordSize",
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticComponent.KafkaPolicy,
            CdcDiagnosticSeverity.Warning,
            true,
            "$.recordSizePolicy",
            observedAt,
            message,
            "recordSizePolicy",
            null,
            expected,
            observed
        );

    private static CdcDiagnostic Diagnostic(
        string codePrefix,
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        CdcDiagnosticSeverity severity,
        bool retryable,
        string path,
        DateTimeOffset observedAt,
        string message,
        string? artifactKind,
        string? artifactName,
        string? expected,
        string? observed
    ) =>
        new CdcDiagnostic(
            $"{codePrefix}{category}",
            category,
            severity,
            component,
            observedAt,
            message,
            retryable,
            artifactKind: artifactKind,
            artifactName: artifactName,
            expected: expected,
            observed: observed
        ).WithPath(path);

    /// <summary>
    /// One required Allow grant: a principal, the host the grant admits it from, and the single
    /// operation it is granted.
    /// </summary>
    /// <remarks>
    /// The host is part of the grant's identity rather than a detail of how it is created. Kafka
    /// authorizes on the triple, so a grant that names the right principal and operation from one host
    /// does not admit the connector or consumer running anywhere else. Comparing without it would let a
    /// host-restricted grant satisfy a requirement this control plane only ever creates as
    /// <see cref="AnyHost" />, and the missing wildcard grant would never be created because the
    /// requirement already looked met.
    /// </remarks>
    private readonly record struct CdcKafkaAclGrant(string Principal, string Host, AclOperation Operation);

    private enum CdcKafkaConfigComparison
    {
        Exactly,
        AtLeast,
    }

    /// <summary>One governed topic-level configuration override and the comparison it must satisfy.</summary>
    private sealed record CdcKafkaTopicConfigRule(
        string Name,
        long Value,
        CdcKafkaConfigComparison Comparison
    );

    /// <summary>
    /// The governed shape of one binding topic: its derived name, its fixed partition count and cleanup
    /// policy, and the explicit topic-level overrides it is created with and validated against.
    /// </summary>
    private sealed record CdcKafkaTopicSpec(
        string TopicName,
        string ArtifactKind,
        int PartitionCount,
        string CleanupPolicy,
        IReadOnlyList<CdcKafkaTopicConfigRule> ConfigRules
    );

    /// <summary>
    /// Topic configuration evidence. A null value means the fact could not be resolved;
    /// <see cref="MinInSyncReplicasIsTopicLevel"/> separates an explicit topic-level override from a
    /// value inherited from a broker default, which the offset store must never rely on.
    /// </summary>
    private sealed record CdcOffsetStoreConfigEvidence(
        string? CleanupPolicy,
        int? MinInSyncReplicas,
        bool MinInSyncReplicasIsTopicLevel
    )
    {
        public static CdcOffsetStoreConfigEvidence Unresolved { get; } = new(null, null, false);
    }
}

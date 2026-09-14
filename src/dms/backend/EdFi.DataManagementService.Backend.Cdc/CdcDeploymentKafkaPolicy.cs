// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.ObjectModel;
using System.Globalization;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using ItemState = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcKafkaPolicyItemState;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Pure policy shared by setup and observation. No creation, repair, readiness caching or cleanup.
/// Transport adapters supply authoritative evidence; controllers decide whether mutations are eligible.
/// </summary>
public static class CdcDeploymentKafkaPolicy
{
    public const long MinimumPublicDeleteRetentionMilliseconds = 604_800_000;

    public static CdcDeploymentKafkaPolicyPlan Build(CdcDeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CdcConnectorTemplateBindingArtifacts artifacts = CdcConnectorTemplateBindingArtifacts.From(
            request.Binding,
            nameof(request)
        );
        bool local = request.WorkerPolicy.DurabilityProfile == CdcKafkaDurabilityProfile.LocalSingleBroker;
        int replicas = local ? 1 : 3;
        int isr = local ? 1 : 2;
        List<CdcKafkaTopicIntent> topics =
        [
            Topic(
                CdcKafkaTopicRole.Public,
                artifacts.ArtifactInventory.TopicName,
                request.Binding.PartitionCount
            ),
            Topic(CdcKafkaTopicRole.Progress, artifacts.ArtifactInventory.ProgressTopicName, 1),
        ];
        if (artifacts.ArtifactInventory.SchemaHistoryTopicName is { } history)
        {
            topics.Add(Topic(CdcKafkaTopicRole.SchemaHistory, history, 1));
        }
        // One partition is a creation default only; shared existing offset-store partitions are not
        // binding identity and may have any positive count.
        CdcKafkaTopicIntent offsets = Topic(
            CdcKafkaTopicRole.SharedOffsets,
            request.WorkerPolicy.OffsetStorageTopic.Value,
            1
        );
        List<CdcKafkaAclGrant> bindingGrants = [];
        List<CdcKafkaAclGrant> offsetGrants = [];
        if (request.WorkerPolicy.AuthorizationProfile == CdcKafkaAuthorizationProfile.AuthorizationEnabled)
        {
            Add(
                offsetGrants,
                request.WorkerPolicy.WorkerPrincipal.Value,
                CdcKafkaAclResourceType.Topic,
                offsets.Name,
                CdcKafkaAclOperation.Read,
                CdcKafkaAclOperation.Write,
                CdcKafkaAclOperation.Describe
            );
            foreach (CdcKafkaTopicIntent topic in topics)
            {
                Add(
                    bindingGrants,
                    request.WorkerPolicy.ConnectorPrincipal.Value,
                    CdcKafkaAclResourceType.Topic,
                    topic.Name,
                    CdcKafkaAclOperation.Write,
                    CdcKafkaAclOperation.Describe
                );
                if (topic.Role == CdcKafkaTopicRole.SchemaHistory)
                {
                    Add(
                        bindingGrants,
                        request.WorkerPolicy.ConnectorPrincipal.Value,
                        CdcKafkaAclResourceType.Topic,
                        topic.Name,
                        CdcKafkaAclOperation.Read,
                        CdcKafkaAclOperation.DescribeConfigs
                    );
                }
            }
            foreach (CdcConsumerAccess consumer in request.WorkerPolicy.Consumers)
            {
                Add(
                    bindingGrants,
                    consumer.Principal.Value,
                    CdcKafkaAclResourceType.Topic,
                    topics[0].Name,
                    CdcKafkaAclOperation.Read,
                    CdcKafkaAclOperation.Describe
                );
                Add(
                    bindingGrants,
                    consumer.Principal.Value,
                    CdcKafkaAclResourceType.Group,
                    consumer.Group.Value,
                    CdcKafkaAclOperation.Read
                );
            }
        }
        return new(
            offsets,
            topics,
            offsetGrants,
            bindingGrants.Distinct().ToArray(),
            request.ConnectorPolicy.MaxRecordBytes,
            request.ConnectorPolicy.EffectiveProducerBufferBytes,
            request.WorkerPolicy.AuthorizationProfile
        );

        CdcKafkaTopicIntent Topic(CdcKafkaTopicRole role, string name, int partitions)
        {
            Dictionary<string, string> config = new(StringComparer.Ordinal)
            {
                ["cleanup.policy"] = role == CdcKafkaTopicRole.SchemaHistory ? "delete" : "compact",
                ["min.insync.replicas"] = isr.ToString(CultureInfo.InvariantCulture),
            };
            if (role == CdcKafkaTopicRole.Public)
            {
                config["delete.retention.ms"] = MinimumPublicDeleteRetentionMilliseconds.ToString(
                    CultureInfo.InvariantCulture
                );
                config["max.message.bytes"] = request.ConnectorPolicy.MaxRecordBytes.ToString(
                    CultureInfo.InvariantCulture
                );
            }
            if (role == CdcKafkaTopicRole.SchemaHistory)
            {
                config["retention.ms"] = "-1";
                config["retention.bytes"] = "-1";
            }
            return new(role, name, partitions, replicas, new ReadOnlyDictionary<string, string>(config));
        }
    }

    public static CoreCdc.CdcKafkaPolicyObservation ObserveBinding(
        CdcDeploymentRequest request,
        string operationId,
        DateTimeOffset observedAt,
        CdcKafkaDeploymentEvidence evidence
    ) =>
        ObserveBindingForIncrease(
            request,
            operationId,
            observedAt,
            evidence,
            request.ConnectorPolicy.MaxRecordBytes
        );

    internal static CoreCdc.CdcKafkaPolicyObservation ObserveBindingForIncrease(
        CdcDeploymentRequest request,
        string operationId,
        DateTimeOffset observedAt,
        CdcKafkaDeploymentEvidence evidence,
        int publicMaxRecordBytes
    )
    {
        CdcDeploymentKafkaPolicyPlan plan = Build(request);
        CoreCdc.CdcKafkaTopicPolicy[] topics = plan
            .BindingTopics.Select(topic =>
                ObserveTopic(request, topic, Lookup(evidence, topic.Name), publicMaxRecordBytes)
            )
            .ToArray();
        CdcKafkaAclValidation acl = ValidateAcls(request, evidence.Acls, sharedOffsets: false);
        CoreCdc.CdcKafkaRecordSizePolicy size = ObserveSize(request, plan, evidence, publicMaxRecordBytes);
        ItemState aggregate = Combine([.. topics.Select(topic => topic.State), acl.State, size.State]);
        return new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            operationId,
            observedAt,
            request.TargetIdentity,
            request.Binding.Provider,
            request.Binding.PhysicalSourceFingerprint,
            ToPolicyState(aggregate),
            request.WorkerPolicy.DurabilityProfile == CdcKafkaDurabilityProfile.LocalSingleBroker
                ? "local-single-broker"
                : "production",
            topics[0],
            topics[1],
            topics.Length == 3 ? topics[2] : null,
            new(topics[0].TopicName, acl.State),
            new(topics[1].TopicName, acl.State),
            topics.Length == 3 ? new(topics[2].TopicName, acl.State) : null,
            size,
            Diagnostics(aggregate, request, observedAt, sharedOffsets: false)
        );
    }

    public static CoreCdc.CdcConnectOffsetStorePolicyObservation ObserveOffsetStore(
        CdcDeploymentRequest request,
        string operationId,
        DateTimeOffset observedAt,
        CdcKafkaDeploymentEvidence evidence
    )
    {
        CdcDeploymentKafkaPolicyPlan plan = Build(request);
        CoreCdc.CdcKafkaTopicPolicy topic = ObserveTopic(
            request,
            plan.OffsetStore,
            Lookup(evidence, plan.OffsetStore.Name)
        );
        CdcKafkaAclValidation acl = ValidateAcls(request, evidence.Acls, sharedOffsets: true);
        ItemState aggregate = Combine([topic.State, acl.State]);
        return new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            operationId,
            observedAt,
            request.TargetIdentity,
            request.Binding.Provider,
            request.Binding.PhysicalSourceFingerprint,
            request.WorkerPolicy.WorkerKey.Value,
            plan.OffsetStore.Name,
            ToOffsetPolicyState(aggregate),
            topic.CleanupPolicy,
            topic.ReplicationFactor,
            topic.MinInSyncReplicas,
            ToOffsetItemState(acl.State),
            Diagnostics(aggregate, request, observedAt, sharedOffsets: true)
        )
        {
            TopicState = ToOffsetItemState(topic.State),
        };
    }

    internal static CoreCdc.CdcKafkaTopicPolicy ObserveTopic(
        CdcDeploymentRequest request,
        CdcKafkaTopicIntent intent,
        CdcTransportResult<CdcKafkaTopicEvidence> result,
        int? publicMaxRecordBytes = null
    )
    {
        if (result is not CdcTransportResult<CdcKafkaTopicEvidence>.Observed observed)
        {
            return new(intent.Name, EvidenceState(result), null, null, null, null);
        }
        CdcKafkaTopicEvidence evidence = observed.Value;
        List<ItemState> states = [evidence.Name == intent.Name ? ItemState.Satisfied : ItemState.Invalid];
        int partitions = evidence.PartitionReplicas.Count;
        bool completePartitions =
            partitions > 0 && Enumerable.Range(0, partitions).All(evidence.PartitionReplicas.ContainsKey);
        states.Add(completePartitions ? ItemState.Satisfied : ItemState.Invalid);
        if (intent.Role != CdcKafkaTopicRole.SharedOffsets)
        {
            states.Add(partitions == intent.PartitionCount ? ItemState.Satisfied : ItemState.Invalid);
        }
        bool local = request.WorkerPolicy.DurabilityProfile == CdcKafkaDurabilityProfile.LocalSingleBroker;
        int replicas = partitions == 0 ? 0 : evidence.PartitionReplicas.Values.Min(ids => ids.Count);
        bool validAssignments = evidence.PartitionReplicas.Values.All(ids =>
            ids.Count > 0
            && ids.All(id => id >= 0)
            && ids.Distinct().Count() == ids.Count
            && (local ? ids.Count == 1 : ids.Count >= 3)
        );
        states.Add(validAssignments && partitions > 0 ? ItemState.Satisfied : ItemState.Invalid);
        states.Add(
            Number(
                evidence,
                "min.insync.replicas",
                true,
                value => (local ? value == 1 : value >= 2) && value <= replicas,
                out long isr
            )
        );
        states.Add(
            Text(
                evidence,
                "cleanup.policy",
                intent.Role == CdcKafkaTopicRole.SchemaHistory ? "delete" : "compact"
            )
        );
        if (intent.Role == CdcKafkaTopicRole.Public)
        {
            states.Add(
                Number(
                    evidence,
                    "delete.retention.ms",
                    true,
                    value => value >= MinimumPublicDeleteRetentionMilliseconds,
                    out _
                )
            );
            states.Add(
                Number(
                    evidence,
                    "max.message.bytes",
                    true,
                    value => value == (publicMaxRecordBytes ?? request.ConnectorPolicy.MaxRecordBytes),
                    out _
                )
            );
        }
        if (intent.Role == CdcKafkaTopicRole.SchemaHistory)
        {
            states.Add(Number(evidence, "retention.ms", false, value => value == -1, out _));
            states.Add(Number(evidence, "retention.bytes", false, value => value == -1, out _));
        }
        return new(
            intent.Name,
            Combine(states),
            partitions > 0 ? partitions : null,
            Cleanup(evidence),
            replicas > 0 ? replicas : null,
            isr is > 0 and <= int.MaxValue ? (int)isr : null
        );
    }

    private static CoreCdc.CdcKafkaRecordSizePolicy ObserveSize(
        CdcDeploymentRequest request,
        CdcDeploymentKafkaPolicyPlan plan,
        CdcKafkaDeploymentEvidence evidence,
        int publicMaxRecordBytes
    )
    {
        List<ItemState> states = [];
        long messageBytes = 0;
        CdcTransportResult<CdcKafkaTopicEvidence> publicTopic = Lookup(evidence, plan.BindingTopics[0].Name);
        if (publicTopic is CdcTransportResult<CdcKafkaTopicEvidence>.Observed topic)
        {
            states.Add(
                Number(
                    topic.Value,
                    "max.message.bytes",
                    true,
                    value => value == publicMaxRecordBytes,
                    out messageBytes
                )
            );
        }
        else
        {
            states.Add(EvidenceState(publicTopic));
        }
        states.Add(ObserveBrokerCapacity(request, evidence));
        if (evidence.Producer is CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Observed producer)
        {
            states.Add(
                producer.Value.MaxRequestBytes == plan.MaxRecordBytes
                && producer.Value.BufferBytes == plan.ProducerBufferBytes
                && producer.Value.WorkerHeapBytes >= request.WorkerPolicy.HeapBytes
                && producer.Value.WorkerHeapBytes > producer.Value.BufferBytes
                    ? ItemState.Satisfied
                    : ItemState.Invalid
            );
        }
        else
        {
            states.Add(EvidenceState(evidence.Producer));
        }
        return new(
            Combine(states),
            plan.MaxRecordBytes,
            messageBytes is > 0 and <= int.MaxValue ? (int)messageBytes : null
        );
    }

    internal static ItemState ObserveBrokerCapacity(
        CdcDeploymentRequest request,
        CdcKafkaDeploymentEvidence evidence
    )
    {
        var plan = Build(request);
        if (evidence.Brokers is CdcTransportResult<CdcKafkaBrokerEvidence>.Observed brokers)
        {
            if (!brokers.Value.InventoryComplete || brokers.Value.Brokers.Count == 0)
            {
                return ItemState.Unknown;
            }
            else
            {
                int[] ids = brokers.Value.Brokers.Select(broker => broker.BrokerId).ToArray();
                bool valid =
                    Array.TrueForAll(ids, id => id >= 0)
                    && ids.Distinct().Count() == ids.Length
                    && brokers.Value.Brokers.All(broker =>
                        broker.SocketRequestMaxBytes >= plan.MaxRecordBytes
                        && broker.ReplicaFetchMaxBytes >= plan.MaxRecordBytes
                        && broker.ReplicaFetchResponseMaxBytes >= plan.MaxRecordBytes
                    );
                // A claimed complete inventory that omits an assigned broker is contradictory.
                valid &= plan
                    .BindingTopics.Append(plan.OffsetStore)
                    .Select(intent => Lookup(evidence, intent.Name))
                    .OfType<CdcTransportResult<CdcKafkaTopicEvidence>.Observed>()
                    .SelectMany(result => result.Value.PartitionReplicas.Values)
                    .SelectMany(replicas => replicas)
                    .All(ids.Contains);
                return valid ? ItemState.Satisfied : ItemState.Invalid;
            }
        }
        else
        {
            return EvidenceState(evidence.Brokers);
        }
    }

    private static ItemState Text(CdcKafkaTopicEvidence topic, string key, string expected) =>
        topic.Configuration.TryGetValue(key, out var value)
            ? Matches(value.Value == expected)
            : ItemState.Unknown;

    private static ItemState Number(
        CdcKafkaTopicEvidence topic,
        string key,
        bool requireOverride,
        Func<long, bool> predicate,
        out long number
    )
    {
        number = 0;
        if (!topic.Configuration.TryGetValue(key, out var value))
        {
            return ItemState.Unknown;
        }
        return
            (!requireOverride || value.IsTopicOverride)
            && long.TryParse(
                value.Value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out number
            )
            && predicate(number)
            ? ItemState.Satisfied
            : ItemState.Invalid;
    }

    private static string? Cleanup(CdcKafkaTopicEvidence topic) =>
        topic.Configuration.TryGetValue("cleanup.policy", out var value)
            ? SanitizedCleanup(value.Value)
            : null;

    private static string SanitizedCleanup(string value) =>
        value is "compact" or "delete" ? value : "invalid";

    private static ItemState Matches(bool matches) => matches ? ItemState.Satisfied : ItemState.Invalid;

    private static CdcTransportResult<CdcKafkaTopicEvidence> Lookup(
        CdcKafkaDeploymentEvidence evidence,
        string name
    ) =>
        evidence.Topics.TryGetValue(name, out var result)
            ? result
            : new CdcTransportResult<CdcKafkaTopicEvidence>.Unavailable(
                new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
            );

    private static ItemState EvidenceState<T>(CdcTransportResult<T> result)
        where T : notnull =>
        result.State == CdcTransportEvidenceState.Absent ? ItemState.Invalid : ItemState.Unknown;

    private static ItemState Combine(IEnumerable<ItemState> states)
    {
        if (states.Contains(ItemState.Invalid))
        {
            return ItemState.Invalid;
        }
        return states.Contains(ItemState.Unknown) ? ItemState.Unknown : ItemState.Satisfied;
    }

    private static CoreCdc.CdcKafkaPolicyState ToPolicyState(ItemState state) =>
        state switch
        {
            ItemState.Satisfied => CoreCdc.CdcKafkaPolicyState.Satisfied,
            ItemState.Invalid => CoreCdc.CdcKafkaPolicyState.Invalid,
            _ => CoreCdc.CdcKafkaPolicyState.Unknown,
        };

    private static CoreCdc.CdcConnectOffsetStorePolicyState ToOffsetPolicyState(ItemState state) =>
        state switch
        {
            ItemState.Satisfied => CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied,
            ItemState.Invalid => CoreCdc.CdcConnectOffsetStorePolicyState.Invalid,
            _ => CoreCdc.CdcConnectOffsetStorePolicyState.Unknown,
        };

    private static CoreCdc.CdcConnectOffsetStoreItemState ToOffsetItemState(ItemState state) =>
        state switch
        {
            ItemState.Satisfied => CoreCdc.CdcConnectOffsetStoreItemState.Satisfied,
            ItemState.Invalid => CoreCdc.CdcConnectOffsetStoreItemState.Invalid,
            _ => CoreCdc.CdcConnectOffsetStoreItemState.Unknown,
        };

    private static IReadOnlyList<CoreCdc.CdcDiagnostic> Diagnostics(
        ItemState state,
        CdcDeploymentRequest request,
        DateTimeOffset observedAt,
        bool sharedOffsets
    )
    {
        List<CoreCdc.CdcDiagnostic> diagnostics = [];
        if (
            request.WorkerPolicy.AuthorizationProfile
            == CdcKafkaAuthorizationProfile.AuthorizationDisabledLocal
        )
        {
            diagnostics.Add(
                new(
                    "authorizationDisabledLocal",
                    CoreCdc.CdcDiagnosticCategory.None,
                    CoreCdc.CdcDiagnosticSeverity.Info,
                    sharedOffsets
                        ? CoreCdc.CdcDiagnosticComponent.ConnectOffsetStore
                        : CoreCdc.CdcDiagnosticComponent.KafkaPolicy,
                    observedAt,
                    "Authorization-disabled local profile; this observation supplies no ACL isolation proof.",
                    false
                )
            );
        }
        if (state != ItemState.Satisfied)
        {
            CoreCdc.CdcDiagnosticCategory invalidCategory = sharedOffsets
                ? CoreCdc.CdcDiagnosticCategory.ConnectOffsetStoreInvalid
                : CoreCdc.CdcDiagnosticCategory.KafkaPolicyInvalid;
            diagnostics.Add(
                new(
                    state == ItemState.Invalid ? "kafkaPolicyInvalid" : "kafkaPolicyUnknown",
                    state == ItemState.Invalid
                        ? invalidCategory
                        : CoreCdc.CdcDiagnosticCategory.StatusObservationUnavailable,
                    CoreCdc.CdcDiagnosticSeverity.Error,
                    sharedOffsets
                        ? CoreCdc.CdcDiagnosticComponent.ConnectOffsetStore
                        : CoreCdc.CdcDiagnosticComponent.KafkaPolicy,
                    observedAt,
                    state == ItemState.Invalid
                        ? "Live Kafka evidence violates the deployment policy."
                        : "Authoritative Kafka policy evidence is unavailable.",
                    true
                )
            );
        }
        return diagnostics;
    }

    private static void Add(
        List<CdcKafkaAclGrant> grants,
        string principal,
        CdcKafkaAclResourceType resourceType,
        string resource,
        params CdcKafkaAclOperation[] operations
    )
    {
        grants.AddRange(
            operations.Select(operation => new CdcKafkaAclGrant(principal, resourceType, resource, operation))
        );
    }

    public static CdcKafkaAclValidation ValidateAcls(
        CdcDeploymentRequest request,
        CdcTransportResult<CdcKafkaAclEvidence> result,
        bool sharedOffsets
    )
    {
        CdcDeploymentKafkaPolicyPlan plan = Build(request);
        CdcKafkaAuthorizationProfile profile = request.WorkerPolicy.AuthorizationProfile;
        if (result is not CdcTransportResult<CdcKafkaAclEvidence>.Observed observed)
        {
            // Absence of an authorizer/inventory is not evidence of disabled authorization.
            return new(ItemState.Unknown, profile, false, []);
        }
        CdcKafkaAclEvidence evidence = observed.Value;
        if (evidence.AuthorizationEnabled != (profile == CdcKafkaAuthorizationProfile.AuthorizationEnabled))
        {
            return new(ItemState.Invalid, profile, true, []);
        }
        if (!evidence.AuthorizationEnabled)
        {
            return new(ItemState.Satisfied, profile, false, []);
        }
        if (!evidence.InventoryComplete || evidence.Grants.Any(grant => !ValidGrant(grant)))
        {
            return new(ItemState.Unknown, profile, false, []);
        }
        CdcWorkerDeploymentPolicy worker = request.WorkerPolicy;
        string[] restrictedPrincipals =
        [
            worker.WorkerPrincipal.Value,
            worker.ConnectorPrincipal.Value,
            .. worker.Consumers.Select(consumer => consumer.Principal.Value),
        ];
        if (
            evidence.AllowEveryoneIfNoAclFound
            || evidence.SuperuserPrincipals.Any(principal =>
                restrictedPrincipals.Contains(principal, StringComparer.Ordinal)
                || principal is "*" or "User:*"
            )
        )
        {
            return new(ItemState.Invalid, profile, true, []);
        }
        CdcKafkaAclGrant[] allRequired = [.. plan.OffsetStoreGrants, .. plan.BindingGrants];
        IReadOnlyList<CdcKafkaAclGrant> required = sharedOffsets
            ? plan.OffsetStoreGrants
            : plan.BindingGrants;
        string[] protectedTopics = sharedOffsets
            ? [plan.OffsetStore.Name]
            : plan.BindingTopics.Select(topic => topic.Name).ToArray();
        foreach (CdcKafkaAclGrant grant in evidence.Grants)
        {
            if (grant.Principal == worker.DeploymentAdministratorPrincipal.Value)
            {
                // Deployment administration is intentionally outside the consumer/connector grant set.
                continue;
            }
            if (grant.Permission == CdcKafkaAclPermission.Deny)
            {
                if (
                    required.Any(expected =>
                        PrincipalMatches(grant, expected.Principal)
                        && ResourceMatches(grant, expected.ResourceType, expected.ResourceName)
                        && (
                            grant.Operation == CdcKafkaAclOperation.All
                            || grant.Operation == expected.Operation
                        )
                    )
                )
                {
                    return new(ItemState.Invalid, profile, true, []);
                }
                continue;
            }
            bool touchesProtectedTopic =
                grant.ResourceType == CdcKafkaAclResourceType.Topic
                && Array.Exists(
                    protectedTopics,
                    topic => ResourceMatches(grant, CdcKafkaAclResourceType.Topic, topic)
                );
            bool touchesProtectedGroup =
                !sharedOffsets
                && worker.Consumers.Any(consumer =>
                    ResourceMatches(grant, CdcKafkaAclResourceType.Group, consumer.Group.Value)
                );
            // Cluster administration can change authorization itself; it is not a service or consumer role.
            if (
                grant.ResourceType == CdcKafkaAclResourceType.Cluster
                && grant.Operation
                    is CdcKafkaAclOperation.All
                        or CdcKafkaAclOperation.Alter
                        or CdcKafkaAclOperation.Create
                        or CdcKafkaAclOperation.ClusterAction
            )
            {
                return new(ItemState.Invalid, profile, true, []);
            }
            bool touchesConsumer = worker.Consumers.Any(consumer =>
                PrincipalMatches(grant, consumer.Principal.Value)
            );
            // Consumers must have only the exact public topic and configured group grants across
            // the complete inventory, including peer topics and other worker internal resources.
            if (touchesProtectedTopic || touchesProtectedGroup || touchesConsumer)
            {
                bool literalAllowed =
                    grant.Pattern == CdcKafkaAclPattern.Literal
                    && grant.ResourceName != "*"
                    && grant.Principal is not ("*" or "User:*")
                    && Array.Exists(
                        allRequired,
                        expected =>
                            expected.Principal == grant.Principal
                            && expected.ResourceType == grant.ResourceType
                            && expected.ResourceName == grant.ResourceName
                            && expected.Operation == grant.Operation
                    );
                if (!literalAllowed)
                {
                    return new(ItemState.Invalid, profile, true, []);
                }
            }
        }
        CdcKafkaAclGrant[] missing = required
            .Where(expected => !evidence.Grants.Contains(expected))
            .ToArray();
        return new(
            missing.Length == 0 ? ItemState.Satisfied : ItemState.Invalid,
            profile,
            false,
            Array.AsReadOnly(missing)
        );
    }

    private static bool ValidGrant(CdcKafkaAclGrant grant) =>
        grant is not null
        && Enum.IsDefined(grant.ResourceType)
        && Enum.IsDefined(grant.Pattern)
        && Enum.IsDefined(grant.Operation)
        && Enum.IsDefined(grant.Permission)
        && !string.IsNullOrWhiteSpace(grant.Principal)
        && !grant.Principal.Any(char.IsControl)
        && !string.IsNullOrWhiteSpace(grant.ResourceName)
        && !grant.ResourceName.Any(char.IsControl)
        && !string.IsNullOrWhiteSpace(grant.Host)
        && !grant.Host.Any(char.IsControl);

    private static bool PrincipalMatches(CdcKafkaAclGrant grant, string principal) =>
        grant.Principal == principal || grant.Principal == "*" || grant.Principal == "User:*";

    private static bool ResourceMatches(CdcKafkaAclGrant grant, CdcKafkaAclResourceType type, string name) =>
        grant.ResourceType == type
        && (
            grant.Pattern == CdcKafkaAclPattern.Prefixed
                ? name.StartsWith(grant.ResourceName, StringComparison.Ordinal)
                : grant.ResourceName == "*" || grant.ResourceName == name
        );
}

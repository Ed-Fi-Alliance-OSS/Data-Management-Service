// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public enum CdcKafkaTopicRole
{
    SharedOffsets,
    Public,
    Progress,
    SchemaHistory,
}

/// <summary>Creation intent. Validation permits stronger production durability and public retention.</summary>
public sealed record CdcKafkaTopicIntent(
    CdcKafkaTopicRole Role,
    string Name,
    int PartitionCount,
    int ReplicationFactor,
    IReadOnlyDictionary<string, string> Configuration
);

public enum CdcKafkaAclResourceType
{
    Topic,
    Group,
    Cluster,
    TransactionalId,
}

public enum CdcKafkaAclPattern
{
    Literal,
    Prefixed,
}

public enum CdcKafkaAclOperation
{
    Read,
    Write,
    Describe,
    DescribeConfigs,
    Create,
    Delete,
    Alter,
    AlterConfigs,
    ClusterAction,
    IdempotentWrite,
    All,
}

public enum CdcKafkaAclPermission
{
    Allow,
    Deny,
}

/// <summary>
/// Exact Kafka principal text, including its type when required by the authorizer. Raw ACL input
/// must never become diagnostic text. Required grants are literal, allow, and all-host grants.
/// </summary>
public sealed record CdcKafkaAclGrant(
    [property: JsonIgnore] string Principal,
    CdcKafkaAclResourceType ResourceType,
    [property: JsonIgnore] string ResourceName,
    CdcKafkaAclOperation Operation,
    CdcKafkaAclPattern Pattern = CdcKafkaAclPattern.Literal,
    CdcKafkaAclPermission Permission = CdcKafkaAclPermission.Allow,
    [property: JsonIgnore] string Host = "*"
)
{
    public override string ToString() => nameof(CdcKafkaAclGrant);
}

/// <summary>Immutable in-memory plan. Cluster grants/offset storage are deliberately separate.</summary>
public sealed class CdcDeploymentKafkaPolicyPlan
{
    internal CdcDeploymentKafkaPolicyPlan(
        CdcKafkaTopicIntent offsetStore,
        IReadOnlyList<CdcKafkaTopicIntent> bindingTopics,
        IReadOnlyList<CdcKafkaAclGrant> offsetStoreGrants,
        IReadOnlyList<CdcKafkaAclGrant> bindingGrants,
        int maxRecordBytes,
        int producerBufferBytes,
        CdcKafkaAuthorizationProfile authorizationProfile
    )
    {
        OffsetStore = offsetStore;
        BindingTopics = Array.AsReadOnly(bindingTopics.ToArray());
        OffsetStoreGrants = Array.AsReadOnly(offsetStoreGrants.ToArray());
        BindingGrants = Array.AsReadOnly(bindingGrants.ToArray());
        MaxRecordBytes = maxRecordBytes;
        ProducerBufferBytes = producerBufferBytes;
        AuthorizationProfile = authorizationProfile;
    }

    public CdcKafkaTopicIntent OffsetStore { get; }
    public IReadOnlyList<CdcKafkaTopicIntent> BindingTopics { get; }

    [JsonIgnore]
    public IReadOnlyList<CdcKafkaAclGrant> OffsetStoreGrants { get; }

    [JsonIgnore]
    public IReadOnlyList<CdcKafkaAclGrant> BindingGrants { get; }
    public int MaxRecordBytes { get; }
    public int ProducerBufferBytes { get; }
    public CdcKafkaAuthorizationProfile AuthorizationProfile { get; }

    public override string ToString() => nameof(CdcDeploymentKafkaPolicyPlan);
}

public sealed record CdcKafkaConfigurationValue([property: JsonIgnore] string Value, bool IsTopicOverride)
{
    public override string ToString() => nameof(CdcKafkaConfigurationValue);
}

/// <summary>
/// Successful describe response including every partition and effective configuration with origin.
/// Empty/incomplete responses must not be replaced with requested settings or broker defaults.
/// </summary>
public sealed record CdcKafkaTopicEvidence(
    [property: JsonIgnore] string Name,
    [property: JsonIgnore] IReadOnlyDictionary<int, IReadOnlyList<int>> PartitionReplicas,
    [property: JsonIgnore] IReadOnlyDictionary<string, CdcKafkaConfigurationValue> Configuration
)
{
    public override string ToString() => nameof(CdcKafkaTopicEvidence);
}

public sealed record CdcKafkaBrokerCapacity(
    int BrokerId,
    long SocketRequestMaxBytes,
    long ReplicaFetchMaxBytes,
    long ReplicaFetchResponseMaxBytes
);

/// <summary>
/// Complete live broker inventory/configuration, including brokers eligible for future assignment.
/// The public topic override supplies the effective record-batch limit, superseding message.max.bytes.
/// </summary>
public sealed record CdcKafkaBrokerEvidence(
    bool InventoryComplete,
    [property: JsonIgnore] IReadOnlyList<CdcKafkaBrokerCapacity> Brokers
)
{
    public override string ToString() => nameof(CdcKafkaBrokerEvidence);
}

/// <summary>Actual producer configuration and deployment heap evidence; never copied from intent.</summary>
public sealed record CdcKafkaProducerCapacityEvidence(
    long MaxRequestBytes,
    long BufferBytes,
    long WorkerHeapBytes
);

/// <summary>
/// Full effective deployment-managed ACL inventory, including wildcard/prefixed grants, denies,
/// superusers and the broker's default-allow setting. A topic-only or allow-only query is incomplete.
/// Unavailable authorizer configuration is an Unavailable transport result, not false booleans.
/// </summary>
public sealed record CdcKafkaAclEvidence(
    bool AuthorizationEnabled,
    bool InventoryComplete,
    bool AllowEveryoneIfNoAclFound,
    [property: JsonIgnore] IReadOnlyList<string> SuperuserPrincipals,
    [property: JsonIgnore] IReadOnlyList<CdcKafkaAclGrant> Grants
)
{
    public override string ToString() => nameof(CdcKafkaAclEvidence);
}

public sealed record CdcKafkaAclValidation(
    CoreCdc.CdcKafkaPolicyItemState State,
    CdcKafkaAuthorizationProfile AuthorizationProfile,
    bool UnsafeGrants,
    [property: JsonIgnore] IReadOnlyList<CdcKafkaAclGrant> MissingGrants
)
{
    // Setup may reconcile missing grants only after all unsafe and unavailable evidence is rejected.
    public bool CanAddMissingGrants =>
        State == CoreCdc.CdcKafkaPolicyItemState.Invalid && !UnsafeGrants && MissingGrants.Count > 0;

    public override string ToString() => nameof(CdcKafkaAclValidation);
}

/// <summary>Every lookup has its own absence/unavailability outcome. Missing dictionary keys are unknown.</summary>
public sealed record CdcKafkaDeploymentEvidence(
    [property: JsonIgnore] IReadOnlyDictionary<string, CdcTransportResult<CdcKafkaTopicEvidence>> Topics,
    [property: JsonIgnore] CdcTransportResult<CdcKafkaBrokerEvidence> Brokers,
    [property: JsonIgnore] CdcTransportResult<CdcKafkaProducerCapacityEvidence> Producer,
    [property: JsonIgnore] CdcTransportResult<CdcKafkaAclEvidence> Acls
)
{
    public override string ToString() => nameof(CdcKafkaDeploymentEvidence);
}

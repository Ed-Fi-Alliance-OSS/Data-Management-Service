// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcDatabaseCreationMode>))]
public enum CdcDatabaseCreationMode
{
    CreatedForInitialCdcProvisioning,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcWriteAdmissionState>))]
public enum CdcWriteAdmissionState
{
    ClosedNeverOpened,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConsistencyScope>))]
public enum CdcConsistencyScope
{
    SingleProviderTransaction,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcCacheAheadState>))]
public enum CdcCacheAheadState
{
    Clear,
    RecoveryRequired,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProjectionCorrelationState>))]
public enum CdcProjectionCorrelationState
{
    Matched,
    TargetMismatch,
    ProviderMismatch,
    SourceMismatch,
    Unavailable,
    InvalidPayload,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderSetupMode>))]
public enum CdcProviderSetupMode
{
    InitialCreateOrExactMatch,
    ValidateOnly,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderSetupOutcome>))]
public enum CdcProviderSetupOutcome
{
    Satisfied,
    Invalid,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderSetupState>))]
public enum CdcProviderSetupState
{
    Matched,
    Mismatched,
    Missing,
    Unknown,
    NotApplicable,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderBarrierState>))]
public enum CdcProviderBarrierState
{
    Reached,
    NotReached,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderArtifactContinuityState>))]
public enum CdcProviderArtifactContinuityState
{
    ExactMatch,
    Missing,
    Recreated,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderRetainedRangeState>))]
public enum CdcProviderRetainedRangeState
{
    CoversCommittedOffset,
    Gap,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcSqlServerCdcJobState>))]
public enum CdcSqlServerCdcJobState
{
    Healthy,
    Missing,
    Stopped,
    Failed,
    Unknown,
}

public sealed record CdcSqlServerCdcJobEvidence(
    [property: JsonRequired] CdcSqlServerCdcJobState CaptureJobState,
    [property: JsonRequired] CdcSqlServerCdcJobState CleanupJobState
)
{
    public static CdcSqlServerCdcJobEvidence Healthy =>
        new(CdcSqlServerCdcJobState.Healthy, CdcSqlServerCdcJobState.Healthy);

    public static CdcSqlServerCdcJobEvidence Missing =>
        new(CdcSqlServerCdcJobState.Missing, CdcSqlServerCdcJobState.Missing);

    public static CdcSqlServerCdcJobEvidence Unknown =>
        new(CdcSqlServerCdcJobState.Unknown, CdcSqlServerCdcJobState.Unknown);

    public bool HasMissingJob =>
        CaptureJobState == CdcSqlServerCdcJobState.Missing
        || CleanupJobState == CdcSqlServerCdcJobState.Missing;

    public bool HasUnknownJob =>
        CaptureJobState == CdcSqlServerCdcJobState.Unknown
        || CleanupJobState == CdcSqlServerCdcJobState.Unknown;

    public bool HasStoppedOrFailedJob =>
        CaptureJobState is CdcSqlServerCdcJobState.Stopped or CdcSqlServerCdcJobState.Failed
        || CleanupJobState is CdcSqlServerCdcJobState.Stopped or CdcSqlServerCdcJobState.Failed;

    public bool IsHealthy =>
        CaptureJobState == CdcSqlServerCdcJobState.Healthy
        && CleanupJobState == CdcSqlServerCdcJobState.Healthy;
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcSqlServerSchemaHistoryEnablementPhase>))]
public enum CdcSqlServerSchemaHistoryEnablementPhase
{
    BeforeInitialAdmission,
    AfterInitialAdmission,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcSqlServerSchemaHistoryState>))]
public enum CdcSqlServerSchemaHistoryState
{
    Valid,
    Missing,
    EmptyWithRetainedOffset,
    RequiredRecordLost,
    Unreadable,
    Unknown,
    NotApplicable,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcKafkaPolicyState>))]
public enum CdcKafkaPolicyState
{
    Satisfied,
    Invalid,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcKafkaPolicyItemState>))]
public enum CdcKafkaPolicyItemState
{
    Satisfied,
    Invalid,
    Unknown,
    NotApplicable,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConnectOffsetStorePolicyState>))]
public enum CdcConnectOffsetStorePolicyState
{
    Satisfied,
    Invalid,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConnectOffsetStoreItemState>))]
public enum CdcConnectOffsetStoreItemState
{
    Satisfied,
    Invalid,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConnectorConfigurationState>))]
public enum CdcConnectorConfigurationState
{
    Matched,
    Invalid,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConnectorConfigurationItemState>))]
public enum CdcConnectorConfigurationItemState
{
    Matched,
    Invalid,
    Unknown,
    NotApplicable,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConnectorRuntimeState>))]
public enum CdcConnectorRuntimeState
{
    Running,
    Paused,
    Failed,
    Stopped,
    Unassigned,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConnectorSnapshotState>))]
public enum CdcConnectorSnapshotState
{
    NotStarted,
    Running,
    Completed,
    NotApplicable,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConnectorLagState>))]
public enum CdcConnectorLagState
{
    WithinThreshold,
    Exceeded,
    Unknown,
}

public interface ICdcObservationContract : ICdcJsonContract
{
    string OperationId { get; }

    DateTimeOffset ObservedAt { get; }

    CdcTargetIdentity TargetIdentity { get; }

    CdcProvider Provider { get; }

    string? PhysicalSourceFingerprint { get; }

    IReadOnlyList<CdcDiagnostic> Diagnostics { get; }
}

public sealed record CdcObservationValidationContext(
    string OperationId,
    CdcTargetIdentity TargetIdentity,
    string? PhysicalSourceFingerprint,
    DateTimeOffset NowUtc
);

public sealed record InitialCdcProvisioningProof(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string ProofId,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string SetupControllerRunId,
    [property: JsonRequired] CdcDatabaseCreationMode DatabaseCreationMode,
    [property: JsonRequired] CdcWriteAdmissionState WriteAdmissionState,
    [property: JsonRequired] DateTimeOffset IssuedAt
) : ICdcJsonContract;

public sealed record InitialCdcEligibilityObservation : ICdcObservationContract
{
    [JsonConstructor]
    public InitialCdcEligibilityObservation(
        int contractVersion,
        string operationId,
        DateTimeOffset observedAt,
        DateTimeOffset durableObservedAt,
        CdcTargetIdentity targetIdentity,
        CdcProvider provider,
        string? physicalSourceFingerprint,
        string setupControllerRunId,
        string writeAdmissionProofId,
        CdcConsistencyScope consistencyScope,
        CdcLifecycleState lifecycleState,
        CdcCacheAheadState cacheAheadState,
        bool canonicalRowsPresent,
        bool cacheRowsPresent,
        bool workRowsPresent,
        string providerConsistencyToken,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        ContractVersion = contractVersion;
        OperationId = operationId;
        ObservedAt = observedAt;
        DurableObservedAt = durableObservedAt;
        TargetIdentity = targetIdentity;
        Provider = provider;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        SetupControllerRunId = setupControllerRunId;
        WriteAdmissionProofId = writeAdmissionProofId;
        ConsistencyScope = consistencyScope;
        LifecycleState = lifecycleState;
        CacheAheadState = cacheAheadState;
        CanonicalRowsPresent = canonicalRowsPresent;
        CacheRowsPresent = cacheRowsPresent;
        WorkRowsPresent = workRowsPresent;
        ProviderConsistencyToken = providerConsistencyToken;
        Diagnostics = diagnostics;
    }

    private readonly string _providerConsistencyToken = CdcContractText.EvidenceUnavailable;

    [JsonRequired]
    public int ContractVersion { get; init; }

    [JsonRequired]
    public string OperationId { get; init; }

    [JsonRequired]
    public DateTimeOffset ObservedAt { get; init; }

    [JsonRequired]
    public DateTimeOffset DurableObservedAt { get; init; }

    [JsonRequired]
    public CdcTargetIdentity TargetIdentity { get; init; }

    [JsonRequired]
    public CdcProvider Provider { get; init; }

    [JsonRequired]
    public string? PhysicalSourceFingerprint { get; init; }

    [JsonRequired]
    public string SetupControllerRunId { get; init; }

    [JsonRequired]
    public string WriteAdmissionProofId { get; init; }

    [JsonRequired]
    public CdcConsistencyScope ConsistencyScope { get; init; }

    [JsonRequired]
    public CdcLifecycleState LifecycleState { get; init; }

    [JsonRequired]
    public CdcCacheAheadState CacheAheadState { get; init; }

    [JsonRequired]
    public bool CanonicalRowsPresent { get; init; }

    [JsonRequired]
    public bool CacheRowsPresent { get; init; }

    [JsonRequired]
    public bool WorkRowsPresent { get; init; }

    [JsonRequired]
    public string ProviderConsistencyToken
    {
        get => _providerConsistencyToken;
        init => _providerConsistencyToken = CdcContractText.SanitizeRequiredEvidence(value);
    }

    [JsonRequired]
    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; init; }
}

public sealed record CdcProjectionCorrelationObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] DateTimeOffset ProjectionObservedAt,
    [property: JsonRequired] DocumentCacheStatusTargetKey E18TargetKey,
    [property: JsonRequired] CdcProjectionCorrelationState CorrelationState,
    [property: JsonRequired] DocumentCacheOperationalHealthStatus OperationalHealthStatus,
    [property: JsonRequired] DocumentCacheStatusReason OperationalHealthReason,
    [property: JsonRequired] DocumentCacheCaughtUpStatus CaughtUpStatus,
    [property: JsonRequired] DocumentCacheStatusReason CaughtUpReason,
    [property: JsonRequired] DocumentCacheStatusQueuePresence QueuePresence,
    [property: JsonRequired]
        IReadOnlyList<DocumentCacheStatusEnqueueFailureCategory> EnqueueFailureCategories,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcProviderSetupObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] CdcProviderSetupMode SetupMode,
    [property: JsonRequired] CdcProviderSetupOutcome SetupOutcome,
    [property: JsonRequired] CdcProviderSetupState ArtifactInventoryState,
    [property: JsonRequired] CdcProviderSetupState GrantInventoryState,
    [property: JsonRequired] CdcProviderSetupState SourceInventoryState,
    [property: JsonRequired] CdcProviderSetupState HeartbeatState,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcProviderBarrierObservation : ICdcObservationContract
{
    [JsonConstructor]
    public CdcProviderBarrierObservation(
        int contractVersion,
        string operationId,
        DateTimeOffset observedAt,
        CdcTargetIdentity targetIdentity,
        CdcProvider provider,
        string? physicalSourceFingerprint,
        DateTimeOffset projectionCaughtUpObservedAt,
        DateTimeOffset barrierCapturedAt,
        DateTimeOffset connectorOffsetObservedAt,
        CdcProviderBarrierState barrierState,
        string? postgresqlBarrierLsn,
        string? sqlServerCommitLsn,
        string? sqlServerChangeLsn,
        long? sqlServerEventSerialNo,
        string? committedPosition,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        ContractVersion = contractVersion;
        OperationId = operationId;
        ObservedAt = observedAt;
        TargetIdentity = targetIdentity;
        Provider = provider;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        ProjectionCaughtUpObservedAt = projectionCaughtUpObservedAt;
        BarrierCapturedAt = barrierCapturedAt;
        ConnectorOffsetObservedAt = connectorOffsetObservedAt;
        BarrierState = barrierState;
        PostgresqlBarrierLsn = postgresqlBarrierLsn;
        SqlServerCommitLsn = sqlServerCommitLsn;
        SqlServerChangeLsn = sqlServerChangeLsn;
        SqlServerEventSerialNo = sqlServerEventSerialNo;
        CommittedPosition = committedPosition;
        Diagnostics = diagnostics;
    }

    private readonly string? _committedPosition;

    [JsonRequired]
    public int ContractVersion { get; init; }

    [JsonRequired]
    public string OperationId { get; init; }

    [JsonRequired]
    public DateTimeOffset ObservedAt { get; init; }

    [JsonRequired]
    public CdcTargetIdentity TargetIdentity { get; init; }

    [JsonRequired]
    public CdcProvider Provider { get; init; }

    [JsonRequired]
    public string? PhysicalSourceFingerprint { get; init; }

    [JsonRequired]
    public DateTimeOffset ProjectionCaughtUpObservedAt { get; init; }

    [JsonRequired]
    public DateTimeOffset BarrierCapturedAt { get; init; }

    [JsonRequired]
    public DateTimeOffset ConnectorOffsetObservedAt { get; init; }

    [JsonRequired]
    public CdcProviderBarrierState BarrierState { get; init; }

    [JsonRequired]
    public string? PostgresqlBarrierLsn { get; init; }

    [JsonRequired]
    public string? SqlServerCommitLsn { get; init; }

    [JsonRequired]
    public string? SqlServerChangeLsn { get; init; }

    [JsonRequired]
    public long? SqlServerEventSerialNo { get; init; }

    [JsonRequired]
    public string? CommittedPosition
    {
        get => _committedPosition;
        init => _committedPosition = CdcContractText.SanitizeOptionalEvidence(value);
    }

    [JsonRequired]
    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; init; }
}

public sealed record CdcConnectorOffsetObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] string ConnectorName,
    [property: JsonRequired] string TopicPrefix,
    [property: JsonRequired] CdcConnectorOffsetMatchResult SourcePartitionMatchResult,
    [property: JsonRequired] string ConnectSourcePartitionHash,
    [property: JsonRequired] bool IsSnapshot,
    [property: JsonRequired] bool IsNull,
    [property: JsonRequired] long? LsnProc,
    [property: JsonRequired] string? CommitLsn,
    [property: JsonRequired] string? ChangeLsn,
    [property: JsonRequired] long? EventSerialNo,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcSourceHistoryObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] CdcSourceHistoryContinuity Continuity,
    [property: JsonRequired] bool IncidentLatched,
    [property: JsonRequired] CdcProviderArtifactContinuityState ProviderArtifactState,
    [property: JsonRequired] CdcProviderRetainedRangeState RetainedRangeState,
    [property: JsonRequired] CdcIncidentPositionMetadata? PositionEvidence,
    [property: JsonRequired] CdcIncidentFailureCategory? IncidentFailureCategory,
    [property: JsonRequired] CdcSqlServerSchemaHistoryEnablementPhase? SchemaHistoryEnablementPhase,
    [property: JsonRequired] CdcSqlServerSchemaHistoryState SchemaHistoryState,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract
{
    [JsonRequired]
    public CdcSqlServerCdcJobEvidence? SqlServerJobs { get; init; }
}

public sealed record CdcKafkaTopicPolicy(
    [property: JsonRequired] string TopicName,
    [property: JsonRequired] CdcKafkaPolicyItemState State,
    [property: JsonRequired] int? PartitionCount,
    [property: JsonRequired] string? CleanupPolicy,
    [property: JsonRequired] int? ReplicationFactor,
    [property: JsonRequired] int? MinInSyncReplicas
);

public sealed record CdcKafkaAclPolicy(
    [property: JsonRequired] string ResourceName,
    [property: JsonRequired] CdcKafkaPolicyItemState State
);

public sealed record CdcKafkaRecordSizePolicy(
    [property: JsonRequired] CdcKafkaPolicyItemState State,
    [property: JsonRequired] int? MaxRecordBytes,
    [property: JsonRequired] int? MaxMessageBytes
);

public sealed record CdcKafkaPolicyObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] CdcKafkaPolicyState PolicyState,
    [property: JsonRequired] string DurabilityProfile,
    [property: JsonRequired] CdcKafkaTopicPolicy PublicTopic,
    [property: JsonRequired] CdcKafkaTopicPolicy ProgressTopic,
    [property: JsonRequired] CdcKafkaTopicPolicy? SchemaHistoryTopic,
    [property: JsonRequired] CdcKafkaAclPolicy PublicTopicAcls,
    [property: JsonRequired] CdcKafkaAclPolicy ProgressTopicAcls,
    [property: JsonRequired] CdcKafkaAclPolicy? SchemaHistoryTopicAcls,
    [property: JsonRequired] CdcKafkaRecordSizePolicy RecordSizePolicy,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcConnectOffsetStorePolicyObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] string WorkerKey,
    [property: JsonRequired] string OffsetStorageTopic,
    [property: JsonRequired] CdcConnectOffsetStorePolicyState PolicyState,
    [property: JsonRequired] string? CleanupPolicy,
    [property: JsonRequired] int? ReplicationFactor,
    [property: JsonRequired] int? MinInSyncReplicas,
    [property: JsonRequired] CdcConnectOffsetStoreItemState AclState,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcConnectorConfigurationObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] string ConnectorName,
    [property: JsonRequired] CdcConnectorConfigurationState ConfigurationState,
    [property: JsonRequired] string TopicPrefix,
    [property: JsonRequired] int? TaskCount,
    [property: JsonRequired] CdcConnectorConfigurationItemState TransformState,
    [property: JsonRequired] CdcConnectorConfigurationItemState ConverterState,
    [property: JsonRequired] CdcConnectorConfigurationItemState ProducerOverrideState,
    [property: JsonRequired] CdcConnectorConfigurationItemState HeartbeatState,
    [property: JsonRequired] CdcConnectorConfigurationItemState SourceIncludeListState,
    [property: JsonRequired] CdcConnectorConfigurationItemState OffsetState,
    [property: JsonRequired] CdcConnectorConfigurationItemState SchemaHistoryState,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcConnectorRuntimeObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] string ConnectorName,
    [property: JsonRequired] CdcConnectorRuntimeState ConnectorState,
    [property: JsonRequired] int? TaskCount,
    [property: JsonRequired] int? RunningTaskCount,
    [property: JsonRequired] CdcConnectorRuntimeState SoleTaskState,
    [property: JsonRequired] CdcConnectorSnapshotState SnapshotState,
    [property: JsonRequired] string? LastErrorCategory,
    [property: JsonRequired] DateTimeOffset? LastErrorObservedAt,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcConnectorLagObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] CdcConnectorLagState LagState,
    [property: JsonRequired] long? CurrentLagMilliseconds,
    [property: JsonRequired] long? ThresholdMilliseconds,
    [property: JsonRequired] long? P50LagMilliseconds,
    [property: JsonRequired] long? P95LagMilliseconds,
    [property: JsonRequired] long? P99LagMilliseconds,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public static class InitialCdcProvisioningProofValidator
{
    public static CdcContractValidationResult Validate(
        InitialCdcProvisioningProof proof,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateContractVersion(
            proof.ContractVersion,
            "$.contractVersion",
            diagnostics
        );
        CdcObservationValidationRules.ValidateRequiredToken(
            proof.ProofId,
            "$.proofId",
            "proofId",
            diagnostics
        );
        CdcObservationValidationRules.ValidateOperationId(
            proof.OperationId,
            context.OperationId,
            "$.operationId",
            diagnostics
        );
        CdcObservationValidationRules.ValidateTargetIdentity(
            proof.TargetIdentity,
            context.TargetIdentity,
            "$.targetIdentity",
            diagnostics
        );
        CdcObservationValidationRules.ValidateProvider(
            proof.Provider,
            context.TargetIdentity.Provider,
            "$.provider",
            diagnostics
        );
        CdcObservationValidationRules.ValidateRequiredToken(
            proof.SetupControllerRunId,
            "$.setupControllerRunId",
            "setupControllerRunId",
            diagnostics
        );
        ValidateDatabaseCreationMode(proof.DatabaseCreationMode, diagnostics);
        ValidateWriteAdmissionState(proof.WriteAdmissionState, diagnostics);
        CdcObservationValidationRules.ValidateTimestamp(
            proof.IssuedAt,
            context.NowUtc,
            "$.issuedAt",
            diagnostics
        );

        return diagnostics.ToValidationResult();
    }

    private static void ValidateDatabaseCreationMode(
        CdcDatabaseCreationMode mode,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(mode) || mode != CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.databaseCreationMode",
                "CDC provisioning proof databaseCreationMode must be createdForInitialCdcProvisioning."
            );
        }
    }

    private static void ValidateWriteAdmissionState(
        CdcWriteAdmissionState state,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(state) || state != CdcWriteAdmissionState.ClosedNeverOpened)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.writeAdmissionState",
                "CDC provisioning proof writeAdmissionState must be closedNeverOpened."
            );
        }
    }
}

public static class InitialCdcEligibilityObservationValidator
{
    public static CdcContractValidationResult Validate(
        InitialCdcEligibilityObservation observation,
        InitialCdcProvisioningProof proof,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        CdcObservationValidationRules.ValidateTimestamp(
            observation.DurableObservedAt,
            context.NowUtc,
            "$.durableObservedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateObservedNotBeforeDurable(
            observation.DurableObservedAt,
            observation.ObservedAt,
            "$.durableObservedAt",
            "$.observedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateRequiredToken(
            observation.SetupControllerRunId,
            "$.setupControllerRunId",
            "setupControllerRunId",
            diagnostics
        );
        if (
            !string.Equals(
                observation.SetupControllerRunId,
                proof.SetupControllerRunId,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.OperationMismatch,
                "$.setupControllerRunId",
                "CDC eligibility setupControllerRunId must match the provisioning proof."
            );
        }

        CdcObservationValidationRules.ValidateRequiredToken(
            observation.WriteAdmissionProofId,
            "$.writeAdmissionProofId",
            "writeAdmissionProofId",
            diagnostics
        );
        if (!string.Equals(observation.WriteAdmissionProofId, proof.ProofId, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.OperationMismatch,
                "$.writeAdmissionProofId",
                "CDC eligibility writeAdmissionProofId must match the provisioning proof."
            );
        }

        ValidateConsistencyScope(observation.ConsistencyScope, diagnostics);
        ValidateLifecycleState(observation.LifecycleState, diagnostics);
        ValidateCacheAheadState(observation.CacheAheadState, diagnostics);
        ValidateInitialRowsAbsent(observation, diagnostics);
        ValidateProviderConsistencyToken(observation.ProviderConsistencyToken, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateConsistencyScope(
        CdcConsistencyScope consistencyScope,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            !Enum.IsDefined(consistencyScope)
            || consistencyScope != CdcConsistencyScope.SingleProviderTransaction
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.consistencyScope",
                "CDC eligibility consistencyScope must be singleProviderTransaction."
            );
        }
    }

    private static void ValidateLifecycleState(
        CdcLifecycleState lifecycleState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(lifecycleState) || lifecycleState == CdcLifecycleState.Unknown)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.lifecycleState",
                "CDC eligibility lifecycleState must be authoritative."
            );
        }
    }

    private static void ValidateCacheAheadState(
        CdcCacheAheadState cacheAheadState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(cacheAheadState) || cacheAheadState == CdcCacheAheadState.Unknown)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.cacheAheadState",
                "CDC eligibility cacheAheadState must be authoritative."
            );
        }
    }

    private static void ValidateInitialRowsAbsent(
        InitialCdcEligibilityObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.CanonicalRowsPresent)
        {
            AddRowsPresentDiagnostic("$.canonicalRowsPresent", "canonical");
        }

        if (observation.CacheRowsPresent)
        {
            AddRowsPresentDiagnostic("$.cacheRowsPresent", "cache");
        }

        if (observation.WorkRowsPresent)
        {
            AddRowsPresentDiagnostic("$.workRowsPresent", "work");
        }

        void AddRowsPresentDiagnostic(string path, string rowSet)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC eligibility requires {rowSet} rows to be absent before binding creation."
            );
        }
    }

    private static void ValidateProviderConsistencyToken(
        string? providerConsistencyToken,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!CdcContractText.IsValidEvidenceText(providerConsistencyToken))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.UnsafeEvidence,
                "$.providerConsistencyToken",
                "CDC eligibility providerConsistencyToken must be bounded sanitized evidence."
            );
        }
    }
}

public static class CdcProjectionCorrelationObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcProjectionCorrelationObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        CdcObservationValidationRules.ValidateTimestamp(
            observation.ProjectionObservedAt,
            context.NowUtc,
            "$.projectionObservedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateObservedNotBeforeDurable(
            observation.ProjectionObservedAt,
            observation.ObservedAt,
            "$.projectionObservedAt",
            "$.observedAt",
            diagnostics
        );
        ValidateE18TargetKey(observation.E18TargetKey, context.TargetIdentity, diagnostics);
        ValidateCorrelationState(observation.CorrelationState, diagnostics);
        ValidateE18Enums(observation, diagnostics);
        ValidateEnqueueFailureCategories(observation.EnqueueFailureCategories, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateE18TargetKey(
        DocumentCacheStatusTargetKey? e18TargetKey,
        CdcTargetIdentity expectedTargetIdentity,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (e18TargetKey is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MissingRequiredField,
                "$.e18TargetKey",
                "Missing required field `e18TargetKey`."
            );
            return;
        }

        string? bindingTenantKey = CdcTargetValidator.MapE18TenantKeyToBindingTenantKey(
            e18TargetKey.TenantKey
        );
        if (bindingTenantKey is null)
        {
            diagnostics.MissingRequiredField("$.e18TargetKey.tenantKey", "tenantKey");
            return;
        }

        string dataStoreId = e18TargetKey.DataStoreId.ToString(CultureInfo.InvariantCulture);
        if (
            !string.Equals(bindingTenantKey, expectedTargetIdentity.TenantKey, StringComparison.Ordinal)
            || !string.Equals(dataStoreId, expectedTargetIdentity.DataStoreId, StringComparison.Ordinal)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.TargetMismatch,
                "$.e18TargetKey",
                "CDC projection observation E18 target key must match the CDC binding target."
            );
        }
    }

    private static void ValidateCorrelationState(
        CdcProjectionCorrelationState correlationState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(correlationState))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                "$.correlationState",
                "CDC projection correlationState is unsupported."
            );
            return;
        }

        CdcDiagnosticCategory? category = correlationState switch
        {
            CdcProjectionCorrelationState.TargetMismatch => CdcDiagnosticCategory.TargetMismatch,
            CdcProjectionCorrelationState.ProviderMismatch => CdcDiagnosticCategory.ProviderMismatch,
            CdcProjectionCorrelationState.SourceMismatch => CdcDiagnosticCategory.SourceMismatch,
            CdcProjectionCorrelationState.InvalidPayload => CdcDiagnosticCategory.MalformedPayload,
            _ => null,
        };

        if (category is not null)
        {
            diagnostics.Add(
                category.Value,
                "$.correlationState",
                "CDC projection observation correlationState must be matched or unavailable for a structurally valid observation."
            );
        }
    }

    private static void ValidateE18Enums(
        CdcProjectionCorrelationObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateEnum(
            observation.OperationalHealthStatus,
            "$.operationalHealthStatus",
            "operationalHealthStatus"
        );
        ValidateEnum(
            observation.OperationalHealthReason,
            "$.operationalHealthReason",
            "operationalHealthReason"
        );
        ValidateEnum(observation.CaughtUpStatus, "$.caughtUpStatus", "caughtUpStatus");
        ValidateEnum(observation.CaughtUpReason, "$.caughtUpReason", "caughtUpReason");
        ValidateEnum(observation.QueuePresence, "$.queuePresence", "queuePresence");

        void ValidateEnum<TEnum>(TEnum value, string path, string fieldName)
            where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(value))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidEnumValue,
                    path,
                    $"CDC projection observation {fieldName} is unsupported."
                );
            }
        }
    }

    private static void ValidateEnqueueFailureCategories(
        IReadOnlyList<DocumentCacheStatusEnqueueFailureCategory>? categories,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (categories is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MissingRequiredField,
                "$.enqueueFailureCategories",
                "Missing required field `enqueueFailureCategories`."
            );
            return;
        }

        for (int index = 0; index < categories.Count; index++)
        {
            if (!Enum.IsDefined(categories[index]))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidEnumValue,
                    $"$.enqueueFailureCategories[{index}]",
                    "CDC projection observation enqueueFailureCategories contains an unsupported category."
                );
            }
        }
    }
}

public static class CdcProviderSetupObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcProviderSetupObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        ValidateSetupMode(observation.SetupMode, diagnostics);
        ValidateSetupOutcome(observation.SetupOutcome, diagnostics);
        ValidateSetupState(observation.ArtifactInventoryState, "$.artifactInventoryState", diagnostics);
        ValidateSetupState(observation.GrantInventoryState, "$.grantInventoryState", diagnostics);
        ValidateSetupState(observation.SourceInventoryState, "$.sourceInventoryState", diagnostics);
        ValidateSetupState(observation.HeartbeatState, "$.heartbeatState", diagnostics);
        ValidateOutcomeStateConsistency(observation, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateSetupMode(CdcProviderSetupMode setupMode, CdcDiagnosticCollector diagnostics)
    {
        if (!Enum.IsDefined(setupMode))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                "$.setupMode",
                "CDC provider setup observation setupMode is unsupported."
            );
        }
    }

    private static void ValidateSetupOutcome(
        CdcProviderSetupOutcome setupOutcome,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(setupOutcome))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                "$.setupOutcome",
                "CDC provider setup observation setupOutcome is unsupported."
            );
        }
    }

    private static void ValidateSetupState(
        CdcProviderSetupState setupState,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(setupState))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                path,
                "CDC provider setup observation state is unsupported."
            );
        }
    }

    private static void ValidateOutcomeStateConsistency(
        CdcProviderSetupObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(observation.SetupOutcome))
        {
            return;
        }

        CdcProviderSetupState[] states =
        [
            observation.ArtifactInventoryState,
            observation.GrantInventoryState,
            observation.SourceInventoryState,
            observation.HeartbeatState,
        ];

        bool allStatesDefined = Array.TrueForAll(states, Enum.IsDefined);
        if (!allStatesDefined)
        {
            return;
        }

        if (
            observation.SetupOutcome == CdcProviderSetupOutcome.Satisfied
            && Array.Exists(
                states,
                state => state is not (CdcProviderSetupState.Matched or CdcProviderSetupState.NotApplicable)
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.setupOutcome",
                "CDC provider setup satisfied outcome requires matched or notApplicable inventory states."
            );
        }

        if (
            observation.SetupOutcome == CdcProviderSetupOutcome.Unknown
            && Array.TrueForAll(states, state => state != CdcProviderSetupState.Unknown)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.setupOutcome",
                "CDC provider setup unknown outcome requires at least one unknown state."
            );
        }
    }
}

public static class CdcKafkaPolicyObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcKafkaPolicyObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, null, diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult ValidateForBinding(
        CdcKafkaPolicyObservation observation,
        CdcBinding binding,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, binding, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateStructure(
        CdcKafkaPolicyObservation observation,
        CdcObservationValidationContext context,
        CdcBinding? binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        ValidatePolicyState(observation.PolicyState, diagnostics);
        CdcObservationValidationRules.ValidateRequiredToken(
            observation.DurabilityProfile,
            "$.durabilityProfile",
            "durabilityProfile",
            diagnostics
        );
        ValidateTopicPolicy(observation.PublicTopic, "$.publicTopic", true, diagnostics);
        ValidateTopicPolicy(observation.ProgressTopic, "$.progressTopic", true, diagnostics);
        ValidateTopicPolicy(
            observation.SchemaHistoryTopic,
            "$.schemaHistoryTopic",
            observation.Provider == CdcProvider.SqlServer,
            diagnostics
        );
        ValidateAclPolicy(observation.PublicTopicAcls, "$.publicTopicAcls", true, diagnostics);
        ValidateAclPolicy(observation.ProgressTopicAcls, "$.progressTopicAcls", true, diagnostics);
        ValidateAclPolicy(
            observation.SchemaHistoryTopicAcls,
            "$.schemaHistoryTopicAcls",
            observation.Provider == CdcProvider.SqlServer,
            diagnostics
        );
        ValidateRecordSizePolicy(observation.RecordSizePolicy, diagnostics);
        ValidateProviderSchemaHistoryApplicability(observation, diagnostics);
        ValidatePolicyStateConsistency(observation, diagnostics);

        if (binding is not null)
        {
            ValidateBindingDerivedNames(observation, binding, diagnostics);
        }
    }

    private static void ValidatePolicyState(
        CdcKafkaPolicyState policyState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(policyState))
        {
            diagnostics.InvalidEnumValue(
                "$.policyState",
                "CDC Kafka policy observation policyState is unsupported."
            );
        }
    }

    private static void ValidateTopicPolicy(
        CdcKafkaTopicPolicy? topicPolicy,
        string path,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (topicPolicy is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, path.Split('.')[^1]);
            }

            return;
        }

        CdcObservationValidationRules.ValidateArtifactName(
            topicPolicy.TopicName,
            $"{path}.topicName",
            "topicName",
            true,
            diagnostics
        );
        ValidateKafkaItemState(topicPolicy.State, $"{path}.state", diagnostics);
        ValidateTopicNumericEvidence(
            topicPolicy.PartitionCount,
            $"{path}.partitionCount",
            "partitionCount",
            topicPolicy.State,
            diagnostics
        );
        ValidateCleanupPolicy(
            topicPolicy.CleanupPolicy,
            $"{path}.cleanupPolicy",
            topicPolicy.State,
            diagnostics
        );
        ValidateTopicNumericEvidence(
            topicPolicy.ReplicationFactor,
            $"{path}.replicationFactor",
            "replicationFactor",
            topicPolicy.State,
            diagnostics
        );
        ValidateTopicNumericEvidence(
            topicPolicy.MinInSyncReplicas,
            $"{path}.minInSyncReplicas",
            "minInSyncReplicas",
            topicPolicy.State,
            diagnostics
        );
    }

    private static void ValidateAclPolicy(
        CdcKafkaAclPolicy? aclPolicy,
        string path,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (aclPolicy is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, path.Split('.')[^1]);
            }

            return;
        }

        CdcObservationValidationRules.ValidateArtifactName(
            aclPolicy.ResourceName,
            $"{path}.resourceName",
            "resourceName",
            true,
            diagnostics
        );
        ValidateKafkaItemState(aclPolicy.State, $"{path}.state", diagnostics);
    }

    private static void ValidateRecordSizePolicy(
        CdcKafkaRecordSizePolicy? recordSizePolicy,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (recordSizePolicy is null)
        {
            diagnostics.MissingRequiredField("$.recordSizePolicy", "recordSizePolicy");
            return;
        }

        ValidateKafkaItemState(recordSizePolicy.State, "$.recordSizePolicy.state", diagnostics);
        ValidateTopicNumericEvidence(
            recordSizePolicy.MaxRecordBytes,
            "$.recordSizePolicy.maxRecordBytes",
            "maxRecordBytes",
            recordSizePolicy.State,
            diagnostics
        );
        ValidateTopicNumericEvidence(
            recordSizePolicy.MaxMessageBytes,
            "$.recordSizePolicy.maxMessageBytes",
            "maxMessageBytes",
            recordSizePolicy.State,
            diagnostics
        );

        if (
            recordSizePolicy.State != CdcKafkaPolicyItemState.Unknown
            && recordSizePolicy.MaxRecordBytes is not null
            && recordSizePolicy.MaxMessageBytes is not null
            && recordSizePolicy.MaxRecordBytes > recordSizePolicy.MaxMessageBytes
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.recordSizePolicy.maxRecordBytes",
                "CDC Kafka policy maxRecordBytes must not exceed maxMessageBytes."
            );
        }
    }

    private static void ValidateTopicNumericEvidence(
        int? value,
        string path,
        string fieldName,
        CdcKafkaPolicyItemState state,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (state != CdcKafkaPolicyItemState.Unknown)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (value <= 0)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC Kafka policy observation {fieldName} must be positive."
            );
        }
    }

    private static void ValidateCleanupPolicy(
        string? cleanupPolicy,
        string path,
        CdcKafkaPolicyItemState state,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (cleanupPolicy is null)
        {
            if (state != CdcKafkaPolicyItemState.Unknown)
            {
                diagnostics.MissingRequiredField(path, "cleanupPolicy");
            }

            return;
        }

        if (
            cleanupPolicy.Length == 0
            || cleanupPolicy.Length > 64
            || cleanupPolicy.Contains(',', StringComparison.Ordinal)
            || !string.Equals(
                cleanupPolicy,
                CdcContractText.SanitizeRequired(cleanupPolicy),
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.UnsafeEvidence,
                path,
                "CDC Kafka policy cleanupPolicy must be bounded sanitized evidence."
            );
        }
    }

    private static void ValidateKafkaItemState(
        CdcKafkaPolicyItemState itemState,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(itemState))
        {
            diagnostics.InvalidEnumValue(path, "CDC Kafka policy observation item state is unsupported.");
        }
    }

    private static void ValidateProviderSchemaHistoryApplicability(
        CdcKafkaPolicyObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Provider == CdcProvider.Postgresql)
        {
            if (observation.SchemaHistoryTopic is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.schemaHistoryTopic",
                    "CDC Kafka policy schemaHistoryTopic is SQL Server-only evidence."
                );
            }

            if (observation.SchemaHistoryTopicAcls is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.schemaHistoryTopicAcls",
                    "CDC Kafka policy schemaHistoryTopicAcls is SQL Server-only evidence."
                );
            }

            return;
        }

        if (observation.Provider == CdcProvider.SqlServer)
        {
            if (observation.SchemaHistoryTopic is null)
            {
                diagnostics.MissingRequiredField("$.schemaHistoryTopic", "schemaHistoryTopic");
            }

            if (observation.SchemaHistoryTopicAcls is null)
            {
                diagnostics.MissingRequiredField("$.schemaHistoryTopicAcls", "schemaHistoryTopicAcls");
            }
        }
    }

    private static void ValidatePolicyStateConsistency(
        CdcKafkaPolicyObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(observation.PolicyState))
        {
            return;
        }

        CdcKafkaPolicyItemState[] states =
        [
            StateOrUnknown(observation.PublicTopic),
            StateOrUnknown(observation.ProgressTopic),
            StateOrNotApplicable(observation.SchemaHistoryTopic),
            StateOrUnknown(observation.PublicTopicAcls),
            StateOrUnknown(observation.ProgressTopicAcls),
            StateOrNotApplicable(observation.SchemaHistoryTopicAcls),
            observation.RecordSizePolicy?.State ?? CdcKafkaPolicyItemState.Unknown,
        ];

        bool allStatesDefined = Array.TrueForAll(states, Enum.IsDefined);
        if (!allStatesDefined)
        {
            return;
        }

        if (
            observation.PolicyState == CdcKafkaPolicyState.Satisfied
            && Array.Exists(
                states,
                state =>
                    state is not (CdcKafkaPolicyItemState.Satisfied or CdcKafkaPolicyItemState.NotApplicable)
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.policyState",
                "CDC Kafka policy satisfied state requires every applicable item to be satisfied."
            );
        }

        if (
            observation.PolicyState == CdcKafkaPolicyState.Invalid
            && Array.TrueForAll(states, state => state != CdcKafkaPolicyItemState.Invalid)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.policyState",
                "CDC Kafka policy invalid state requires at least one invalid item."
            );
        }

        if (
            observation.PolicyState == CdcKafkaPolicyState.Unknown
            && Array.TrueForAll(states, state => state != CdcKafkaPolicyItemState.Unknown)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.policyState",
                "CDC Kafka policy unknown state requires at least one unknown item."
            );
        }
    }

    private static CdcKafkaPolicyItemState StateOrUnknown(CdcKafkaTopicPolicy? topicPolicy) =>
        topicPolicy?.State ?? CdcKafkaPolicyItemState.Unknown;

    private static CdcKafkaPolicyItemState StateOrUnknown(CdcKafkaAclPolicy? aclPolicy) =>
        aclPolicy?.State ?? CdcKafkaPolicyItemState.Unknown;

    private static CdcKafkaPolicyItemState StateOrNotApplicable(CdcKafkaTopicPolicy? topicPolicy) =>
        topicPolicy?.State ?? CdcKafkaPolicyItemState.NotApplicable;

    private static CdcKafkaPolicyItemState StateOrNotApplicable(CdcKafkaAclPolicy? aclPolicy) =>
        aclPolicy?.State ?? CdcKafkaPolicyItemState.NotApplicable;

    private static void ValidateBindingDerivedNames(
        CdcKafkaPolicyObservation observation,
        CdcBinding binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                diagnostic.Path,
                "CDC Kafka policy binding artifacts must match the deterministic inventory."
            );
        }

        if (artifactNameResult.Inventory is null)
        {
            return;
        }

        ValidateOptionalExactMatch(
            observation.PublicTopic?.TopicName,
            artifactNameResult.Inventory.TopicName,
            "$.publicTopic.topicName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            observation.ProgressTopic?.TopicName,
            artifactNameResult.Inventory.ProgressTopicName,
            "$.progressTopic.topicName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            observation.PublicTopicAcls?.ResourceName,
            artifactNameResult.Inventory.TopicName,
            "$.publicTopicAcls.resourceName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            observation.ProgressTopicAcls?.ResourceName,
            artifactNameResult.Inventory.ProgressTopicName,
            "$.progressTopicAcls.resourceName",
            diagnostics
        );

        if (artifactNameResult.Inventory.SchemaHistoryTopicName is null)
        {
            return;
        }

        ValidateOptionalExactMatch(
            observation.SchemaHistoryTopic?.TopicName,
            artifactNameResult.Inventory.SchemaHistoryTopicName,
            "$.schemaHistoryTopic.topicName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            observation.SchemaHistoryTopicAcls?.ResourceName,
            artifactNameResult.Inventory.SchemaHistoryTopicName,
            "$.schemaHistoryTopicAcls.resourceName",
            diagnostics
        );
    }

    private static void ValidateOptionalExactMatch(
        string? actual,
        string expected,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (actual is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                path,
                "CDC Kafka policy artifact name must match the binding-derived inventory."
            );
        }
    }
}

public static class CdcConnectOffsetStorePolicyObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcConnectOffsetStorePolicyObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        CdcObservationValidationRules.ValidateRequiredToken(
            observation.WorkerKey,
            "$.workerKey",
            "workerKey",
            diagnostics
        );
        CdcObservationValidationRules.ValidateArtifactName(
            observation.OffsetStorageTopic,
            "$.offsetStorageTopic",
            "offsetStorageTopic",
            true,
            diagnostics
        );
        ValidatePolicyState(observation.PolicyState, diagnostics);
        ValidateCleanupPolicy(observation, diagnostics);
        ValidatePositiveIfRequired(
            observation.ReplicationFactor,
            "$.replicationFactor",
            "replicationFactor",
            observation.PolicyState != CdcConnectOffsetStorePolicyState.Unknown,
            diagnostics
        );
        ValidatePositiveIfRequired(
            observation.MinInSyncReplicas,
            "$.minInSyncReplicas",
            "minInSyncReplicas",
            observation.PolicyState != CdcConnectOffsetStorePolicyState.Unknown,
            diagnostics
        );
        ValidateAclState(observation.AclState, diagnostics);
        ValidatePolicyStateConsistency(observation, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidatePolicyState(
        CdcConnectOffsetStorePolicyState policyState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(policyState))
        {
            diagnostics.InvalidEnumValue(
                "$.policyState",
                "CDC Connect offset-store observation policyState is unsupported."
            );
        }
    }

    private static void ValidateCleanupPolicy(
        CdcConnectOffsetStorePolicyObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.CleanupPolicy is null)
        {
            if (observation.PolicyState != CdcConnectOffsetStorePolicyState.Unknown)
            {
                diagnostics.MissingRequiredField("$.cleanupPolicy", "cleanupPolicy");
            }

            return;
        }

        if (
            observation.CleanupPolicy.Length == 0
            || observation.CleanupPolicy.Length > 64
            || observation.CleanupPolicy.Contains(',', StringComparison.Ordinal)
            || !string.Equals(
                observation.CleanupPolicy,
                CdcContractText.SanitizeRequired(observation.CleanupPolicy),
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.UnsafeEvidence,
                "$.cleanupPolicy",
                "CDC Connect offset-store cleanupPolicy must be bounded sanitized evidence."
            );
        }
    }

    private static void ValidatePositiveIfRequired(
        int? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (value <= 0)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC Connect offset-store observation {fieldName} must be positive."
            );
        }
    }

    private static void ValidateAclState(
        CdcConnectOffsetStoreItemState aclState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(aclState))
        {
            diagnostics.InvalidEnumValue(
                "$.aclState",
                "CDC Connect offset-store observation aclState is unsupported."
            );
        }
    }

    private static void ValidatePolicyStateConsistency(
        CdcConnectOffsetStorePolicyObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(observation.PolicyState) || !Enum.IsDefined(observation.AclState))
        {
            return;
        }

        bool cleanupUnknown = observation.CleanupPolicy is null;
        bool cleanupInvalid =
            observation.CleanupPolicy is not null
            && !string.Equals(observation.CleanupPolicy, "compact", StringComparison.Ordinal);
        bool replicationUnknown = observation.ReplicationFactor is null;
        bool replicationInvalid = observation.ReplicationFactor is <= 0;
        bool minInSyncUnknown = observation.MinInSyncReplicas is null;
        bool minInSyncInvalid = observation.MinInSyncReplicas is <= 0;

        if (
            observation.PolicyState == CdcConnectOffsetStorePolicyState.Satisfied
            && (
                cleanupUnknown
                || cleanupInvalid
                || replicationUnknown
                || replicationInvalid
                || minInSyncUnknown
                || minInSyncInvalid
                || observation.AclState != CdcConnectOffsetStoreItemState.Satisfied
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.policyState",
                "CDC Connect offset-store satisfied state requires compact cleanup, positive durability values, and satisfied ACLs."
            );
        }

        if (
            observation.PolicyState == CdcConnectOffsetStorePolicyState.Invalid
            && !(
                cleanupInvalid
                || replicationInvalid
                || minInSyncInvalid
                || observation.AclState == CdcConnectOffsetStoreItemState.Invalid
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.policyState",
                "CDC Connect offset-store invalid state requires at least one invalid policy fact."
            );
        }

        if (
            observation.PolicyState == CdcConnectOffsetStorePolicyState.Unknown
            && !(
                cleanupUnknown
                || replicationUnknown
                || minInSyncUnknown
                || observation.AclState == CdcConnectOffsetStoreItemState.Unknown
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.policyState",
                "CDC Connect offset-store unknown state requires at least one unknown policy fact."
            );
        }
    }
}

public static class CdcConnectorConfigurationObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcConnectorConfigurationObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, null, diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult ValidateForBinding(
        CdcConnectorConfigurationObservation observation,
        CdcBinding binding,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, binding, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateStructure(
        CdcConnectorConfigurationObservation observation,
        CdcObservationValidationContext context,
        CdcBinding? binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        CdcObservationValidationRules.ValidateArtifactName(
            observation.ConnectorName,
            "$.connectorName",
            "connectorName",
            true,
            diagnostics
        );
        ValidateConfigurationState(observation.ConfigurationState, diagnostics);
        CdcObservationValidationRules.ValidateRequiredToken(
            observation.TopicPrefix,
            "$.topicPrefix",
            "topicPrefix",
            diagnostics
        );
        ValidateTaskCount(observation.TaskCount, diagnostics);
        ValidateConfigurationItemState(observation.TransformState, "$.transformState", diagnostics);
        ValidateConfigurationItemState(observation.ConverterState, "$.converterState", diagnostics);
        ValidateConfigurationItemState(
            observation.ProducerOverrideState,
            "$.producerOverrideState",
            diagnostics
        );
        ValidateConfigurationItemState(observation.HeartbeatState, "$.heartbeatState", diagnostics);
        ValidateConfigurationItemState(
            observation.SourceIncludeListState,
            "$.sourceIncludeListState",
            diagnostics
        );
        ValidateConfigurationItemState(observation.OffsetState, "$.offsetState", diagnostics);
        ValidateConfigurationItemState(observation.SchemaHistoryState, "$.schemaHistoryState", diagnostics);
        ValidateSchemaHistoryApplicability(observation, diagnostics);
        ValidateConfigurationStateConsistency(observation, diagnostics);

        if (binding is not null)
        {
            ValidateBindingDerivedNames(observation, binding, diagnostics);
        }
    }

    private static void ValidateConfigurationState(
        CdcConnectorConfigurationState configurationState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(configurationState))
        {
            diagnostics.InvalidEnumValue(
                "$.configurationState",
                "CDC connector configuration observation configurationState is unsupported."
            );
        }
    }

    private static void ValidateConfigurationItemState(
        CdcConnectorConfigurationItemState itemState,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(itemState))
        {
            diagnostics.InvalidEnumValue(
                path,
                "CDC connector configuration observation item state is unsupported."
            );
        }
    }

    private static void ValidateTaskCount(int? taskCount, CdcDiagnosticCollector diagnostics)
    {
        if (taskCount is null)
        {
            diagnostics.MissingRequiredField("$.taskCount", "taskCount");
            return;
        }

        if (taskCount != 1)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.taskCount",
                "CDC connector configuration taskCount must be exactly one."
            );
        }
    }

    private static void ValidateSchemaHistoryApplicability(
        CdcConnectorConfigurationObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Provider == CdcProvider.Postgresql)
        {
            if (observation.SchemaHistoryState != CdcConnectorConfigurationItemState.NotApplicable)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.schemaHistoryState",
                    "CDC connector configuration schemaHistoryState must be notApplicable for PostgreSQL."
                );
            }

            return;
        }

        if (
            observation.Provider == CdcProvider.SqlServer
            && observation.SchemaHistoryState == CdcConnectorConfigurationItemState.NotApplicable
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.schemaHistoryState",
                "CDC connector configuration schemaHistoryState is required for SQL Server."
            );
        }
    }

    private static void ValidateConfigurationStateConsistency(
        CdcConnectorConfigurationObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(observation.ConfigurationState))
        {
            return;
        }

        CdcConnectorConfigurationItemState[] states =
        [
            observation.TransformState,
            observation.ConverterState,
            observation.ProducerOverrideState,
            observation.HeartbeatState,
            observation.SourceIncludeListState,
            observation.OffsetState,
            observation.SchemaHistoryState,
        ];

        bool allStatesDefined = Array.TrueForAll(states, Enum.IsDefined);
        if (!allStatesDefined)
        {
            return;
        }

        if (
            observation.ConfigurationState == CdcConnectorConfigurationState.Matched
            && (
                observation.TaskCount != 1
                || Array.Exists(
                    states,
                    state =>
                        state
                            is not (
                                CdcConnectorConfigurationItemState.Matched
                                or CdcConnectorConfigurationItemState.NotApplicable
                            )
                )
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.configurationState",
                "CDC connector matched configuration requires exactly one task and matched applicable settings."
            );
        }

        if (
            observation.ConfigurationState == CdcConnectorConfigurationState.Invalid
            && Array.TrueForAll(states, state => state != CdcConnectorConfigurationItemState.Invalid)
            && observation.TaskCount == 1
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.configurationState",
                "CDC connector invalid configuration requires at least one invalid setting."
            );
        }

        if (
            observation.ConfigurationState == CdcConnectorConfigurationState.Unknown
            && Array.TrueForAll(states, state => state != CdcConnectorConfigurationItemState.Unknown)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.configurationState",
                "CDC connector unknown configuration requires at least one unknown setting."
            );
        }
    }

    private static void ValidateBindingDerivedNames(
        CdcConnectorConfigurationObservation observation,
        CdcBinding binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                diagnostic.Path,
                "CDC connector configuration binding artifacts must match the deterministic inventory."
            );
        }

        if (artifactNameResult.Inventory is null)
        {
            return;
        }

        if (
            !string.Equals(
                observation.ConnectorName,
                artifactNameResult.Inventory.ConnectorName,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                "$.connectorName",
                "CDC connector configuration connectorName must match the binding-derived inventory."
            );
        }

        if (
            !string.Equals(
                observation.TopicPrefix,
                artifactNameResult.Inventory.TopicPrefix,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                "$.topicPrefix",
                "CDC connector configuration topicPrefix must match the binding-derived inventory."
            );
        }
    }
}

public static class CdcConnectorRuntimeObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcConnectorRuntimeObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, null, diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult ValidateForBinding(
        CdcConnectorRuntimeObservation observation,
        CdcBinding binding,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, binding, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateStructure(
        CdcConnectorRuntimeObservation observation,
        CdcObservationValidationContext context,
        CdcBinding? binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        CdcObservationValidationRules.ValidateArtifactName(
            observation.ConnectorName,
            "$.connectorName",
            "connectorName",
            true,
            diagnostics
        );
        ValidateRuntimeState(observation.ConnectorState, "$.connectorState", diagnostics);
        ValidateTaskCount(observation.TaskCount, "$.taskCount", "taskCount", diagnostics);
        ValidateTaskCount(
            observation.RunningTaskCount,
            "$.runningTaskCount",
            "runningTaskCount",
            diagnostics
        );
        ValidateRuntimeState(observation.SoleTaskState, "$.soleTaskState", diagnostics);
        ValidateSnapshotState(observation.SnapshotState, diagnostics);
        ValidateLastError(observation, context.NowUtc, diagnostics);
        ValidateRuntimeStateConsistency(observation, diagnostics);

        if (binding is not null)
        {
            ValidateBindingDerivedName(observation, binding, diagnostics);
        }
    }

    private static void ValidateRuntimeState(
        CdcConnectorRuntimeState state,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(state))
        {
            diagnostics.InvalidEnumValue(path, "CDC connector runtime observation state is unsupported.");
        }
    }

    private static void ValidateSnapshotState(
        CdcConnectorSnapshotState snapshotState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(snapshotState))
        {
            diagnostics.InvalidEnumValue(
                "$.snapshotState",
                "CDC connector runtime observation snapshotState is unsupported."
            );
        }
    }

    private static void ValidateTaskCount(
        int? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            diagnostics.MissingRequiredField(path, fieldName);
            return;
        }

        if (value < 0)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC connector runtime observation {fieldName} must not be negative."
            );
        }
    }

    private static void ValidateLastError(
        CdcConnectorRuntimeObservation observation,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool failed =
            observation.ConnectorState == CdcConnectorRuntimeState.Failed
            || observation.SoleTaskState == CdcConnectorRuntimeState.Failed;
        if (observation.LastErrorCategory is null)
        {
            if (failed || observation.LastErrorObservedAt is not null)
            {
                diagnostics.MissingRequiredField("$.lastErrorCategory", "lastErrorCategory");
            }
        }
        else
        {
            CdcObservationValidationRules.ValidateRequiredToken(
                observation.LastErrorCategory,
                "$.lastErrorCategory",
                "lastErrorCategory",
                diagnostics
            );
        }

        if (observation.LastErrorObservedAt is null)
        {
            return;
        }

        CdcObservationValidationRules.ValidateTimestamp(
            observation.LastErrorObservedAt.Value,
            nowUtc,
            "$.lastErrorObservedAt",
            diagnostics
        );
        if (observation.LastErrorObservedAt > observation.ObservedAt)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                "$.lastErrorObservedAt",
                "CDC connector runtime lastErrorObservedAt must not be later than observedAt."
            );
        }
    }

    private static void ValidateRuntimeStateConsistency(
        CdcConnectorRuntimeObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.TaskCount is null || observation.RunningTaskCount is null)
        {
            return;
        }

        if (observation.RunningTaskCount > observation.TaskCount)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.runningTaskCount",
                "CDC connector runtime runningTaskCount must not exceed taskCount."
            );
        }

        if (observation.TaskCount != 1)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.taskCount",
                "CDC connector runtime taskCount must be exactly one."
            );
        }

        if (
            observation.ConnectorState == CdcConnectorRuntimeState.Running
            && (
                observation.RunningTaskCount != 1
                || observation.SoleTaskState != CdcConnectorRuntimeState.Running
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.connectorState",
                "CDC connector runtime running connector requires its sole task to be running."
            );
        }

        if (
            observation.SoleTaskState == CdcConnectorRuntimeState.Running
            && observation.RunningTaskCount == 0
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.soleTaskState",
                "CDC connector runtime running sole task requires a running task count."
            );
        }
    }

    private static void ValidateBindingDerivedName(
        CdcConnectorRuntimeObservation observation,
        CdcBinding binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                diagnostic.Path,
                "CDC connector runtime binding artifacts must match the deterministic inventory."
            );
        }

        if (
            artifactNameResult.Inventory is not null
            && !string.Equals(
                observation.ConnectorName,
                artifactNameResult.Inventory.ConnectorName,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                "$.connectorName",
                "CDC connector runtime connectorName must match the binding-derived inventory."
            );
        }
    }
}

public static class CdcConnectorLagObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcConnectorLagObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        ValidateLagState(observation.LagState, diagnostics);
        bool lagRequired = observation.LagState != CdcConnectorLagState.Unknown;
        ValidateLagValue(
            observation.CurrentLagMilliseconds,
            "$.currentLagMilliseconds",
            "currentLagMilliseconds",
            lagRequired,
            diagnostics
        );
        ValidateLagValue(
            observation.ThresholdMilliseconds,
            "$.thresholdMilliseconds",
            "thresholdMilliseconds",
            lagRequired,
            diagnostics
        );
        ValidateLagValue(
            observation.P50LagMilliseconds,
            "$.p50LagMilliseconds",
            "p50LagMilliseconds",
            lagRequired,
            diagnostics
        );
        ValidateLagValue(
            observation.P95LagMilliseconds,
            "$.p95LagMilliseconds",
            "p95LagMilliseconds",
            lagRequired,
            diagnostics
        );
        ValidateLagValue(
            observation.P99LagMilliseconds,
            "$.p99LagMilliseconds",
            "p99LagMilliseconds",
            lagRequired,
            diagnostics
        );
        ValidateLagStateConsistency(observation, diagnostics);
        ValidateLagQuantileOrdering(observation, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateLagState(CdcConnectorLagState lagState, CdcDiagnosticCollector diagnostics)
    {
        if (!Enum.IsDefined(lagState))
        {
            diagnostics.InvalidEnumValue(
                "$.lagState",
                "CDC connector lag observation lagState is unsupported."
            );
        }
    }

    private static void ValidateLagValue(
        long? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (value < 0)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC connector lag observation {fieldName} must not be negative."
            );
        }
    }

    private static void ValidateLagStateConsistency(
        CdcConnectorLagObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            observation.LagState == CdcConnectorLagState.WithinThreshold
            && observation.CurrentLagMilliseconds is not null
            && observation.ThresholdMilliseconds is not null
            && observation.CurrentLagMilliseconds > observation.ThresholdMilliseconds
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.currentLagMilliseconds",
                "CDC connector lag withinThreshold state requires current lag to be within the threshold."
            );
        }

        if (
            observation.LagState == CdcConnectorLagState.Exceeded
            && observation.CurrentLagMilliseconds is not null
            && observation.ThresholdMilliseconds is not null
            && observation.CurrentLagMilliseconds <= observation.ThresholdMilliseconds
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.currentLagMilliseconds",
                "CDC connector lag exceeded state requires current lag to exceed the threshold."
            );
        }
    }

    private static void ValidateLagQuantileOrdering(
        CdcConnectorLagObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            observation.P50LagMilliseconds is null
            || observation.P95LagMilliseconds is null
            || observation.P99LagMilliseconds is null
        )
        {
            return;
        }

        if (
            observation.P50LagMilliseconds > observation.P95LagMilliseconds
            || observation.P95LagMilliseconds > observation.P99LagMilliseconds
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                "$.p50LagMilliseconds",
                "CDC connector lag quantiles must be ordered p50 <= p95 <= p99."
            );
        }
    }
}

public static class CdcProviderBarrierObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcProviderBarrierObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        ValidateOrderingEvidence(observation, context.NowUtc, diagnostics);
        ValidateBarrierState(observation.BarrierState, diagnostics);
        ValidateProviderBarrierFields(observation, diagnostics);
        ValidateCommittedPosition(observation, diagnostics);
        ValidateStateConsistency(observation, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateOrderingEvidence(
        CdcProviderBarrierObservation observation,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateTimestamp(
            observation.ProjectionCaughtUpObservedAt,
            nowUtc,
            "$.projectionCaughtUpObservedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateTimestamp(
            observation.BarrierCapturedAt,
            nowUtc,
            "$.barrierCapturedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateTimestamp(
            observation.ConnectorOffsetObservedAt,
            nowUtc,
            "$.connectorOffsetObservedAt",
            diagnostics
        );

        ValidateNotAfter(
            observation.ProjectionCaughtUpObservedAt,
            observation.BarrierCapturedAt,
            "$.projectionCaughtUpObservedAt",
            "$.barrierCapturedAt",
            diagnostics
        );
        ValidateNotAfter(
            observation.BarrierCapturedAt,
            observation.ConnectorOffsetObservedAt,
            "$.barrierCapturedAt",
            "$.connectorOffsetObservedAt",
            diagnostics
        );
        ValidateNotAfter(
            observation.ConnectorOffsetObservedAt,
            observation.ObservedAt,
            "$.connectorOffsetObservedAt",
            "$.observedAt",
            diagnostics
        );
    }

    private static void ValidateNotAfter(
        DateTimeOffset earlier,
        DateTimeOffset later,
        string earlierPath,
        string laterPath,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (earlier > later)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                earlierPath,
                $"CDC provider barrier observation requires {earlierPath} to be no later than {laterPath}."
            );
        }
    }

    private static void ValidateBarrierState(
        CdcProviderBarrierState barrierState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(barrierState))
        {
            diagnostics.InvalidEnumValue(
                "$.barrierState",
                "CDC provider barrier observation barrierState is unsupported."
            );
        }
    }

    private static void ValidateProviderBarrierFields(
        CdcProviderBarrierObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool barrierRequired =
            observation.BarrierState is CdcProviderBarrierState.Reached or CdcProviderBarrierState.NotReached;

        switch (observation.Provider)
        {
            case CdcProvider.Postgresql:
                ValidatePostgresqlBarrierFields(observation, barrierRequired, diagnostics);
                break;
            case CdcProvider.SqlServer:
                ValidateSqlServerBarrierFields(observation, barrierRequired, diagnostics);
                break;
            default:
                break;
        }
    }

    private static void ValidatePostgresqlBarrierFields(
        CdcProviderBarrierObservation observation,
        bool barrierRequired,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateProviderInapplicable(
            observation.SqlServerCommitLsn,
            "$.sqlServerCommitLsn",
            "sqlServerCommitLsn",
            diagnostics
        );
        ValidateProviderInapplicable(
            observation.SqlServerChangeLsn,
            "$.sqlServerChangeLsn",
            "sqlServerChangeLsn",
            diagnostics
        );
        ValidateProviderInapplicable(
            observation.SqlServerEventSerialNo,
            "$.sqlServerEventSerialNo",
            "sqlServerEventSerialNo",
            diagnostics
        );

        if (barrierRequired || observation.PostgresqlBarrierLsn is not null)
        {
            Add(
                CdcPostgresqlProviderPosition.ParseWalLsn(
                    observation.PostgresqlBarrierLsn,
                    "$.postgresqlBarrierLsn"
                )
            );
        }

        void Add(CdcPostgresqlWalPositionResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidateSqlServerBarrierFields(
        CdcProviderBarrierObservation observation,
        bool barrierRequired,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateProviderInapplicable(
            observation.PostgresqlBarrierLsn,
            "$.postgresqlBarrierLsn",
            "postgresqlBarrierLsn",
            diagnostics
        );

        if (barrierRequired || observation.SqlServerCommitLsn is not null)
        {
            Add(
                CdcSqlServerProviderPositionParser.ParseLsn(
                    observation.SqlServerCommitLsn,
                    "$.sqlServerCommitLsn"
                )
            );
        }

        if (barrierRequired || observation.SqlServerChangeLsn is not null)
        {
            Add(
                CdcSqlServerProviderPositionParser.ParseLsn(
                    observation.SqlServerChangeLsn,
                    "$.sqlServerChangeLsn"
                )
            );
        }

        if (barrierRequired || observation.SqlServerEventSerialNo is not null)
        {
            CdcSqlServerEventSerialNoResult result = CdcSqlServerProviderPositionParser.ParseEventSerialNo(
                observation.SqlServerEventSerialNo,
                "$.sqlServerEventSerialNo"
            );
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }

        void Add(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidateCommittedPosition(
        CdcProviderBarrierObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool required = observation.BarrierState == CdcProviderBarrierState.Reached;
        CdcObservationValidationRules.ValidateSanitizedEvidenceText(
            observation.CommittedPosition,
            "$.committedPosition",
            "committedPosition",
            required,
            diagnostics
        );
    }

    private static void ValidateStateConsistency(
        CdcProviderBarrierObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            observation.BarrierState == CdcProviderBarrierState.NotReached
            && observation.CommittedPosition is not null
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.committedPosition",
                "CDC provider barrier notReached observation must not report a committed position as reached."
            );
        }
    }

    private static void ValidateProviderInapplicable(
        object? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is not null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC provider barrier observation {fieldName} is not applicable for this provider."
            );
        }
    }
}

public static class CdcConnectorOffsetObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcConnectorOffsetObservation observation,
        CdcObservationValidationContext context,
        string? expectedConnectSourcePartitionHash = null
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, expectedConnectSourcePartitionHash, diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult ValidateForBinding(
        CdcConnectorOffsetObservation observation,
        CdcBinding binding,
        CdcObservationValidationContext context,
        string? expectedConnectSourcePartitionHash = null
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, expectedConnectSourcePartitionHash, diagnostics);
        ValidateBindingDerivedNames(observation, binding, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateStructure(
        CdcConnectorOffsetObservation observation,
        CdcObservationValidationContext context,
        string? expectedConnectSourcePartitionHash,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        CdcObservationValidationRules.ValidateArtifactName(
            observation.ConnectorName,
            "$.connectorName",
            "connectorName",
            true,
            diagnostics
        );
        CdcObservationValidationRules.ValidateRequiredToken(
            observation.TopicPrefix,
            "$.topicPrefix",
            "topicPrefix",
            diagnostics
        );
        CdcConnectorOffsetValidationRules.ValidateSourcePartitionMatch(
            observation.SourcePartitionMatchResult,
            diagnostics
        );
        CdcConnectorOffsetValidationRules.ValidateSnapshotFlag(observation.IsSnapshot, diagnostics);
        CdcConnectorOffsetValidationRules.ValidateNullFlag(observation.IsNull, diagnostics);
        ValidateConnectSourcePartitionHash(observation, expectedConnectSourcePartitionHash, diagnostics);
        ValidateProviderOffsetFields(observation, diagnostics);
    }

    private static void ValidateConnectSourcePartitionHash(
        CdcConnectorOffsetObservation observation,
        string? expectedConnectSourcePartitionHash,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateHashValue(
            observation.ConnectSourcePartitionHash,
            "$.connectSourcePartitionHash",
            "connectSourcePartitionHash",
            true,
            diagnostics
        );

        if (
            expectedConnectSourcePartitionHash is not null
            && !string.Equals(
                observation.ConnectSourcePartitionHash,
                expectedConnectSourcePartitionHash,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.SourceMismatch,
                "$.connectSourcePartitionHash",
                "CDC connector offset source partition hash must match the expected Connect source partition."
            );
        }
    }

    private static void ValidateProviderOffsetFields(
        CdcConnectorOffsetObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        switch (observation.Provider)
        {
            case CdcProvider.Postgresql:
                ValidatePostgresqlOffsetFields(observation, diagnostics);
                break;
            case CdcProvider.SqlServer:
                ValidateSqlServerOffsetFields(observation, diagnostics);
                break;
            default:
                break;
        }
    }

    private static void ValidatePostgresqlOffsetFields(
        CdcConnectorOffsetObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateProviderInapplicable(observation.CommitLsn, "$.commitLsn", "commitLsn", diagnostics);
        ValidateProviderInapplicable(observation.ChangeLsn, "$.changeLsn", "changeLsn", diagnostics);
        ValidateProviderInapplicable(
            observation.EventSerialNo,
            "$.eventSerialNo",
            "eventSerialNo",
            diagnostics
        );

        if (observation.LsnProc is null)
        {
            diagnostics.MissingRequiredField("$.lsnProc", "lsnProc");
        }
    }

    private static void ValidateSqlServerOffsetFields(
        CdcConnectorOffsetObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateProviderInapplicable(observation.LsnProc, "$.lsnProc", "lsnProc", diagnostics);

        Add(CdcSqlServerProviderPositionParser.ParseLsn(observation.CommitLsn, "$.commitLsn"));
        Add(CdcSqlServerProviderPositionParser.ParseLsn(observation.ChangeLsn, "$.changeLsn"));
        CdcSqlServerEventSerialNoResult eventSerialNoResult =
            CdcSqlServerProviderPositionParser.ParseEventSerialNo(
                observation.EventSerialNo,
                "$.eventSerialNo"
            );
        foreach (CdcDiagnostic diagnostic in eventSerialNoResult.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        void Add(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidateBindingDerivedNames(
        CdcConnectorOffsetObservation observation,
        CdcBinding binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                diagnostic.Path,
                "CDC connector offset binding artifacts must match the deterministic inventory."
            );
        }

        if (artifactNameResult.Inventory is null)
        {
            return;
        }

        if (
            !string.Equals(
                observation.ConnectorName,
                artifactNameResult.Inventory.ConnectorName,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                "$.connectorName",
                "CDC connector offset connectorName must match the binding-derived inventory."
            );
        }

        if (
            !string.Equals(
                observation.TopicPrefix,
                artifactNameResult.Inventory.ConnectorName,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                "$.topicPrefix",
                "CDC connector offset topicPrefix must match the binding-derived connector name."
            );
        }
    }

    private static void ValidateProviderInapplicable(
        object? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is not null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC connector offset observation {fieldName} is not applicable for this provider."
            );
        }
    }
}

public static class CdcSourceHistoryObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcSourceHistoryObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, null, diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult ValidateForBinding(
        CdcSourceHistoryObservation observation,
        CdcBinding binding,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, binding, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateStructure(
        CdcSourceHistoryObservation observation,
        CdcObservationValidationContext context,
        CdcBinding? binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        ValidateContinuity(observation.Continuity, diagnostics);
        ValidateProviderArtifactState(observation.ProviderArtifactState, diagnostics);
        ValidateRetainedRangeState(observation.RetainedRangeState, diagnostics);
        ValidateSqlServerJobs(observation, diagnostics);
        ValidateSchemaHistory(observation, diagnostics);
        ValidateIncidentCandidate(observation, binding, context.NowUtc, diagnostics);
        ValidateContinuityStateConsistency(observation, diagnostics);
        ValidatePositionEvidence(observation, binding, diagnostics);
    }

    private static void ValidateContinuity(
        CdcSourceHistoryContinuity continuity,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(continuity))
        {
            diagnostics.InvalidEnumValue(
                "$.continuity",
                "CDC source-history observation continuity is unsupported."
            );
        }
    }

    private static void ValidateProviderArtifactState(
        CdcProviderArtifactContinuityState providerArtifactState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(providerArtifactState))
        {
            diagnostics.InvalidEnumValue(
                "$.providerArtifactState",
                "CDC source-history observation providerArtifactState is unsupported."
            );
        }
    }

    private static void ValidateRetainedRangeState(
        CdcProviderRetainedRangeState retainedRangeState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(retainedRangeState))
        {
            diagnostics.InvalidEnumValue(
                "$.retainedRangeState",
                "CDC source-history observation retainedRangeState is unsupported."
            );
        }
    }

    private static void ValidateSqlServerJobs(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Provider == CdcProvider.Postgresql)
        {
            if (observation.SqlServerJobs is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.sqlServerJobs",
                    "CDC source-history sqlServerJobs is SQL Server-only evidence."
                );
            }

            return;
        }

        if (observation.Provider != CdcProvider.SqlServer)
        {
            return;
        }

        if (observation.SqlServerJobs is null)
        {
            diagnostics.MissingRequiredField("$.sqlServerJobs", "sqlServerJobs");
            return;
        }

        ValidateSqlServerJobState(
            observation.SqlServerJobs.CaptureJobState,
            "$.sqlServerJobs.captureJobState",
            "captureJobState",
            diagnostics
        );
        ValidateSqlServerJobState(
            observation.SqlServerJobs.CleanupJobState,
            "$.sqlServerJobs.cleanupJobState",
            "cleanupJobState",
            diagnostics
        );
    }

    private static void ValidateSqlServerJobState(
        CdcSqlServerCdcJobState state,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(state))
        {
            diagnostics.InvalidEnumValue(path, $"CDC source-history observation {fieldName} is unsupported.");
        }
    }

    private static void ValidateSchemaHistory(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(observation.SchemaHistoryState))
        {
            diagnostics.InvalidEnumValue(
                "$.schemaHistoryState",
                "CDC source-history observation schemaHistoryState is unsupported."
            );
            return;
        }

        if (observation.Provider == CdcProvider.Postgresql)
        {
            if (observation.SchemaHistoryEnablementPhase is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.schemaHistoryEnablementPhase",
                    "CDC source-history schemaHistoryEnablementPhase is SQL Server-only evidence."
                );
            }

            if (observation.SchemaHistoryState != CdcSqlServerSchemaHistoryState.NotApplicable)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.schemaHistoryState",
                    "CDC source-history schemaHistoryState must be notApplicable for PostgreSQL."
                );
            }

            return;
        }

        if (observation.Provider != CdcProvider.SqlServer)
        {
            return;
        }

        if (observation.SchemaHistoryEnablementPhase is null)
        {
            diagnostics.MissingRequiredField(
                "$.schemaHistoryEnablementPhase",
                "schemaHistoryEnablementPhase"
            );
        }
        else if (!Enum.IsDefined(observation.SchemaHistoryEnablementPhase.Value))
        {
            diagnostics.InvalidEnumValue(
                "$.schemaHistoryEnablementPhase",
                "CDC source-history observation schemaHistoryEnablementPhase is unsupported."
            );
        }

        if (observation.SchemaHistoryState == CdcSqlServerSchemaHistoryState.NotApplicable)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.schemaHistoryState",
                "CDC source-history schemaHistoryState is required for SQL Server."
            );
        }
    }

    private static void ValidateIncidentCandidate(
        CdcSourceHistoryObservation observation,
        CdcBinding? binding,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Continuity != CdcSourceHistoryContinuity.Lost)
        {
            if (observation.IncidentFailureCategory is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.incidentFailureCategory",
                    "CDC source-history incidentFailureCategory is only valid for lost continuity."
                );
            }

            return;
        }

        if (observation.IncidentFailureCategory is null)
        {
            diagnostics.MissingRequiredField("$.incidentFailureCategory", "incidentFailureCategory");
            return;
        }

        if (!Enum.IsDefined(observation.IncidentFailureCategory.Value))
        {
            diagnostics.InvalidEnumValue(
                "$.incidentFailureCategory",
                "CDC source-history observation incidentFailureCategory is unsupported."
            );
            return;
        }

        if (binding is not null && observation.PositionEvidence is not null)
        {
            CdcIncident incident = new(
                CdcJsonContract.CurrentContractVersion,
                CdcIncidentType.SourceHistoryContinuityLost,
                observation.ObservedAt,
                binding.ToCompleteBindingIdentity(),
                observation.IncidentFailureCategory.Value,
                observation.PositionEvidence
            );
            CdcContractValidationResult incidentResult = CdcIncidentValidator.ValidateForBinding(
                incident,
                binding,
                nowUtc
            );

            foreach (CdcDiagnostic diagnostic in incidentResult.Diagnostics)
            {
                diagnostics.Add(
                    diagnostic.Category,
                    $"$.positionEvidence{CdcProofValidationRules.TrimRootPath(diagnostic.Path)}",
                    "CDC source-history incident candidate position evidence is invalid."
                );
            }
        }
    }

    private static void ValidateContinuityStateConsistency(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Continuity == CdcSourceHistoryContinuity.Healthy)
        {
            ValidateHealthyState(observation, diagnostics);
        }

        if (
            observation.Continuity != CdcSourceHistoryContinuity.Lost
            && observation.ProviderArtifactState
                is CdcProviderArtifactContinuityState.Missing
                    or CdcProviderArtifactContinuityState.Recreated
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.providerArtifactState",
                "CDC source-history missing or recreated provider artifacts must be reported as lost continuity."
            );
        }

        if (
            observation.Continuity != CdcSourceHistoryContinuity.Lost
            && observation.RetainedRangeState == CdcProviderRetainedRangeState.Gap
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.retainedRangeState",
                "CDC source-history retained range gap must be reported as lost continuity."
            );
        }

        ValidateSqlServerSchemaHistoryConsistency(observation, diagnostics);
        ValidateSqlServerJobConsistency(observation, diagnostics);
    }

    private static void ValidateHealthyState(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.IncidentLatched)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.incidentLatched",
                "CDC source-history cannot be healthy after a valid incident latch."
            );
        }

        if (observation.ProviderArtifactState != CdcProviderArtifactContinuityState.ExactMatch)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.providerArtifactState",
                "CDC source-history healthy continuity requires exact-match provider artifacts."
            );
        }

        if (observation.RetainedRangeState != CdcProviderRetainedRangeState.CoversCommittedOffset)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.retainedRangeState",
                "CDC source-history healthy continuity requires retained history to cover the committed offset."
            );
        }

        if (
            observation.Provider == CdcProvider.SqlServer
            && observation.SchemaHistoryState != CdcSqlServerSchemaHistoryState.Valid
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.schemaHistoryState",
                "CDC source-history healthy SQL Server continuity requires valid schema history."
            );
        }

        if (
            observation.Provider == CdcProvider.SqlServer
            && observation.SqlServerJobs is not null
            && !observation.SqlServerJobs.IsHealthy
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.sqlServerJobs",
                "CDC source-history healthy SQL Server continuity requires healthy capture and cleanup jobs."
            );
        }
    }

    private static void ValidateSqlServerSchemaHistoryConsistency(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Provider != CdcProvider.SqlServer)
        {
            return;
        }

        bool terminalSchemaHistoryLoss =
            observation.SchemaHistoryState
            is CdcSqlServerSchemaHistoryState.Missing
                or CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset
                or CdcSqlServerSchemaHistoryState.RequiredRecordLost;

        if (
            terminalSchemaHistoryLoss
            && observation.SchemaHistoryEnablementPhase
                == CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission
            && observation.Continuity == CdcSourceHistoryContinuity.Lost
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.schemaHistoryEnablementPhase",
                "CDC source-history SQL Server schema-history loss cannot terminally latch before initial admission."
            );
        }

        if (
            terminalSchemaHistoryLoss
            && observation.SchemaHistoryEnablementPhase
                == CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission
            && observation.Continuity != CdcSourceHistoryContinuity.Lost
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.schemaHistoryState",
                "CDC source-history terminal SQL Server schema-history loss after admission must report lost continuity."
            );
        }
    }

    private static void ValidateSqlServerJobConsistency(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Provider != CdcProvider.SqlServer || observation.SqlServerJobs is null)
        {
            return;
        }

        if (!observation.SqlServerJobs.HasMissingJob)
        {
            return;
        }

        if (
            observation.Continuity != CdcSourceHistoryContinuity.Lost
            || observation.ProviderArtifactState != CdcProviderArtifactContinuityState.Missing
            || observation.IncidentFailureCategory != CdcIncidentFailureCategory.ProviderArtifactMissing
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.sqlServerJobs",
                "CDC source-history missing SQL Server capture or cleanup jobs must report provider artifact loss."
            );
        }
    }

    private static void ValidatePositionEvidence(
        CdcSourceHistoryObservation observation,
        CdcBinding? binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool required =
            observation.Continuity is CdcSourceHistoryContinuity.Healthy or CdcSourceHistoryContinuity.Lost;
        if (observation.PositionEvidence is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField("$.positionEvidence", "positionEvidence");
            }

            return;
        }

        ValidatePositionEvidenceShape(
            observation.PositionEvidence,
            observation.Provider,
            observation,
            diagnostics
        );
        ValidateRetainedRangeEvidence(
            observation.PositionEvidence,
            observation.Provider,
            observation,
            diagnostics
        );

        if (binding is not null)
        {
            ValidatePositionEvidenceArtifacts(observation.PositionEvidence, binding, diagnostics);
        }
    }

    private static void ValidatePositionEvidenceShape(
        CdcIncidentPositionMetadata positionEvidence,
        CdcProvider provider,
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.ConnectorName,
            "$.positionEvidence.connectorName",
            "connectorName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.TopicName,
            "$.positionEvidence.topicName",
            "topicName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.ProgressTopicName,
            "$.positionEvidence.progressTopicName",
            "progressTopicName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.SchemaHistoryTopicName,
            "$.positionEvidence.schemaHistoryTopicName",
            "schemaHistoryTopicName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.ProviderArtifactName,
            "$.positionEvidence.providerArtifactName",
            "providerArtifactName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateHashValue(
            positionEvidence.ConnectSourcePartitionHash,
            "$.positionEvidence.connectSourcePartitionHash",
            "connectSourcePartitionHash",
            false,
            diagnostics
        );
        ValidateProviderPositionFields(positionEvidence, provider, observation, diagnostics);
        ValidateUnavailableFacts(positionEvidence.UnavailableFacts, diagnostics);
    }

    private static void ValidateProviderPositionFields(
        CdcIncidentPositionMetadata positionEvidence,
        CdcProvider provider,
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool providerPositionRequired = RequiresProviderPosition(observation, positionEvidence);

        switch (provider)
        {
            case CdcProvider.Postgresql:
                CdcObservationValidationRules.ValidateProviderPositionText(
                    positionEvidence.LsnProc,
                    "$.positionEvidence.lsnProc",
                    "lsnProc",
                    providerPositionRequired,
                    diagnostics
                );
                ValidateProviderInapplicable(
                    positionEvidence.CommitLsn,
                    "$.positionEvidence.commitLsn",
                    "commitLsn",
                    diagnostics
                );
                ValidateProviderInapplicable(
                    positionEvidence.ChangeLsn,
                    "$.positionEvidence.changeLsn",
                    "changeLsn",
                    diagnostics
                );
                ValidateProviderInapplicable(
                    positionEvidence.EventSerialNo,
                    "$.positionEvidence.eventSerialNo",
                    "eventSerialNo",
                    diagnostics
                );
                break;
            case CdcProvider.SqlServer:
                ValidateProviderInapplicable(
                    positionEvidence.LsnProc,
                    "$.positionEvidence.lsnProc",
                    "lsnProc",
                    diagnostics
                );
                if (providerPositionRequired || positionEvidence.CommitLsn is not null)
                {
                    Add(
                        CdcSqlServerProviderPositionParser.ParseLsn(
                            positionEvidence.CommitLsn,
                            "$.positionEvidence.commitLsn"
                        )
                    );
                }

                if (providerPositionRequired || positionEvidence.ChangeLsn is not null)
                {
                    Add(
                        CdcSqlServerProviderPositionParser.ParseLsn(
                            positionEvidence.ChangeLsn,
                            "$.positionEvidence.changeLsn"
                        )
                    );
                }

                if (providerPositionRequired || positionEvidence.EventSerialNo is not null)
                {
                    CdcSqlServerEventSerialNoResult result =
                        CdcSqlServerProviderPositionParser.ParseEventSerialNo(
                            positionEvidence.EventSerialNo,
                            "$.positionEvidence.eventSerialNo"
                        );
                    foreach (CdcDiagnostic diagnostic in result.Diagnostics)
                    {
                        diagnostics.Add(diagnostic);
                    }
                }

                break;
        }

        void Add(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static bool RequiresProviderPosition(
        CdcSourceHistoryObservation observation,
        CdcIncidentPositionMetadata positionEvidence
    )
    {
        if (observation.Continuity == CdcSourceHistoryContinuity.Healthy)
        {
            return true;
        }

        if (
            positionEvidence.UnavailableFacts is not null
            && positionEvidence.UnavailableFacts.Contains(CdcIncidentUnavailableFact.ConnectOffset)
            && observation.IncidentFailureCategory
                is CdcIncidentFailureCategory.ProviderArtifactMissing
                    or CdcIncidentFailureCategory.ProviderArtifactRecreated
                    or CdcIncidentFailureCategory.RetainedHistoryGap
        )
        {
            return false;
        }

        return observation.IncidentFailureCategory
            is CdcIncidentFailureCategory.ProviderArtifactMissing
                or CdcIncidentFailureCategory.ProviderArtifactRecreated
                or CdcIncidentFailureCategory.RetainedHistoryGap
                or CdcIncidentFailureCategory.SchemaHistoryMissing
                or CdcIncidentFailureCategory.SchemaHistoryEmptyWithRetainedOffset
                or CdcIncidentFailureCategory.SchemaHistoryRequiredRecordLost;
    }

    private static void ValidateRetainedRangeEvidence(
        CdcIncidentPositionMetadata positionEvidence,
        CdcProvider provider,
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool retainedRangeRequired =
            observation.RetainedRangeState
            is CdcProviderRetainedRangeState.CoversCommittedOffset
                or CdcProviderRetainedRangeState.Gap;
        if (!retainedRangeRequired)
        {
            ValidateOptionalRetainedRangePosition(
                positionEvidence.RetainedRangeStart,
                "$.positionEvidence.retainedRangeStart",
                provider,
                diagnostics
            );
            ValidateOptionalRetainedRangePosition(
                positionEvidence.RetainedRangeEnd,
                "$.positionEvidence.retainedRangeEnd",
                provider,
                diagnostics
            );
            return;
        }

        if (positionEvidence.RetainedRangeStart is null)
        {
            diagnostics.MissingRequiredField("$.positionEvidence.retainedRangeStart", "retainedRangeStart");
        }

        if (positionEvidence.RetainedRangeEnd is null)
        {
            diagnostics.MissingRequiredField("$.positionEvidence.retainedRangeEnd", "retainedRangeEnd");
        }

        if (positionEvidence.RetainedRangeStart is null || positionEvidence.RetainedRangeEnd is null)
        {
            return;
        }

        ValidateRetainedRangeOrdering(
            positionEvidence.RetainedRangeStart,
            positionEvidence.RetainedRangeEnd,
            provider,
            diagnostics
        );
    }

    private static void ValidateOptionalRetainedRangePosition(
        string? value,
        string path,
        CdcProvider provider,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            return;
        }

        ValidateRetainedRangePosition(value, path, provider, diagnostics);
    }

    private static void ValidateRetainedRangeOrdering(
        string retainedRangeStart,
        string retainedRangeEnd,
        CdcProvider provider,
        CdcDiagnosticCollector diagnostics
    )
    {
        switch (provider)
        {
            case CdcProvider.Postgresql:
                CdcPostgresqlWalPositionResult start = CdcPostgresqlProviderPosition.ParseWalLsn(
                    retainedRangeStart,
                    "$.positionEvidence.retainedRangeStart"
                );
                CdcPostgresqlWalPositionResult end = CdcPostgresqlProviderPosition.ParseWalLsn(
                    retainedRangeEnd,
                    "$.positionEvidence.retainedRangeEnd"
                );
                AddPostgresql(start);
                AddPostgresql(end);
                if (
                    start.Position is not null
                    && end.Position is not null
                    && start.Position.Value.CompareTo(end.Position.Value) > 0
                )
                {
                    diagnostics.Add(
                        CdcDiagnosticCategory.InvalidOrdering,
                        "$.positionEvidence.retainedRangeStart",
                        "CDC source-history retainedRangeStart must not be after retainedRangeEnd."
                    );
                }

                break;
            case CdcProvider.SqlServer:
                CdcSqlServerLsnResult sqlStart = CdcSqlServerProviderPositionParser.ParseLsn(
                    retainedRangeStart,
                    "$.positionEvidence.retainedRangeStart"
                );
                CdcSqlServerLsnResult sqlEnd = CdcSqlServerProviderPositionParser.ParseLsn(
                    retainedRangeEnd,
                    "$.positionEvidence.retainedRangeEnd"
                );
                AddSqlServer(sqlStart);
                AddSqlServer(sqlEnd);
                if (
                    sqlStart.Lsn is not null
                    && sqlEnd.Lsn is not null
                    && sqlStart.Lsn.Value.CompareTo(sqlEnd.Lsn.Value) > 0
                )
                {
                    diagnostics.Add(
                        CdcDiagnosticCategory.InvalidOrdering,
                        "$.positionEvidence.retainedRangeStart",
                        "CDC source-history retainedRangeStart must not be after retainedRangeEnd."
                    );
                }

                break;
        }

        void AddPostgresql(CdcPostgresqlWalPositionResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }

        void AddSqlServer(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidateRetainedRangePosition(
        string value,
        string path,
        CdcProvider provider,
        CdcDiagnosticCollector diagnostics
    )
    {
        switch (provider)
        {
            case CdcProvider.Postgresql:
                AddPostgresql(CdcPostgresqlProviderPosition.ParseWalLsn(value, path));
                break;
            case CdcProvider.SqlServer:
                AddSqlServer(CdcSqlServerProviderPositionParser.ParseLsn(value, path));
                break;
        }

        void AddPostgresql(CdcPostgresqlWalPositionResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }

        void AddSqlServer(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidatePositionEvidenceArtifacts(
        CdcIncidentPositionMetadata positionEvidence,
        CdcBinding binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                diagnostic.Path,
                "CDC source-history binding artifacts must match the deterministic inventory."
            );
        }

        if (artifactNameResult.Inventory is null)
        {
            return;
        }

        ValidateOptionalExactMatch(
            positionEvidence.ConnectorName,
            artifactNameResult.Inventory.ConnectorName,
            "$.positionEvidence.connectorName",
            "connectorName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            positionEvidence.TopicName,
            artifactNameResult.Inventory.TopicName,
            "$.positionEvidence.topicName",
            "topicName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            positionEvidence.ProgressTopicName,
            artifactNameResult.Inventory.ProgressTopicName,
            "$.positionEvidence.progressTopicName",
            "progressTopicName",
            diagnostics
        );

        if (artifactNameResult.Inventory.SchemaHistoryTopicName is null)
        {
            if (positionEvidence.SchemaHistoryTopicName is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.ArtifactNameMismatch,
                    "$.positionEvidence.schemaHistoryTopicName",
                    "CDC source-history schemaHistoryTopicName is not applicable for this binding provider."
                );
            }
        }
        else
        {
            ValidateOptionalExactMatch(
                positionEvidence.SchemaHistoryTopicName,
                artifactNameResult.Inventory.SchemaHistoryTopicName,
                "$.positionEvidence.schemaHistoryTopicName",
                "schemaHistoryTopicName",
                diagnostics
            );
        }

        if (positionEvidence.ProviderArtifactName is not null)
        {
            HashSet<string> providerArtifactNames = artifactNameResult
                .Inventory.GovernedArtifacts.Where(artifact => IsProviderArtifact(artifact.Kind))
                .Select(artifact => artifact.Name)
                .ToHashSet(StringComparer.Ordinal);

            if (!providerArtifactNames.Contains(positionEvidence.ProviderArtifactName))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.ArtifactNameMismatch,
                    "$.positionEvidence.providerArtifactName",
                    "CDC source-history providerArtifactName must match a binding-derived provider artifact."
                );
            }
        }
    }

    private static bool IsProviderArtifact(CdcGovernedArtifactKind kind) =>
        kind
            is CdcGovernedArtifactKind.PostgresqlPublication
                or CdcGovernedArtifactKind.PostgresqlLogicalSlot
                or CdcGovernedArtifactKind.SqlServerCdcGatingRole
                or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                or CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat;

    private static void ValidateOptionalExactMatch(
        string? value,
        string expected,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is not null && !string.Equals(value, expected, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                path,
                $"CDC source-history {fieldName} must match the binding-derived artifact."
            );
        }
    }

    private static void ValidateProviderInapplicable(
        object? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is not null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC source-history observation {fieldName} is not applicable for this provider."
            );
        }
    }

    private static void ValidateUnavailableFacts(
        IReadOnlyList<CdcIncidentUnavailableFact>? unavailableFacts,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (unavailableFacts is null)
        {
            diagnostics.MissingRequiredField("$.positionEvidence.unavailableFacts", "unavailableFacts");
            return;
        }

        HashSet<CdcIncidentUnavailableFact> seenFacts = [];
        for (int index = 0; index < unavailableFacts.Count; index++)
        {
            CdcIncidentUnavailableFact fact = unavailableFacts[index];
            if (!Enum.IsDefined(fact))
            {
                diagnostics.InvalidEnumValue(
                    $"$.positionEvidence.unavailableFacts[{index}]",
                    "CDC source-history unavailableFacts contains an unsupported fact."
                );
            }
            else if (!seenFacts.Add(fact))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    $"$.positionEvidence.unavailableFacts[{index}]",
                    "CDC source-history unavailableFacts must not contain duplicate facts."
                );
            }
        }
    }
}

internal static class CdcObservationValidationRules
{
    public static void ValidateEnvelope(
        ICdcObservationContract observation,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateContractVersion(observation.ContractVersion, "$.contractVersion", diagnostics);
        ValidateOperationId(observation.OperationId, context.OperationId, "$.operationId", diagnostics);
        ValidateTimestamp(observation.ObservedAt, context.NowUtc, "$.observedAt", diagnostics);
        ValidateTargetIdentity(
            observation.TargetIdentity,
            context.TargetIdentity,
            "$.targetIdentity",
            diagnostics
        );
        ValidateProvider(observation.Provider, context.TargetIdentity.Provider, "$.provider", diagnostics);
        ValidatePhysicalSourceFingerprint(
            observation.PhysicalSourceFingerprint,
            context.PhysicalSourceFingerprint,
            "$.physicalSourceFingerprint",
            diagnostics
        );
        ValidateDiagnostics(observation.Diagnostics, diagnostics);
    }

    public static void ValidateContractVersion(
        int contractVersion,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (contractVersion != CdcJsonContract.CurrentContractVersion)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidContractVersion,
                path,
                $"CDC observation contract version `{contractVersion}` is not supported. Expected `{CdcJsonContract.CurrentContractVersion}`."
            );
        }
    }

    public static void ValidateOperationId(
        string? operationId,
        string expectedOperationId,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateRequiredToken(operationId, path, "operationId", diagnostics);
        if (!string.Equals(operationId, expectedOperationId, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.OperationMismatch,
                path,
                "CDC observation operationId must match the current operation."
            );
        }
    }

    public static void ValidateRequiredToken(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            value is null
            || value.Length == 0
            || value.Length > 128
            || !CdcKafkaSafeTokenValidator.IsValid(value)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC observation {fieldName} must be a non-empty safe token."
            );
        }
    }

    public static void ValidateTimestamp(
        DateTimeOffset timestamp,
        DateTimeOffset nowUtc,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        DateTimeOffset normalizedNowUtc = nowUtc.ToUniversalTime();
        if (timestamp.Offset != TimeSpan.Zero || timestamp > normalizedNowUtc)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidTimestamp,
                path,
                "CDC observation timestamp must be UTC and must not be in the future."
            );
        }
    }

    public static void ValidateObservedNotBeforeDurable(
        DateTimeOffset durableObservedAt,
        DateTimeOffset observedAt,
        string durablePath,
        string observedPath,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (durableObservedAt > observedAt)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                durablePath,
                $"CDC observation durable evidence timestamp must not be later than {observedPath}."
            );
        }
    }

    public static void ValidateTargetIdentity(
        CdcTargetIdentity? targetIdentity,
        CdcTargetIdentity expectedTargetIdentity,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (targetIdentity is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MissingRequiredField,
                path,
                "Missing required field `targetIdentity`."
            );
            return;
        }

        CdcContractValidationResult identityResult = CdcTargetValidator.ValidateBindingIdentity(
            CdcBindingIdentity.FromTargetIdentity(targetIdentity)
        );
        foreach (CdcDiagnostic diagnostic in identityResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.TargetMismatch,
                $"{path}{CdcProofValidationRules.TrimRootPath(diagnostic.Path)}",
                "CDC observation targetIdentity is invalid."
            );
        }

        ValidateProvider(
            targetIdentity.Provider,
            expectedTargetIdentity.Provider,
            $"{path}.provider",
            diagnostics
        );

        if (targetIdentity != expectedTargetIdentity)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.TargetMismatch,
                path,
                "CDC observation targetIdentity must match the current target."
            );
        }
    }

    public static void ValidateProvider(
        CdcProvider provider,
        CdcProvider expectedProvider,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(provider))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                path,
                "CDC observation provider is unsupported."
            );
            return;
        }

        if (provider != expectedProvider)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ProviderMismatch,
                path,
                "CDC observation provider must match the current target provider."
            );
        }
    }

    private static void ValidatePhysicalSourceFingerprint(
        string? physicalSourceFingerprint,
        string? expectedPhysicalSourceFingerprint,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (expectedPhysicalSourceFingerprint is null)
        {
            if (physicalSourceFingerprint is not null)
            {
                ValidateHashValue(
                    physicalSourceFingerprint,
                    path,
                    "physicalSourceFingerprint",
                    true,
                    diagnostics
                );
            }

            return;
        }

        ValidateHashValue(physicalSourceFingerprint, path, "physicalSourceFingerprint", true, diagnostics);
        if (!CdcSha256ValueValidator.IsValid(physicalSourceFingerprint))
        {
            return;
        }

        if (
            !string.Equals(
                physicalSourceFingerprint,
                expectedPhysicalSourceFingerprint,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.SourceMismatch,
                path,
                "CDC observation physicalSourceFingerprint must match the current source."
            );
        }
    }

    public static void ValidateArtifactName(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (
            value.Length == 0
            || value.Length > CdcArtifactNameGenerator.MaximumKafkaOrConnectNameLength
            || !CdcKafkaSafeTokenValidator.IsValid(value)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC observation {fieldName} must be a non-empty safe artifact name."
            );
        }
    }

    public static void ValidateHashValue(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcSha256ValueValidator.Validate(
            value,
            path,
            fieldName,
            required,
            diagnostics,
            CdcDiagnosticCategory.MalformedPayload,
            $"CDC observation {fieldName} must be `sha256:` plus 64 lowercase hex characters."
        );
    }

    public static void ValidateProviderPositionText(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (value.Length == 0 || value.Length > 256 || !IsProviderPosition(value))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedPayload,
                path,
                $"CDC observation {fieldName} must be a provider-normalized position."
            );
        }
    }

    public static void ValidateSanitizedEvidenceText(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (!CdcContractText.IsValidEvidenceText(value))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.UnsafeEvidence,
                path,
                $"CDC observation {fieldName} must be bounded sanitized evidence."
            );
        }
    }

    private static void ValidateDiagnostics(
        IReadOnlyList<CdcDiagnostic>? diagnosticsFromObservation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (diagnosticsFromObservation is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MissingRequiredField,
                "$.diagnostics",
                "Missing required field `diagnostics`."
            );
            return;
        }

        for (int index = 0; index < diagnosticsFromObservation.Count; index++)
        {
            if (diagnosticsFromObservation[index] is null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.MalformedPayload,
                    $"$.diagnostics[{index}]",
                    "CDC observation diagnostics must not contain null items."
                );
            }
        }
    }

    private static bool IsProviderPosition(string value) =>
        IsDecimalInteger(value) || IsPostgresqlWalLsn(value) || IsSqlServerLsn(value);

    private static bool IsDecimalInteger(string value) =>
        value.Length != 0 && value.All(character => character is >= '0' and <= '9');

    private static bool IsPostgresqlWalLsn(string value)
    {
        int separatorIndex = value.IndexOf('/', StringComparison.Ordinal);
        return separatorIndex > 0
            && separatorIndex == value.LastIndexOf('/')
            && separatorIndex < value.Length - 1
            && value[..separatorIndex].All(IsHex)
            && value[(separatorIndex + 1)..].All(IsHex);
    }

    private static bool IsSqlServerLsn(string value)
    {
        string[] parts = value.Split(':');
        return parts.Length == 3
            && parts[0].Length == 8
            && parts[1].Length == 8
            && parts[2].Length == 4
            && Array.TrueForAll(parts, part => part.All(IsHex));
    }

    private static bool IsHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

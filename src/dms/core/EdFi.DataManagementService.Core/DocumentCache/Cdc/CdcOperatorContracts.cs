// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcBindingState>))]
public enum CdcBindingState
{
    BindingPresent,
    BindingMissing,
    BindingMismatch,
    IncidentLatched,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcSourceHistoryContinuity>))]
public enum CdcSourceHistoryContinuity
{
    Healthy,
    Unknown,
    Lost,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcAdmissionState>))]
public enum CdcAdmissionState
{
    Admitted,
    NotAdmitted,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcRetryClassification>))]
public enum CdcRetryClassification
{
    RetryGuardedActivation,
    ResumeProviderTopicConnectorSetup,
    RejectUnboundTracking,
    RejectBindingMismatch,
    RejectResettingLifecycle,
    RejectRebuildingLifecycle,
    RejectCacheAheadLatch,
    RejectUnexpectedRows,
    RejectNotInitialWorkflow,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcRetryAction>))]
public enum CdcRetryAction
{
    Proceed,
    FailClosed,
    RetireUnusedBindingAndReprovision,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcIncidentType>))]
public enum CdcIncidentType
{
    SourceHistoryContinuityLost,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcIncidentFailureCategory>))]
public enum CdcIncidentFailureCategory
{
    ProviderArtifactMissing,
    ProviderArtifactRecreated,
    RetainedHistoryGap,
    ConnectOffsetMissing,
    ConnectOffsetMalformed,
    ConnectSourcePartitionMismatch,
    SchemaHistoryMissing,
    SchemaHistoryEmptyWithRetainedOffset,
    SchemaHistoryRequiredRecordLost,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcAdoptionVerificationKind>))]
public enum CdcAdoptionVerificationKind
{
    PhysicalSource,
    ProviderArtifacts,
    Connector,
    ConnectorConfig,
    KafkaTopics,
    KafkaAcls,
    ConnectOffsets,
    SourceHistoryContinuity,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcAdoptionVerificationState>))]
public enum CdcAdoptionVerificationState
{
    ExactMatch,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcCleanupMode>))]
public enum CdcCleanupMode
{
    RetireBindingGeneration,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcCleanupState>))]
public enum CdcCleanupState
{
    Deleted,
    NotFound,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcGovernedArtifactKind>))]
public enum CdcGovernedArtifactKind
{
    KafkaConnectConnector,
    ConnectSourceOffsets,
    PublicTopic,
    ProgressTopic,
    PublicTopicAcls,
    ProgressTopicAcls,
    PostgresqlPublication,
    PostgresqlLogicalSlot,
    SqlServerCdcGatingRole,
    SqlServerCaptureInstanceDocument,
    SqlServerCaptureInstanceDocumentCache,
    SqlServerCaptureInstanceCdcHeartbeat,
    SchemaHistoryTopic,
    SchemaHistoryTopicAcls,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcIncidentUnavailableFact>))]
public enum CdcIncidentUnavailableFact
{
    ProviderArtifact,
    ProviderRetainedRange,
    ConnectOffset,
    SchemaHistory,
}

public sealed record CdcBinding(
    [property: JsonRequired] int Version,
    [property: JsonRequired] string DeploymentKey,
    [property: JsonRequired] string TenantKey,
    [property: JsonRequired] string DataStoreId,
    [property: JsonRequired] string InstanceKey,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string PhysicalSourceFingerprint,
    [property: JsonRequired] string ConnectorName,
    [property: JsonRequired] string TopicName,
    [property: JsonRequired] int PartitionCount,
    [property: JsonRequired] string PartitionerAlgorithm,
    [property: JsonRequired] int ContractVersion
) : ICdcJsonContract
{
    public CdcTargetIdentity ToTargetIdentity() =>
        new(DeploymentKey, TenantKey, DataStoreId, InstanceKey, Generation, Provider);

    public CdcBindingIdentity ToBindingIdentity() =>
        new(DeploymentKey, TenantKey, DataStoreId, InstanceKey, Generation);

    public CdcCompleteBindingIdentity ToCompleteBindingIdentity() =>
        new(
            DeploymentKey,
            TenantKey,
            DataStoreId,
            InstanceKey,
            Generation,
            Provider,
            PhysicalSourceFingerprint,
            ConnectorName,
            TopicName
        );
}

public sealed record CdcCompleteBindingIdentity(
    [property: JsonRequired] string DeploymentKey,
    [property: JsonRequired] string TenantKey,
    [property: JsonRequired] string DataStoreId,
    [property: JsonRequired] string InstanceKey,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string PhysicalSourceFingerprint,
    [property: JsonRequired] string ConnectorName,
    [property: JsonRequired] string TopicName
)
{
    public CdcTargetIdentity ToTargetIdentity() =>
        new(DeploymentKey, TenantKey, DataStoreId, InstanceKey, Generation, Provider);

    public CdcBindingIdentity ToBindingIdentity() =>
        new(DeploymentKey, TenantKey, DataStoreId, InstanceKey, Generation);
}

public sealed record CdcBindingStateContract(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcBindingState State,
    CdcBinding? Binding,
    CdcIncident? Incident
) : ICdcJsonContract;

public sealed record CdcStatus(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcReadiness Readiness,
    [property: JsonRequired] CdcBlockingCategory PrimaryBlockingCategory,
    [property: JsonRequired] IReadOnlyList<CdcTargetStatus> Targets
) : ICdcJsonContract;

public sealed record CdcTargetStatus(
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcReadiness Readiness,
    [property: JsonRequired] CdcBlockingCategory PrimaryBlockingCategory,
    [property: JsonRequired] CdcComponent Binding,
    [property: JsonRequired] CdcComponent Projection,
    [property: JsonRequired] CdcComponent ProviderSetup,
    [property: JsonRequired] CdcComponent ProviderBarrier,
    [property: JsonRequired] CdcSourceHistoryComponent SourceHistory,
    [property: JsonRequired] CdcComponent KafkaPolicy,
    [property: JsonRequired] CdcComponent ConnectOffsetStore,
    [property: JsonRequired] CdcComponent ConnectorConfig,
    [property: JsonRequired] CdcComponent ConnectorRuntime,
    [property: JsonRequired] CdcComponent Lag,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
);

public sealed record CdcSourceHistoryComponent
{
    [JsonConstructor]
    public CdcSourceHistoryComponent(
        CdcComponentState state,
        CdcBlockingCategory category,
        DateTimeOffset? observedAt,
        string? message,
        CdcSourceHistoryContinuity continuity,
        bool incidentLatched
    )
    {
        CdcComponent component = new(state, category, observedAt, message);

        State = component.State;
        Category = component.Category;
        ObservedAt = component.ObservedAt;
        Message = component.Message;
        Continuity = continuity;
        IncidentLatched = incidentLatched;
    }

    [JsonRequired]
    public CdcComponentState State { get; init; }

    [JsonRequired]
    public CdcBlockingCategory Category { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public string? Message { get; init; }

    [JsonRequired]
    public CdcSourceHistoryContinuity Continuity { get; init; }

    [JsonRequired]
    public bool IncidentLatched { get; init; }

    public static CdcSourceHistoryComponent FromComponent(
        CdcComponent component,
        CdcSourceHistoryContinuity continuity,
        bool incidentLatched
    )
    {
        ArgumentNullException.ThrowIfNull(component);

        return new(
            component.State,
            component.Category,
            component.ObservedAt,
            component.Message,
            continuity,
            incidentLatched
        );
    }
}

public sealed record CdcAdmission(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcAdmissionState AdmissionState,
    [property: JsonRequired] CdcBlockingCategory PrimaryBlockingCategory,
    [property: JsonRequired] CdcAdmissionSteps Steps,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcJsonContract;

public sealed record CdcAdmissionSteps(
    [property: JsonRequired] CdcComponent Binding,
    [property: JsonRequired] CdcComponent GuardedTrackingActivation,
    [property: JsonRequired] CdcComponent ProviderSetup,
    [property: JsonRequired] CdcComponent ConnectorAndTopicValidation,
    [property: JsonRequired] CdcComponent FirstProjectionCaughtUp,
    [property: JsonRequired] CdcComponent ProviderBarrier,
    [property: JsonRequired] CdcComponent SourceHistory,
    [property: JsonRequired] CdcComponent SecondProjectionCaughtUp,
    [property: JsonRequired] CdcComponent Lag
);

public sealed record CdcRetry(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcRetryClassification RetryClassification,
    [property: JsonRequired] CdcRetryAction Action,
    [property: JsonRequired] CdcBlockingCategory PrimaryBlockingCategory,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcJsonContract;

public sealed record CdcIncident(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] CdcIncidentType IncidentType,
    [property: JsonRequired] DateTimeOffset LatchedAt,
    [property: JsonRequired] CdcCompleteBindingIdentity BindingIdentity,
    [property: JsonRequired] CdcIncidentFailureCategory FailureCategory,
    [property: JsonRequired] CdcIncidentPositionMetadata PositionMetadata
) : ICdcJsonContract;

public sealed record CdcIncidentPositionMetadata(
    string? ConnectorName,
    string? TopicName,
    string? ProgressTopicName,
    string? SchemaHistoryTopicName,
    string? ProviderArtifactName,
    string? ConnectSourcePartitionHash,
    string? LsnProc,
    string? CommitLsn,
    string? ChangeLsn,
    long? EventSerialNo,
    string? RetainedRangeStart,
    string? RetainedRangeEnd,
    [property: JsonRequired] IReadOnlyList<CdcIncidentUnavailableFact> UnavailableFacts
);

public sealed record CdcAdoptionProof(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset VerifiedAt,
    [property: JsonRequired] CdcBinding Binding,
    [property: JsonRequired] IReadOnlyList<CdcAdoptionVerificationResult> VerificationResults
) : ICdcJsonContract;

public sealed record CdcAdoptionVerificationResult
{
    [JsonConstructor]
    public CdcAdoptionVerificationResult(
        CdcAdoptionVerificationKind verificationKind,
        CdcAdoptionVerificationState state,
        string evidenceSummary
    )
    {
        VerificationKind = verificationKind;
        State = state;
        EvidenceSummary = evidenceSummary;
    }

    private readonly string _evidenceSummary = CdcContractText.EvidenceUnavailable;

    [JsonRequired]
    public CdcAdoptionVerificationKind VerificationKind { get; init; }

    [JsonRequired]
    public CdcAdoptionVerificationState State { get; init; }

    [JsonRequired]
    public string EvidenceSummary
    {
        get => _evidenceSummary;
        init => _evidenceSummary = CdcContractText.SanitizeRequiredEvidence(value);
    }
}

public sealed record CdcCleanupProof(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset VerifiedAt,
    [property: JsonRequired] CdcCompleteBindingIdentity BindingIdentity,
    [property: JsonRequired] CdcCleanupMode CleanupMode,
    [property: JsonRequired] IReadOnlyList<CdcGovernedArtifact> GovernedArtifacts
) : ICdcJsonContract;

public sealed record CdcGovernedArtifact
{
    [JsonConstructor]
    public CdcGovernedArtifact(
        CdcGovernedArtifactKind artifactKind,
        string artifactName,
        CdcCleanupState cleanupState,
        string evidenceSummary
    )
    {
        ArtifactKind = artifactKind;
        ArtifactName = CdcContractText.SanitizeRequired(artifactName);
        CleanupState = cleanupState;
        EvidenceSummary = evidenceSummary;
    }

    private readonly string _evidenceSummary = CdcContractText.EvidenceUnavailable;

    [JsonRequired]
    public CdcGovernedArtifactKind ArtifactKind { get; init; }

    [JsonRequired]
    public string ArtifactName { get; init; }

    [JsonRequired]
    public CdcCleanupState CleanupState { get; init; }

    [JsonRequired]
    public string EvidenceSummary
    {
        get => _evidenceSummary;
        init => _evidenceSummary = CdcContractText.SanitizeRequiredEvidence(value);
    }
}

internal static class CdcContractText
{
    public const string EvidenceUnavailable = "CDC evidence unavailable.";

    private const int MaximumTextLength = 512;

    public static string SanitizeRequired(string? value)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return EvidenceUnavailable;
        }

        return sanitized.Length <= MaximumTextLength ? sanitized : sanitized[..MaximumTextLength];
    }

    public static string SanitizeRequiredEvidence(string? value)
    {
        string sanitized = SanitizeRequired(value);
        if (
            CdcSensitiveText.ContainsSensitiveFragment(value)
            || CdcSensitiveText.ContainsSensitiveFragment(sanitized)
        )
        {
            return EvidenceUnavailable;
        }

        return sanitized;
    }

    public static string? SanitizeOptionalEvidence(string? value) =>
        value is null ? null : SanitizeRequiredEvidence(value);

    public static bool IsValidEvidenceText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value != EvidenceUnavailable
        && value.Length <= MaximumTextLength
        && string.Equals(value, SanitizeRequired(value), StringComparison.Ordinal)
        && !CdcSensitiveText.ContainsSensitiveFragment(value);
}

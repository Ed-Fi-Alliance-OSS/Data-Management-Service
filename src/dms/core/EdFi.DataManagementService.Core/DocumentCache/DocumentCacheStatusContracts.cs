// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.DocumentCache;

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusResolutionStatus>))]
public enum DocumentCacheStatusResolutionStatus
{
    Resolved,
    Unresolved,
    Unknown,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusResolutionReason>))]
public enum DocumentCacheStatusResolutionReason
{
    None,
    TargetNotFound,
    CmsUnavailable,
    CmsUnauthorized,
    CmsTimeout,
    InvalidCmsResponse,
    ProviderMetadataMissing,
    ProviderMetadataUnknown,
    ProviderMismatch,
    ConnectionInputMissing,
    PhysicalSourceFingerprintFailure,
    TargetRemoved,
    TargetReplaced,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusEligibilityStatus>))]
public enum DocumentCacheStatusEligibilityStatus
{
    Eligible,
    Ineligible,
    Unknown,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusReason>))]
public enum DocumentCacheStatusReason
{
    None,
    UnresolvedTarget,
    ProviderMetadataMissing,
    ProviderMetadataUnknown,
    ProviderMismatch,
    ConnectionInputMissing,
    PhysicalSourceFingerprintFailure,
    EffectiveSchemaCompatibilityFailure,
    ResourceKeyCompatibilityFailure,
    InventoryInvalid,
    EnqueueTriggerUnavailable,
    SqlServerPrerequisiteFailed,
    UnsupportedPrerequisiteIncident,
    TargetRemoved,
    TargetReplaced,
    RuntimeNotObserved,
    RuntimeCancelled,
    RuntimeFaulted,
    TargetBackoff,
    StatusEndpointTimeout,
    StatusObservationTimeout,
    ProviderObservationFailed,
    StateMissingOrInvalid,
    LifecycleDisabled,
    LifecycleResetting,
    LifecycleRebuilding,
    CacheAheadRecoveryRequired,
    QueueNotEmpty,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusInventoryStatus>))]
public enum DocumentCacheStatusInventoryStatus
{
    Valid,
    Invalid,
    Unknown,
    NotObserved,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusEnqueueTriggerStatus>))]
public enum DocumentCacheStatusEnqueueTriggerStatus
{
    Enabled,
    Disabled,
    Invalid,
    Unknown,
    NotObserved,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusInventoryReason>))]
public enum DocumentCacheStatusInventoryReason
{
    None,
    Missing,
    Disabled,
    Invalid,
    Unreadable,
    LegacyArtifactPresent,
    PrivilegeFailure,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusProviderPrerequisiteStatus>))]
public enum DocumentCacheStatusProviderPrerequisiteStatus
{
    Satisfied,
    Unsatisfied,
    UnsupportedIncident,
    Unknown,
    NotApplicable,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusProviderPrerequisiteReason>))]
public enum DocumentCacheStatusProviderPrerequisiteReason
{
    None,
    Disabled,
    Unreadable,
    UnsupportedIncident,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusLifecycleState>))]
public enum DocumentCacheStatusLifecycleState
{
    Tracking,
    Disabled,
    Resetting,
    Rebuilding,
    Invalid,
    Unknown,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusAvailability>))]
public enum DocumentCacheStatusAvailability
{
    Available,
    Unavailable,
    Unknown,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusCacheAheadState>))]
public enum DocumentCacheStatusCacheAheadState
{
    Clear,
    RecoveryRequired,
    Unknown,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheOperationalHealthStatus>))]
public enum DocumentCacheOperationalHealthStatus
{
    Operational,
    NonOperational,
    Unknown,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheCaughtUpStatus>))]
public enum DocumentCacheCaughtUpStatus
{
    CaughtUp,
    NotCaughtUp,
    Unknown,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusQueuePresence>))]
public enum DocumentCacheStatusQueuePresence
{
    Empty,
    NotEmpty,
    Unknown,
    Unavailable,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusBacklogEstimateKind>))]
public enum DocumentCacheStatusBacklogEstimateKind
{
    Unavailable,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusExecutionState>))]
public enum DocumentCacheStatusExecutionState
{
    NotObserved,
    Starting,
    Idle,
    WaitingForPoll,
    WaitingForConcurrency,
    Active,
    TargetBackoff,
    Cancelling,
    Cancelled,
    Faulted,
    Stopped,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusActiveCommandStatus>))]
public enum DocumentCacheStatusActiveCommandStatus
{
    Running,
    Cancelling,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusEndedCommandOutcome>))]
public enum DocumentCacheStatusEndedCommandOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Rejected,
    TimedOut,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusTargetDiagnosticCategory>))]
public enum DocumentCacheStatusTargetDiagnosticCategory
{
    TargetResolution,
    Inventory,
    ProviderPrerequisite,
    RuntimeFault,
    TargetBackoff,
    TargetInvariant,
    ProviderObservationFailed,
    StatusObservationTimeout,
    StatusEndpointTimeout,
    DirectFillInvariant,
    AdministrativeCommand,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusDocumentDiagnosticCategory>))]
public enum DocumentCacheStatusDocumentDiagnosticCategory
{
    MaterializationFailed,
    WriterFailed,
    SourceChanged,
    MissingSource,
    CacheAheadSuspected,
    PoisonRetryScheduled,
}

[JsonConverter(
    typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusPoisonTraversalDiagnosticCategory>)
)]
public enum DocumentCacheStatusPoisonTraversalDiagnosticCategory
{
    RetryScheduled,
    PageCapacityExhausted,
    SkippedUntilRetry,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusEnqueueFailureCategory>))]
public enum DocumentCacheStatusEnqueueFailureCategory
{
    StateMissingOrInvalid,
    EnqueueTriggerUnavailable,
    WorkPersistenceFailed,
    ProviderTimeout,
    ProviderUnavailable,
    UnclassifiedProviderFailure,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusCanonicalOperation>))]
public enum DocumentCacheStatusCanonicalOperation
{
    Insert,
    Update,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheStatusResourceKind>))]
public enum DocumentCacheStatusResourceKind
{
    Resource,
    Descriptor,
}

public sealed record DocumentCacheStatusResponse
{
    public DocumentCacheStatusResponse(
        DateTimeOffset observedAt,
        IEnumerable<DocumentCacheStatusTarget> targets
    )
    {
        ArgumentNullException.ThrowIfNull(targets);

        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        Targets = targets
            .OrderBy(target => target.TargetKey.TenantKey, StringComparer.Ordinal)
            .ThenBy(target => target.TargetKey.DataStoreId)
            .ToImmutableArray();
    }

    [JsonPropertyName("contractVersion")]
    [JsonPropertyOrder(1)]
    public int ContractVersion => 1;

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(2)]
    public DateTimeOffset ObservedAt { get; }

    [JsonPropertyName("targets")]
    [JsonPropertyOrder(3)]
    public ImmutableArray<DocumentCacheStatusTarget> Targets { get; }
}

public sealed record DocumentCacheStatusTargetKey
{
    [JsonConstructor]
    public DocumentCacheStatusTargetKey(string? tenantKey, long dataStoreId)
    {
        TargetKey = DocumentCacheTargetKey.Create(tenantKey, dataStoreId);
    }

    [JsonIgnore]
    public DocumentCacheTargetKey TargetKey { get; }

    [JsonPropertyName("tenantKey")]
    [JsonPropertyOrder(1)]
    public string TenantKey => TargetKey.TenantKey;

    [JsonPropertyName("dataStoreId")]
    [JsonPropertyOrder(2)]
    public long DataStoreId => TargetKey.DataStoreId;

    public static DocumentCacheStatusTargetKey FromTargetKey(DocumentCacheTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        return new(targetKey.TenantKey, targetKey.DataStoreId);
    }
}

public sealed record DocumentCacheStatusTarget
{
    [JsonConstructor]
    public DocumentCacheStatusTarget(
        DocumentCacheStatusTargetKey targetKey,
        long? targetGeneration,
        DateTimeOffset processObservedAt,
        DateTimeOffset? durableObservedAt,
        string? provider,
        string? physicalSourceFingerprint,
        DocumentCacheStatusResolutionComponent resolution,
        DocumentCacheStatusEligibilityComponent eligibility,
        DocumentCacheStatusInventoryComponentGroup inventory,
        DocumentCacheStatusProviderPrerequisitesComponent providerPrerequisites,
        DocumentCacheStatusLifecycleComponent lifecycle,
        DocumentCacheStatusCacheAheadComponent cacheAhead,
        DocumentCacheOperationalHealthComponent operationalHealth,
        DocumentCacheCaughtUpComponent caughtUp,
        DocumentCacheStatusQueueSummary queueSummary,
        DocumentCacheStatusExecutionStateComponent executionState,
        DocumentCacheStatusActiveCommand? activeCommand,
        DocumentCacheStatusLastEndedDiagnostic? lastEndedDiagnostic,
        DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent> targetDiagnostics,
        DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent> documentDiagnostics,
        DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent> poisonTraversalDiagnostics,
        DocumentCacheStatusEffectiveSettings effectiveSettings,
        DocumentCacheStatusEnqueueFailures enqueueFailures
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(providerPrerequisites);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(cacheAhead);
        ArgumentNullException.ThrowIfNull(operationalHealth);
        ArgumentNullException.ThrowIfNull(caughtUp);
        ArgumentNullException.ThrowIfNull(queueSummary);
        ArgumentNullException.ThrowIfNull(executionState);
        ArgumentNullException.ThrowIfNull(targetDiagnostics);
        ArgumentNullException.ThrowIfNull(documentDiagnostics);
        ArgumentNullException.ThrowIfNull(poisonTraversalDiagnostics);
        ArgumentNullException.ThrowIfNull(effectiveSettings);
        ArgumentNullException.ThrowIfNull(enqueueFailures);

        if (targetGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetGeneration),
                "Target generation must be positive when supplied."
            );
        }

        TargetKey = targetKey;
        TargetGeneration = targetGeneration;
        ProcessObservedAt = DocumentCacheStatusTimestamp.ToUtc(processObservedAt);
        DurableObservedAt = DocumentCacheStatusTimestamp.ToUtc(durableObservedAt);
        Provider = string.IsNullOrWhiteSpace(provider) ? null : provider;
        PhysicalSourceFingerprint = string.IsNullOrWhiteSpace(physicalSourceFingerprint)
            ? null
            : physicalSourceFingerprint;
        Resolution = resolution;
        Eligibility = eligibility;
        Inventory = inventory;
        ProviderPrerequisites = providerPrerequisites;
        Lifecycle = lifecycle;
        CacheAhead = cacheAhead;
        OperationalHealth = operationalHealth;
        CaughtUp = caughtUp;
        QueueSummary = queueSummary;
        ExecutionState = executionState;
        ActiveCommand = activeCommand;
        LastEndedDiagnostic = lastEndedDiagnostic;
        TargetDiagnostics = targetDiagnostics;
        DocumentDiagnostics = documentDiagnostics;
        PoisonTraversalDiagnostics = poisonTraversalDiagnostics;
        EffectiveSettings = effectiveSettings;
        EnqueueFailures = enqueueFailures;
    }

    [JsonPropertyName("targetKey")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusTargetKey TargetKey { get; }

    [JsonPropertyName("targetGeneration")]
    [JsonPropertyOrder(2)]
    public long? TargetGeneration { get; }

    [JsonPropertyName("processObservedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(3)]
    public DateTimeOffset ProcessObservedAt { get; }

    [JsonPropertyName("durableObservedAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(4)]
    public DateTimeOffset? DurableObservedAt { get; }

    [JsonPropertyName("provider")]
    [JsonPropertyOrder(5)]
    public string? Provider { get; }

    [JsonPropertyName("physicalSourceFingerprint")]
    [JsonPropertyOrder(6)]
    public string? PhysicalSourceFingerprint { get; }

    [JsonPropertyName("resolution")]
    [JsonPropertyOrder(7)]
    public DocumentCacheStatusResolutionComponent Resolution { get; }

    [JsonPropertyName("eligibility")]
    [JsonPropertyOrder(8)]
    public DocumentCacheStatusEligibilityComponent Eligibility { get; }

    [JsonPropertyName("inventory")]
    [JsonPropertyOrder(9)]
    public DocumentCacheStatusInventoryComponentGroup Inventory { get; }

    [JsonPropertyName("providerPrerequisites")]
    [JsonPropertyOrder(10)]
    public DocumentCacheStatusProviderPrerequisitesComponent ProviderPrerequisites { get; }

    [JsonPropertyName("lifecycle")]
    [JsonPropertyOrder(11)]
    public DocumentCacheStatusLifecycleComponent Lifecycle { get; }

    [JsonPropertyName("cacheAhead")]
    [JsonPropertyOrder(12)]
    public DocumentCacheStatusCacheAheadComponent CacheAhead { get; }

    [JsonPropertyName("operationalHealth")]
    [JsonPropertyOrder(13)]
    public DocumentCacheOperationalHealthComponent OperationalHealth { get; }

    [JsonPropertyName("caughtUp")]
    [JsonPropertyOrder(14)]
    public DocumentCacheCaughtUpComponent CaughtUp { get; }

    [JsonPropertyName("queueSummary")]
    [JsonPropertyOrder(15)]
    public DocumentCacheStatusQueueSummary QueueSummary { get; }

    [JsonPropertyName("executionState")]
    [JsonPropertyOrder(16)]
    public DocumentCacheStatusExecutionStateComponent ExecutionState { get; }

    [JsonPropertyName("activeCommand")]
    [JsonPropertyOrder(17)]
    public DocumentCacheStatusActiveCommand? ActiveCommand { get; }

    [JsonPropertyName("lastEndedDiagnostic")]
    [JsonPropertyOrder(18)]
    public DocumentCacheStatusLastEndedDiagnostic? LastEndedDiagnostic { get; }

    [JsonPropertyName("targetDiagnostics")]
    [JsonPropertyOrder(19)]
    public DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent> TargetDiagnostics { get; }

    [JsonPropertyName("documentDiagnostics")]
    [JsonPropertyOrder(20)]
    public DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent> DocumentDiagnostics { get; }

    [JsonPropertyName("poisonTraversalDiagnostics")]
    [JsonPropertyOrder(21)]
    public DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent> PoisonTraversalDiagnostics { get; }

    [JsonPropertyName("effectiveSettings")]
    [JsonPropertyOrder(22)]
    public DocumentCacheStatusEffectiveSettings EffectiveSettings { get; }

    [JsonPropertyName("enqueueFailures")]
    [JsonPropertyOrder(23)]
    public DocumentCacheStatusEnqueueFailures EnqueueFailures { get; }
}

public sealed record DocumentCacheStatusResolutionComponent
{
    [JsonConstructor]
    public DocumentCacheStatusResolutionComponent(
        DocumentCacheStatusResolutionStatus status,
        DocumentCacheStatusResolutionReason reason,
        DateTimeOffset? observedAt,
        string? message
    )
    {
        Status = status;
        Reason = reason;
        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusResolutionStatus Status { get; }

    [JsonPropertyName("reason")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusResolutionReason Reason { get; }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(3)]
    public DateTimeOffset? ObservedAt { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(4)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusEligibilityComponent
{
    [JsonConstructor]
    public DocumentCacheStatusEligibilityComponent(
        DocumentCacheStatusEligibilityStatus status,
        DocumentCacheStatusReason reason,
        string? message
    )
    {
        Status = status;
        Reason = reason;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusEligibilityStatus Status { get; }

    [JsonPropertyName("reason")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusReason Reason { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(3)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusInventoryComponentGroup
{
    [JsonConstructor]
    public DocumentCacheStatusInventoryComponentGroup(
        DateTimeOffset? observedAt,
        DocumentCacheStatusInventoryComponent state,
        DocumentCacheStatusInventoryComponent work,
        DocumentCacheStatusInventoryComponent cache,
        DocumentCacheStatusInventoryComponent dataStoreIdentity,
        DocumentCacheStatusEnqueueTriggerComponent enqueueTrigger
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(dataStoreIdentity);
        ArgumentNullException.ThrowIfNull(enqueueTrigger);

        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        State = state;
        Work = work;
        Cache = cache;
        DataStoreIdentity = dataStoreIdentity;
        EnqueueTrigger = enqueueTrigger;
    }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(1)]
    public DateTimeOffset? ObservedAt { get; }

    [JsonPropertyName("state")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusInventoryComponent State { get; }

    [JsonPropertyName("work")]
    [JsonPropertyOrder(3)]
    public DocumentCacheStatusInventoryComponent Work { get; }

    [JsonPropertyName("cache")]
    [JsonPropertyOrder(4)]
    public DocumentCacheStatusInventoryComponent Cache { get; }

    [JsonPropertyName("dataStoreIdentity")]
    [JsonPropertyOrder(5)]
    public DocumentCacheStatusInventoryComponent DataStoreIdentity { get; }

    [JsonPropertyName("enqueueTrigger")]
    [JsonPropertyOrder(6)]
    public DocumentCacheStatusEnqueueTriggerComponent EnqueueTrigger { get; }
}

public sealed record DocumentCacheStatusInventoryComponent
{
    [JsonConstructor]
    public DocumentCacheStatusInventoryComponent(
        DocumentCacheStatusInventoryStatus status,
        DocumentCacheStatusInventoryReason reason,
        string? message
    )
    {
        Status = status;
        Reason = reason;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusInventoryStatus Status { get; }

    [JsonPropertyName("reason")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusInventoryReason Reason { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(3)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusEnqueueTriggerComponent
{
    [JsonConstructor]
    public DocumentCacheStatusEnqueueTriggerComponent(
        DocumentCacheStatusEnqueueTriggerStatus status,
        DocumentCacheStatusInventoryReason reason,
        string? message
    )
    {
        Status = status;
        Reason = reason;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusEnqueueTriggerStatus Status { get; }

    [JsonPropertyName("reason")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusInventoryReason Reason { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(3)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusProviderPrerequisitesComponent
{
    [JsonConstructor]
    public DocumentCacheStatusProviderPrerequisitesComponent(
        DocumentCacheStatusProviderPrerequisiteStatus status,
        DocumentCacheStatusProviderPrerequisiteReason reason,
        DateTimeOffset? observedAt,
        DocumentCacheStatusProviderPrerequisiteComponent sqlServerReadCommittedSnapshot,
        DocumentCacheStatusProviderPrerequisiteComponent sqlServerNestedTriggers
    )
    {
        ArgumentNullException.ThrowIfNull(sqlServerReadCommittedSnapshot);
        ArgumentNullException.ThrowIfNull(sqlServerNestedTriggers);

        Status = status;
        Reason = reason;
        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        SqlServerReadCommittedSnapshot = sqlServerReadCommittedSnapshot;
        SqlServerNestedTriggers = sqlServerNestedTriggers;
    }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusProviderPrerequisiteStatus Status { get; }

    [JsonPropertyName("reason")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusProviderPrerequisiteReason Reason { get; }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(3)]
    public DateTimeOffset? ObservedAt { get; }

    [JsonPropertyName("sqlServerReadCommittedSnapshot")]
    [JsonPropertyOrder(4)]
    public DocumentCacheStatusProviderPrerequisiteComponent SqlServerReadCommittedSnapshot { get; }

    [JsonPropertyName("sqlServerNestedTriggers")]
    [JsonPropertyOrder(5)]
    public DocumentCacheStatusProviderPrerequisiteComponent SqlServerNestedTriggers { get; }
}

public sealed record DocumentCacheStatusProviderPrerequisiteComponent
{
    [JsonConstructor]
    public DocumentCacheStatusProviderPrerequisiteComponent(
        DocumentCacheStatusProviderPrerequisiteStatus status,
        DocumentCacheStatusProviderPrerequisiteReason reason,
        string? message
    )
    {
        Status = status;
        Reason = reason;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusProviderPrerequisiteStatus Status { get; }

    [JsonPropertyName("reason")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusProviderPrerequisiteReason Reason { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(3)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusLifecycleComponent
{
    [JsonConstructor]
    public DocumentCacheStatusLifecycleComponent(
        DocumentCacheStatusLifecycleState state,
        DocumentCacheStatusAvailability availability,
        string? message
    )
    {
        State = state;
        Availability = availability;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("state")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusLifecycleState State { get; }

    [JsonPropertyName("availability")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusAvailability Availability { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(3)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusCacheAheadComponent
{
    [JsonConstructor]
    public DocumentCacheStatusCacheAheadComponent(
        DocumentCacheStatusCacheAheadState state,
        bool? recoveryRequired,
        string? message
    )
    {
        State = state;
        RecoveryRequired = recoveryRequired;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("state")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusCacheAheadState State { get; }

    [JsonPropertyName("recoveryRequired")]
    [JsonPropertyOrder(2)]
    public bool? RecoveryRequired { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(3)]
    public string? Message { get; }
}

public sealed record DocumentCacheOperationalHealthComponent
{
    [JsonConstructor]
    public DocumentCacheOperationalHealthComponent(
        DocumentCacheOperationalHealthStatus status,
        DocumentCacheStatusReason reason,
        string? message
    )
    {
        Status = status;
        Reason = reason;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(1)]
    public DocumentCacheOperationalHealthStatus Status { get; }

    [JsonPropertyName("reason")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusReason Reason { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(3)]
    public string? Message { get; }
}

public sealed record DocumentCacheCaughtUpComponent
{
    [JsonConstructor]
    public DocumentCacheCaughtUpComponent(
        DocumentCacheCaughtUpStatus status,
        DocumentCacheStatusReason reason,
        string? message
    )
    {
        Status = status;
        Reason = reason;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(1)]
    public DocumentCacheCaughtUpStatus Status { get; }

    [JsonPropertyName("reason")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusReason Reason { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(3)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusQueueSummary
{
    [JsonConstructor]
    public DocumentCacheStatusQueueSummary(
        DocumentCacheStatusQueuePresence presence,
        DateTimeOffset? oldestWorkFirstEnqueuedAt,
        double? oldestWorkAgeSeconds,
        DocumentCacheStatusBacklogEstimate backlogEstimate
    )
    {
        ArgumentNullException.ThrowIfNull(backlogEstimate);

        if (oldestWorkAgeSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(oldestWorkAgeSeconds),
                "Oldest work age must not be negative when supplied."
            );
        }

        Presence = presence;
        OldestWorkFirstEnqueuedAt = DocumentCacheStatusTimestamp.ToUtc(oldestWorkFirstEnqueuedAt);
        OldestWorkAgeSeconds = oldestWorkAgeSeconds;
        BacklogEstimate = backlogEstimate;
    }

    [JsonPropertyName("presence")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusQueuePresence Presence { get; }

    [JsonPropertyName("oldestWorkFirstEnqueuedAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(2)]
    public DateTimeOffset? OldestWorkFirstEnqueuedAt { get; }

    [JsonPropertyName("oldestWorkAgeSeconds")]
    [JsonPropertyOrder(3)]
    public double? OldestWorkAgeSeconds { get; }

    [JsonPropertyName("backlogEstimate")]
    [JsonPropertyOrder(4)]
    public DocumentCacheStatusBacklogEstimate BacklogEstimate { get; }
}

public sealed record DocumentCacheStatusBacklogEstimate
{
    [JsonConstructor]
    public DocumentCacheStatusBacklogEstimate(DocumentCacheStatusBacklogEstimateKind kind, long? value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Backlog estimate must not be negative when supplied."
            );
        }

        Kind = kind;
        Value = value;
    }

    public static DocumentCacheStatusBacklogEstimate Unavailable { get; } =
        new(DocumentCacheStatusBacklogEstimateKind.Unavailable, value: null);

    [JsonPropertyName("kind")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusBacklogEstimateKind Kind { get; }

    [JsonPropertyName("value")]
    [JsonPropertyOrder(2)]
    public long? Value { get; }
}

public sealed record DocumentCacheStatusExecutionStateComponent
{
    [JsonConstructor]
    public DocumentCacheStatusExecutionStateComponent(
        DocumentCacheStatusExecutionState status,
        DateTimeOffset? observedAt,
        int? activeWorkers,
        int? concurrencySlotsUsed,
        DateTimeOffset? targetBackoffUntil,
        DateTimeOffset? lastSuccessfulWorkAt,
        DateTimeOffset? lastFailureAt,
        string? message
    )
    {
        if (activeWorkers < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeWorkers),
                "Active worker count must not be negative when supplied."
            );
        }

        if (concurrencySlotsUsed < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(concurrencySlotsUsed),
                "Concurrency slots used must not be negative when supplied."
            );
        }

        Status = status;
        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        ActiveWorkers = activeWorkers;
        ConcurrencySlotsUsed = concurrencySlotsUsed;
        TargetBackoffUntil = DocumentCacheStatusTimestamp.ToUtc(targetBackoffUntil);
        LastSuccessfulWorkAt = DocumentCacheStatusTimestamp.ToUtc(lastSuccessfulWorkAt);
        LastFailureAt = DocumentCacheStatusTimestamp.ToUtc(lastFailureAt);
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusExecutionState Status { get; }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(2)]
    public DateTimeOffset? ObservedAt { get; }

    [JsonPropertyName("activeWorkers")]
    [JsonPropertyOrder(3)]
    public int? ActiveWorkers { get; }

    [JsonPropertyName("concurrencySlotsUsed")]
    [JsonPropertyOrder(4)]
    public int? ConcurrencySlotsUsed { get; }

    [JsonPropertyName("targetBackoffUntil")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(5)]
    public DateTimeOffset? TargetBackoffUntil { get; }

    [JsonPropertyName("lastSuccessfulWorkAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(6)]
    public DateTimeOffset? LastSuccessfulWorkAt { get; }

    [JsonPropertyName("lastFailureAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(7)]
    public DateTimeOffset? LastFailureAt { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(8)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusActiveCommand
{
    [JsonConstructor]
    public DocumentCacheStatusActiveCommand(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeCommandPhase phase,
        DocumentCacheStatusActiveCommandStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset observedAt,
        string? message,
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> phaseDiagnostics = default
    )
    {
        Command = command;
        Phase = phase;
        Status = status;
        StartedAt = DocumentCacheStatusTimestamp.ToUtc(startedAt);
        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        Message = DocumentCacheStatusText.SanitizeNullable(message);
        PhaseDiagnostics = phaseDiagnostics.IsDefault ? [] : phaseDiagnostics;
    }

    [JsonPropertyName("command")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeCommand Command { get; }

    [JsonPropertyName("phase")]
    [JsonPropertyOrder(2)]
    public DocumentCacheAdministrativeCommandPhase Phase { get; }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(3)]
    public DocumentCacheStatusActiveCommandStatus Status { get; }

    [JsonPropertyName("startedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(4)]
    public DateTimeOffset StartedAt { get; }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(5)]
    public DateTimeOffset ObservedAt { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(6)]
    public string? Message { get; }

    [JsonPropertyName("phaseDiagnostics")]
    [JsonPropertyOrder(7)]
    public ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> PhaseDiagnostics { get; }
}

public sealed record DocumentCacheStatusLastEndedDiagnostic
{
    [JsonConstructor]
    public DocumentCacheStatusLastEndedDiagnostic(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeCommandPhase phase,
        DocumentCacheStatusEndedCommandOutcome outcome,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        DateTimeOffset observedAt,
        string? message
    )
    {
        Command = command;
        Phase = phase;
        Outcome = outcome;
        StartedAt = DocumentCacheStatusTimestamp.ToUtc(startedAt);
        EndedAt = DocumentCacheStatusTimestamp.ToUtc(endedAt);
        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("command")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeCommand Command { get; }

    [JsonPropertyName("phase")]
    [JsonPropertyOrder(2)]
    public DocumentCacheAdministrativeCommandPhase Phase { get; }

    [JsonPropertyName("outcome")]
    [JsonPropertyOrder(3)]
    public DocumentCacheStatusEndedCommandOutcome Outcome { get; }

    [JsonPropertyName("startedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(4)]
    public DateTimeOffset StartedAt { get; }

    [JsonPropertyName("endedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(5)]
    public DateTimeOffset EndedAt { get; }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(6)]
    public DateTimeOffset ObservedAt { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(7)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusDiagnosticWindow<TEvent>
{
    [JsonConstructor]
    public DocumentCacheStatusDiagnosticWindow(
        ImmutableArray<TEvent> recentEvents = default,
        int evictedCount = 0
    )
    {
        if (evictedCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evictedCount),
                "Evicted count must not be negative."
            );
        }

        RecentEvents = recentEvents.IsDefault ? [] : recentEvents;
        EvictedCount = evictedCount;
    }

    [JsonPropertyName("recentEvents")]
    [JsonPropertyOrder(1)]
    public ImmutableArray<TEvent> RecentEvents { get; }

    [JsonPropertyName("evictedCount")]
    [JsonPropertyOrder(2)]
    public int EvictedCount { get; }
}

public sealed record DocumentCacheStatusTargetDiagnosticEvent
{
    [JsonConstructor]
    public DocumentCacheStatusTargetDiagnosticEvent(
        DateTimeOffset observedAt,
        DocumentCacheStatusTargetDiagnosticCategory category,
        string? message
    )
    {
        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        Category = category;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(1)]
    public DateTimeOffset ObservedAt { get; }

    [JsonPropertyName("category")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusTargetDiagnosticCategory Category { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(3)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusDocumentDiagnosticEvent
{
    [JsonConstructor]
    public DocumentCacheStatusDocumentDiagnosticEvent(
        long documentId,
        DateTimeOffset observedAt,
        DocumentCacheStatusDocumentDiagnosticCategory category,
        DateTimeOffset? nextRetryAt,
        string? message
    )
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentId), "Document id must be positive.");
        }

        DocumentId = documentId;
        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        Category = category;
        NextRetryAt = DocumentCacheStatusTimestamp.ToUtc(nextRetryAt);
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("documentId")]
    [JsonPropertyOrder(1)]
    public long DocumentId { get; }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(2)]
    public DateTimeOffset ObservedAt { get; }

    [JsonPropertyName("category")]
    [JsonPropertyOrder(3)]
    public DocumentCacheStatusDocumentDiagnosticCategory Category { get; }

    [JsonPropertyName("nextRetryAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(4)]
    public DateTimeOffset? NextRetryAt { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(5)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusPoisonTraversalDiagnosticEvent
{
    [JsonConstructor]
    public DocumentCacheStatusPoisonTraversalDiagnosticEvent(
        long documentId,
        DateTimeOffset observedAt,
        DocumentCacheStatusPoisonTraversalDiagnosticCategory category,
        DateTimeOffset? nextRetryAt,
        string? message
    )
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentId), "Document id must be positive.");
        }

        DocumentId = documentId;
        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        Category = category;
        NextRetryAt = DocumentCacheStatusTimestamp.ToUtc(nextRetryAt);
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("documentId")]
    [JsonPropertyOrder(1)]
    public long DocumentId { get; }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(2)]
    public DateTimeOffset ObservedAt { get; }

    [JsonPropertyName("category")]
    [JsonPropertyOrder(3)]
    public DocumentCacheStatusPoisonTraversalDiagnosticCategory Category { get; }

    [JsonPropertyName("nextRetryAt")]
    [JsonConverter(typeof(DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(4)]
    public DateTimeOffset? NextRetryAt { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(5)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusEffectiveSettings
{
    [JsonConstructor]
    public DocumentCacheStatusEffectiveSettings(
        DocumentCacheStatusProjectorEffectiveSettings projector,
        DocumentCacheStatusReadAccelerationEffectiveSettings readAcceleration,
        DocumentCacheStatusTimingEffectiveSettings status
    )
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(readAcceleration);
        ArgumentNullException.ThrowIfNull(status);

        Projector = projector;
        ReadAcceleration = readAcceleration;
        Status = status;
    }

    [JsonPropertyName("projector")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusProjectorEffectiveSettings Projector { get; }

    [JsonPropertyName("readAcceleration")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusReadAccelerationEffectiveSettings ReadAcceleration { get; }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(3)]
    public DocumentCacheStatusTimingEffectiveSettings Status { get; }

    public static DocumentCacheStatusEffectiveSettings FromEffectiveSettings(
        DocumentCacheTargetEffectiveSettings effectiveSettings
    )
    {
        ArgumentNullException.ThrowIfNull(effectiveSettings);

        return new(
            new DocumentCacheStatusProjectorEffectiveSettings(
                DocumentCacheStatusDuration.Seconds(effectiveSettings.ProjectorPollInterval),
                effectiveSettings.ProjectorPageSize,
                effectiveSettings.ProjectorMaxConcurrentTargets,
                DocumentCacheStatusDuration.Seconds(effectiveSettings.ProjectorFailureBackoff),
                effectiveSettings.ProjectorBaselineHighWaterMark
            ),
            new DocumentCacheStatusReadAccelerationEffectiveSettings(
                effectiveSettings.ReadAccelerationEnabled,
                DocumentCacheStatusDuration.Seconds(effectiveSettings.DirectFillTimeout)
            ),
            new DocumentCacheStatusTimingEffectiveSettings(
                DocumentCacheStatusDuration.Seconds(effectiveSettings.StatusObservationTimeout),
                DocumentCacheStatusDuration.Seconds(effectiveSettings.StatusEndpointTimeout)
            )
        );
    }
}

public sealed record DocumentCacheStatusProjectorEffectiveSettings
{
    [JsonConstructor]
    public DocumentCacheStatusProjectorEffectiveSettings(
        double pollIntervalSeconds,
        int pageSize,
        int maxConcurrentTargets,
        double failureBackoffSeconds,
        int baselineHighWaterMark
    )
    {
        if (pollIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollIntervalSeconds),
                "Poll interval must be positive."
            );
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be positive.");
        }

        if (maxConcurrentTargets <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentTargets),
                "Max concurrent targets must be positive."
            );
        }

        if (failureBackoffSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureBackoffSeconds),
                "Failure backoff must be positive."
            );
        }

        if (baselineHighWaterMark < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baselineHighWaterMark),
                "Baseline high-water mark must not be negative."
            );
        }

        PollIntervalSeconds = pollIntervalSeconds;
        PageSize = pageSize;
        MaxConcurrentTargets = maxConcurrentTargets;
        FailureBackoffSeconds = failureBackoffSeconds;
        BaselineHighWaterMark = baselineHighWaterMark;
    }

    [JsonPropertyName("pollIntervalSeconds")]
    [JsonPropertyOrder(1)]
    public double PollIntervalSeconds { get; }

    [JsonPropertyName("pageSize")]
    [JsonPropertyOrder(2)]
    public int PageSize { get; }

    [JsonPropertyName("maxConcurrentTargets")]
    [JsonPropertyOrder(3)]
    public int MaxConcurrentTargets { get; }

    [JsonPropertyName("failureBackoffSeconds")]
    [JsonPropertyOrder(4)]
    public double FailureBackoffSeconds { get; }

    [JsonPropertyName("baselineHighWaterMark")]
    [JsonPropertyOrder(5)]
    public int BaselineHighWaterMark { get; }
}

public sealed record DocumentCacheStatusReadAccelerationEffectiveSettings
{
    [JsonConstructor]
    public DocumentCacheStatusReadAccelerationEffectiveSettings(bool enabled, double directFillTimeoutSeconds)
    {
        if (directFillTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(directFillTimeoutSeconds),
                "Direct-fill timeout must be positive."
            );
        }

        Enabled = enabled;
        DirectFillTimeoutSeconds = directFillTimeoutSeconds;
    }

    [JsonPropertyName("enabled")]
    [JsonPropertyOrder(1)]
    public bool Enabled { get; }

    [JsonPropertyName("directFillTimeoutSeconds")]
    [JsonPropertyOrder(2)]
    public double DirectFillTimeoutSeconds { get; }
}

public sealed record DocumentCacheStatusTimingEffectiveSettings
{
    [JsonConstructor]
    public DocumentCacheStatusTimingEffectiveSettings(
        double statusObservationTimeoutSeconds,
        double endpointTimeoutSeconds
    )
    {
        if (statusObservationTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusObservationTimeoutSeconds),
                "Status-observation timeout must be positive."
            );
        }

        if (endpointTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endpointTimeoutSeconds),
                "Endpoint timeout must be positive."
            );
        }

        StatusObservationTimeoutSeconds = statusObservationTimeoutSeconds;
        EndpointTimeoutSeconds = endpointTimeoutSeconds;
    }

    [JsonPropertyName("statusObservationTimeoutSeconds")]
    [JsonPropertyOrder(1)]
    public double StatusObservationTimeoutSeconds { get; }

    [JsonPropertyName("endpointTimeoutSeconds")]
    [JsonPropertyOrder(2)]
    public double EndpointTimeoutSeconds { get; }
}

public sealed record DocumentCacheStatusEnqueueFailures
{
    [JsonConstructor]
    public DocumentCacheStatusEnqueueFailures(
        ImmutableArray<DocumentCacheStatusEnqueueFailureEvent> recentEvents = default,
        ImmutableArray<DocumentCacheStatusEnqueueFailureCategoryCount> byCategory = default,
        int evictedCount = 0
    )
    {
        if (evictedCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evictedCount),
                "Evicted count must not be negative."
            );
        }

        RecentEvents = recentEvents.IsDefault ? [] : recentEvents;
        ByCategory = (byCategory.IsDefault ? [] : byCategory)
            .Where(categoryCount => categoryCount.Count > 0)
            .OrderBy(categoryCount => categoryCount.Category)
            .ToImmutableArray();
        EvictedCount = evictedCount;
    }

    [JsonPropertyName("recentEvents")]
    [JsonPropertyOrder(1)]
    public ImmutableArray<DocumentCacheStatusEnqueueFailureEvent> RecentEvents { get; }

    [JsonPropertyName("byCategory")]
    [JsonPropertyOrder(2)]
    public ImmutableArray<DocumentCacheStatusEnqueueFailureCategoryCount> ByCategory { get; }

    [JsonPropertyName("evictedCount")]
    [JsonPropertyOrder(3)]
    public int EvictedCount { get; }
}

public sealed record DocumentCacheStatusEnqueueFailureEvent
{
    [JsonConstructor]
    public DocumentCacheStatusEnqueueFailureEvent(
        DateTimeOffset observedAt,
        DocumentCacheStatusEnqueueFailureCategory category,
        DocumentCacheStatusCanonicalOperation canonicalOperation,
        DocumentCacheStatusResourceKind resourceKind,
        string? message
    )
    {
        ObservedAt = DocumentCacheStatusTimestamp.ToUtc(observedAt);
        Category = category;
        CanonicalOperation = canonicalOperation;
        ResourceKind = resourceKind;
        Message = DocumentCacheStatusText.SanitizeNullable(message);
    }

    [JsonPropertyName("observedAt")]
    [JsonConverter(typeof(DocumentCacheStatusUtcDateTimeOffsetJsonConverter))]
    [JsonPropertyOrder(1)]
    public DateTimeOffset ObservedAt { get; }

    [JsonPropertyName("category")]
    [JsonPropertyOrder(2)]
    public DocumentCacheStatusEnqueueFailureCategory Category { get; }

    [JsonPropertyName("canonicalOperation")]
    [JsonPropertyOrder(3)]
    public DocumentCacheStatusCanonicalOperation CanonicalOperation { get; }

    [JsonPropertyName("resourceKind")]
    [JsonPropertyOrder(4)]
    public DocumentCacheStatusResourceKind ResourceKind { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(5)]
    public string? Message { get; }
}

public sealed record DocumentCacheStatusEnqueueFailureCategoryCount
{
    [JsonConstructor]
    public DocumentCacheStatusEnqueueFailureCategoryCount(
        DocumentCacheStatusEnqueueFailureCategory category,
        int count
    )
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Category count must be positive.");
        }

        Category = category;
        Count = count;
    }

    [JsonPropertyName("category")]
    [JsonPropertyOrder(1)]
    public DocumentCacheStatusEnqueueFailureCategory Category { get; }

    [JsonPropertyName("count")]
    [JsonPropertyOrder(2)]
    public int Count { get; }
}

public static class DocumentCacheStatusDuration
{
    public static double Seconds(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must not be negative.");
        }

        return duration.TotalSeconds;
    }

    public static double? Seconds(TimeSpan? duration) => duration is null ? null : Seconds(duration.Value);
}

public sealed class DocumentCacheStatusUtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Timestamp must be a string.");
        }

        string? value = reader.GetString();
        if (value is null)
        {
            throw new JsonException("Timestamp must be a string.");
        }

        return DocumentCacheStatusTimestamp.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        DocumentCacheStatusTimestamp.Write(writer, value);
}

public sealed class DocumentCacheStatusNullableUtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Timestamp must be a string or null.");
        }

        string? value = reader.GetString();
        if (value is null)
        {
            throw new JsonException("Timestamp must be a string or null.");
        }

        return DocumentCacheStatusTimestamp.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        DocumentCacheStatusTimestamp.Write(writer, value.Value);
    }
}

internal static class DocumentCacheStatusTimestamp
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const string WholeSecondUtcTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static DateTimeOffset ToUtc(DateTimeOffset value) => value.ToUniversalTime();

    public static DateTimeOffset? ToUtc(DateTimeOffset? value) => value is null ? null : ToUtc(value.Value);

    public static DateTimeOffset Parse(string value) =>
        DateTimeOffset
            .Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            .ToUniversalTime();

    public static void Write(Utf8JsonWriter writer, DateTimeOffset value)
    {
        DateTime utcDateTime = ToUtc(value).UtcDateTime;
        long fractionTicks = utcDateTime.Ticks % TicksPerSecond;

        if (fractionTicks == 0)
        {
            writer.WriteStringValue(
                utcDateTime.ToString(WholeSecondUtcTimestampFormat, CultureInfo.InvariantCulture)
            );
            return;
        }

        string fraction = fractionTicks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        if (fraction.Length < 3)
        {
            fraction = fraction.PadRight(3, '0');
        }

        writer.WriteStringValue(
            utcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "." + fraction + "Z"
        );
    }
}

file static class DocumentCacheStatusText
{
    public static string? SanitizeNullable(string? message) =>
        message is null ? null : DocumentCacheDiagnosticText.Sanitize(message);
}

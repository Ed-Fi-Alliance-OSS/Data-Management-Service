// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.DocumentCache;

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheAdministrativeCommand>))]
public enum DocumentCacheAdministrativeCommand
{
    GuardedNewEmptyActivation,
    OfflineActivation,
    OfflineDeactivation,
    OnlineCacheRebuild,
    ExplicitIntegrityScrub,
    InternalOnlyCacheAheadRecovery,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheAdministrativeCommandStatus>))]
public enum DocumentCacheAdministrativeCommandStatus
{
    Completed,
    RejectedNoMutation,
    FailedNoMutation,
    IncompleteRetryable,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheAdministrativeCommandClassification>))]
public enum DocumentCacheAdministrativeCommandClassification
{
    Succeeded,
    TargetNotConfigured,
    TargetUnresolved,
    TargetReplacedBeforeExecution,
    MissingOrInvalidInventory,
    ProviderIneligible,
    ProviderMetadataMissing,
    ProviderMetadataUnknown,
    ProviderMismatch,
    ConnectionInputMissing,
    ProviderPrerequisiteFailed,
    UnsupportedPrerequisiteIncident,
    LifecycleMismatch,
    ResettingRequiresExplicitOperatorRecovery,
    CacheAheadLatchSet,
    NonemptyGuardedActivationState,
    DownstreamHistoryPresentOrUnknown,
    ExpectedSourceMismatch,
    MissingOfflineWriterAdmission,
    UnconfirmedOfflineWriterAdmission,
    MismatchedOfflineWriterAdmission,
    MutexAcquisitionCancelled,
    MutexAcquisitionFailed,
    CancellationBeforeMutation,
    CancellationAfterMutation,
    SessionLossNoMutation,
    SessionLossAfterMutation,
    WorkflowTimeout,
    ProviderCommandTimeout,
    WriterRetryBudgetExhausted,
    PersistentPoison,
    UnexpectedProviderFailure,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheAdministrativeCommandPhase>))]
public enum DocumentCacheAdministrativeCommandPhase
{
    ResolveTarget,
    AcquireMutex,
    Preflight,
    EnterResetting,
    ClearCache,
    ClearWork,
    EnterRebuilding,
    CaptureBoundary,
    SeedBaseline,
    DrainWork,
    EnterTracking,
    EnterDisabled,
    ScrubScan,
    SetCacheAheadLatch,
    Complete,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheAdministrativeDiagnosticCategory>))]
public enum DocumentCacheAdministrativeDiagnosticCategory
{
    TargetNotConfigured,
    TargetUnresolved,
    ProviderMetadataMissing,
    ProviderMetadataUnknown,
    ProviderMismatch,
    ConnectionInputMissing,
    PhysicalSourceFingerprintFailure,
    InventoryFailure,
    EnqueueTriggerFailure,
    ProviderPrerequisiteFailed,
    UnsupportedPrerequisiteIncident,
    LifecycleObservationFailure,
    TransientCmsRefreshFailure,
    TargetReplaced,
    LifecycleMismatch,
    ResettingRequiresExplicitOperatorRecovery,
    CacheAheadLatchSet,
    NonemptyGuardedActivationState,
    DownstreamPublicationHistoryPresentOrUnknown,
    EffectiveSchemaCompatibilityFailure,
    ResourceKeyCompatibilityFailure,
    ExpectedSourceMismatch,
    ProviderIneligible,
    MissingOfflineWriterAdmission,
    UnconfirmedOfflineWriterAdmission,
    MismatchedOfflineWriterAdmission,
    MutexAcquisitionCancelled,
    MutexAcquisitionFailed,
    Cancellation,
    WorkflowTimeout,
    ProviderCommandTimeout,
    WriterRetryBudgetExhausted,
    SessionLoss,
    PersistentPoison,
    UnexpectedProviderFailure,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheOfflineWriterAdmissionConfirmation>))]
public enum DocumentCacheOfflineWriterAdmissionConfirmation
{
    OfflineActivationWritersClosedAndDrained,
    OfflineDeactivationWritersClosedAndDrained,
    InternalOnlyCacheAheadRecoveryWritersClosedAndDrained,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheDownstreamPublicationStatus>))]
public enum DocumentCacheDownstreamPublicationStatus
{
    InternalOnly,
    Active,
    Historical,
    Possible,
    Unknown,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheAdministrativeNoMutationScope>))]
public enum DocumentCacheAdministrativeNoMutationScope
{
    LifecycleCacheWorkLatchAndProviderSettings,
}

public sealed record DocumentCacheAdministrativeTargetKey
{
    [JsonConstructor]
    public DocumentCacheAdministrativeTargetKey(string? tenantKey, long dataStoreId)
    {
        TargetKey = DocumentCacheTargetKey.Create(tenantKey, dataStoreId);
    }

    [JsonIgnore]
    public DocumentCacheTargetKey TargetKey { get; }

    [JsonPropertyName("tenantKey")]
    public string TenantKey => TargetKey.TenantKey;

    [JsonPropertyName("dataStoreId")]
    public long DataStoreId => TargetKey.DataStoreId;

    public static DocumentCacheAdministrativeTargetKey FromTargetKey(DocumentCacheTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        return new(targetKey.TenantKey, targetKey.DataStoreId);
    }
}

public sealed record DocumentCacheOfflineWriterAdmission
{
    [JsonConstructor]
    public DocumentCacheOfflineWriterAdmission(
        bool confirmed,
        DocumentCacheOfflineWriterAdmissionConfirmation? confirmation
    )
    {
        Confirmed = confirmed;
        Confirmation = confirmation;
    }

    [JsonPropertyName("confirmed")]
    public bool Confirmed { get; }

    [JsonPropertyName("confirmation")]
    public DocumentCacheOfflineWriterAdmissionConfirmation? Confirmation { get; }
}

public sealed record DocumentCacheGuardedNewEmptyActivationRequest
{
    [JsonConstructor]
    public DocumentCacheGuardedNewEmptyActivationRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
    }

    [JsonPropertyName("targetKey")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    [JsonPropertyOrder(2)]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }
}

public sealed record DocumentCacheOfflineActivationRequest
{
    [JsonConstructor]
    public DocumentCacheOfflineActivationRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
        OfflineWriterAdmission = DocumentCacheOfflineWriterAdmissionGuard.Require(
            DocumentCacheAdministrativeCommand.OfflineActivation,
            offlineWriterAdmission
        );
    }

    [JsonPropertyName("targetKey")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    [JsonPropertyOrder(2)]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }

    [JsonPropertyName("offlineWriterAdmission")]
    [JsonPropertyOrder(3)]
    public DocumentCacheOfflineWriterAdmission OfflineWriterAdmission { get; }
}

public sealed record DocumentCacheOfflineDeactivationRequest
{
    [JsonConstructor]
    public DocumentCacheOfflineDeactivationRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
        OfflineWriterAdmission = DocumentCacheOfflineWriterAdmissionGuard.Require(
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            offlineWriterAdmission
        );
    }

    [JsonPropertyName("targetKey")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    [JsonPropertyOrder(2)]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }

    [JsonPropertyName("offlineWriterAdmission")]
    [JsonPropertyOrder(3)]
    public DocumentCacheOfflineWriterAdmission OfflineWriterAdmission { get; }
}

public sealed record DocumentCacheOnlineCacheRebuildRequest
{
    [JsonConstructor]
    public DocumentCacheOnlineCacheRebuildRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
    }

    [JsonPropertyName("targetKey")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    [JsonPropertyOrder(2)]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }
}

public sealed record DocumentCacheExplicitIntegrityScrubRequest
{
    [JsonConstructor]
    public DocumentCacheExplicitIntegrityScrubRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
    }

    [JsonPropertyName("targetKey")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    [JsonPropertyOrder(2)]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }
}

public sealed record DocumentCacheInternalOnlyCacheAheadRecoveryRequest
{
    [JsonConstructor]
    public DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
        OfflineWriterAdmission = DocumentCacheOfflineWriterAdmissionGuard.Require(
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            offlineWriterAdmission
        );
    }

    [JsonPropertyName("targetKey")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    [JsonPropertyOrder(2)]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }

    [JsonPropertyName("offlineWriterAdmission")]
    [JsonPropertyOrder(3)]
    public DocumentCacheOfflineWriterAdmission OfflineWriterAdmission { get; }
}

file static class DocumentCacheOfflineWriterAdmissionGuard
{
    public static DocumentCacheOfflineWriterAdmission Require(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission
    )
    {
        if (offlineWriterAdmission is null)
        {
            throw new ArgumentException(
                $"The {command} command requires offline writer admission.",
                nameof(offlineWriterAdmission)
            );
        }

        if (!offlineWriterAdmission.Confirmed)
        {
            throw new ArgumentException(
                "Offline writer admission must have confirmed true.",
                nameof(offlineWriterAdmission)
            );
        }

        DocumentCacheOfflineWriterAdmissionConfirmation expectedConfirmation = ExpectedConfirmation(command);
        if (offlineWriterAdmission.Confirmation != expectedConfirmation)
        {
            throw new ArgumentException(
                $"Offline writer admission confirmation must be {expectedConfirmation}.",
                nameof(offlineWriterAdmission)
            );
        }

        return offlineWriterAdmission;
    }

    private static DocumentCacheOfflineWriterAdmissionConfirmation ExpectedConfirmation(
        DocumentCacheAdministrativeCommand command
    ) =>
        command switch
        {
            DocumentCacheAdministrativeCommand.OfflineActivation =>
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained,
            DocumentCacheAdministrativeCommand.OfflineDeactivation =>
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained,
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery =>
                DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained,
            _ => throw new ArgumentException(
                "The command does not require offline writer admission.",
                nameof(command)
            ),
        };
}

public sealed record DocumentCacheAdministrativeCommandResult
{
    [JsonConstructor]
    public DocumentCacheAdministrativeCommandResult(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        bool mutated,
        long? targetGeneration = null,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint = null,
        DocumentCacheLifecycleState? lifecycle = null,
        bool? cacheAheadRecoveryRequired = null,
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> phaseDiagnostics = default,
        DocumentCacheOfflineWriterAdmissionConfirmation? offlineWriterAdmission = null,
        TimeSpan? elapsedCommandTime = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        if (targetGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetGeneration),
                "Target generation must be positive when supplied."
            );
        }

        if (elapsedCommandTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsedCommandTime),
                "Elapsed command time must not be negative when supplied."
            );
        }

        Command = command;
        TargetKey = targetKey;
        Status = status;
        Classification = classification;
        Mutated = mutated;
        TargetGeneration = targetGeneration;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        Lifecycle = lifecycle;
        CacheAheadRecoveryRequired = cacheAheadRecoveryRequired;
        PhaseDiagnostics = phaseDiagnostics.IsDefault ? [] : phaseDiagnostics;
        OfflineWriterAdmission = offlineWriterAdmission;
        ElapsedCommandTime = elapsedCommandTime;
        DownstreamPublicationStatus = null;
    }

    public DocumentCacheAdministrativeCommandResult(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheLifecycleState? lifecycle = null,
        bool? cacheAheadRecoveryRequired = null,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint = null,
        long? targetGeneration = null,
        DocumentCacheDownstreamPublicationStatus? downstreamPublicationStatus = null,
        ImmutableArray<DocumentCacheAdministrativeDiagnostic> diagnostics = default,
        DocumentCacheAdministrativeNoMutationGuarantee? noMutationGuarantee = null
    )
        : this(
            command,
            targetKey,
            classification == DocumentCacheAdministrativeCommandClassification.Succeeded
                ? DocumentCacheAdministrativeCommandStatus.Completed
                : DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
            classification,
            mutated: false,
            targetGeneration,
            physicalSourceFingerprint,
            lifecycle,
            cacheAheadRecoveryRequired,
            ToPhaseDiagnostics(classification, diagnostics),
            offlineWriterAdmission: null,
            elapsedCommandTime: null
        )
    {
        DownstreamPublicationStatus = downstreamPublicationStatus;
    }

    [JsonPropertyName("command")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeCommand Command { get; }

    [JsonPropertyName("targetKey")]
    [JsonPropertyOrder(2)]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(3)]
    public DocumentCacheAdministrativeCommandStatus Status { get; }

    [JsonPropertyName("classification")]
    [JsonPropertyOrder(4)]
    public DocumentCacheAdministrativeCommandClassification Classification { get; }

    [JsonPropertyName("mutated")]
    [JsonPropertyOrder(5)]
    public bool Mutated { get; }

    [JsonPropertyName("targetGeneration")]
    [JsonPropertyOrder(6)]
    public long? TargetGeneration { get; }

    [JsonPropertyName("physicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    [JsonPropertyOrder(7)]
    public DocumentCachePhysicalSourceFingerprint? PhysicalSourceFingerprint { get; }

    [JsonPropertyName("lifecycle")]
    [JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheLifecycleState>))]
    [JsonPropertyOrder(8)]
    public DocumentCacheLifecycleState? Lifecycle { get; }

    [JsonPropertyName("cacheAheadRecoveryRequired")]
    [JsonPropertyOrder(9)]
    public bool? CacheAheadRecoveryRequired { get; }

    [JsonPropertyName("phaseDiagnostics")]
    [JsonPropertyOrder(10)]
    public ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> PhaseDiagnostics { get; }

    [JsonPropertyName("offlineWriterAdmission")]
    [JsonPropertyOrder(11)]
    public DocumentCacheOfflineWriterAdmissionConfirmation? OfflineWriterAdmission { get; }

    [JsonPropertyName("elapsedCommandTime")]
    [JsonPropertyOrder(12)]
    public TimeSpan? ElapsedCommandTime { get; }

    [JsonIgnore]
    public DocumentCacheLifecycleState? ObservedLifecycle => Lifecycle;

    [JsonIgnore]
    public long? TargetContextGeneration => TargetGeneration;

    [JsonIgnore]
    public DocumentCacheDownstreamPublicationStatus? DownstreamPublicationStatus { get; }

    [JsonIgnore]
    public ImmutableArray<DocumentCacheAdministrativeDiagnostic> Diagnostics =>
        PhaseDiagnostics
            .Select(diagnostic => new DocumentCacheAdministrativeDiagnostic(
                diagnostic.DiagnosticCategory,
                diagnostic.Message
            ))
            .ToImmutableArray();

    [JsonIgnore]
    public DocumentCacheAdministrativeNoMutationGuarantee? NoMutationGuarantee =>
        !Mutated
        && Status
            is DocumentCacheAdministrativeCommandStatus.RejectedNoMutation
                or DocumentCacheAdministrativeCommandStatus.FailedNoMutation
            ? new DocumentCacheAdministrativeNoMutationGuarantee(
                guaranteed: true,
                DocumentCacheAdministrativeNoMutationScope.LifecycleCacheWorkLatchAndProviderSettings,
                "Command result performed no lifecycle, cache, work, latch, or provider-setting mutation."
            )
            : null;

    private static ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> ToPhaseDiagnostics(
        DocumentCacheAdministrativeCommandClassification classification,
        ImmutableArray<DocumentCacheAdministrativeDiagnostic> diagnostics
    )
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return [];
        }

        DocumentCacheAdministrativeCommandPhase currentPhase = SelectDiagnosticPhase(classification);
        DocumentCacheAdministrativeCommandPhase? lastCompletedPhase =
            currentPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                ? DocumentCacheAdministrativeCommandPhase.ResolveTarget
                : null;

        return diagnostics
            .Select(diagnostic => new DocumentCacheAdministrativePhaseDiagnostic(
                currentPhase,
                lastCompletedPhase,
                retryable: false,
                diagnostic.Category,
                affectedDocumentIds: [],
                diagnostic.Message
            ))
            .ToImmutableArray();
    }

    private static DocumentCacheAdministrativeCommandPhase SelectDiagnosticPhase(
        DocumentCacheAdministrativeCommandClassification classification
    ) =>
        classification switch
        {
            DocumentCacheAdministrativeCommandClassification.TargetNotConfigured
            or DocumentCacheAdministrativeCommandClassification.TargetUnresolved
            or DocumentCacheAdministrativeCommandClassification.TargetReplacedBeforeExecution
            or DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch
            or DocumentCacheAdministrativeCommandClassification.MissingOrInvalidInventory
            or DocumentCacheAdministrativeCommandClassification.ProviderIneligible
            or DocumentCacheAdministrativeCommandClassification.ProviderMetadataMissing
            or DocumentCacheAdministrativeCommandClassification.ProviderMetadataUnknown
            or DocumentCacheAdministrativeCommandClassification.ProviderMismatch
            or DocumentCacheAdministrativeCommandClassification.ConnectionInputMissing =>
                DocumentCacheAdministrativeCommandPhase.ResolveTarget,
            DocumentCacheAdministrativeCommandClassification.MutexAcquisitionCancelled
            or DocumentCacheAdministrativeCommandClassification.MutexAcquisitionFailed =>
                DocumentCacheAdministrativeCommandPhase.AcquireMutex,
            _ => DocumentCacheAdministrativeCommandPhase.Preflight,
        };
}

public sealed record DocumentCacheAdministrativePhaseDiagnostic
{
    [JsonConstructor]
    public DocumentCacheAdministrativePhaseDiagnostic(
        DocumentCacheAdministrativeCommandPhase currentPhase,
        DocumentCacheAdministrativeCommandPhase? lastCompletedPhase,
        bool retryable,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        ImmutableArray<long> affectedDocumentIds = default,
        string? message = null
    )
    {
        CurrentPhase = currentPhase;
        LastCompletedPhase = lastCompletedPhase;
        Retryable = retryable;
        DiagnosticCategory = diagnosticCategory;
        AffectedDocumentIds = ValidateAffectedDocumentIds(affectedDocumentIds);
        Message = DocumentCacheDiagnosticText.Sanitize(message ?? diagnosticCategory.ToString());
    }

    [JsonPropertyName("currentPhase")]
    [JsonPropertyOrder(1)]
    public DocumentCacheAdministrativeCommandPhase CurrentPhase { get; }

    [JsonPropertyName("lastCompletedPhase")]
    [JsonPropertyOrder(2)]
    public DocumentCacheAdministrativeCommandPhase? LastCompletedPhase { get; }

    [JsonPropertyName("retryable")]
    [JsonPropertyOrder(3)]
    public bool Retryable { get; }

    [JsonPropertyName("diagnosticCategory")]
    [JsonPropertyOrder(4)]
    public DocumentCacheAdministrativeDiagnosticCategory DiagnosticCategory { get; }

    [JsonPropertyName("affectedDocumentIds")]
    [JsonPropertyOrder(5)]
    public ImmutableArray<long> AffectedDocumentIds { get; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(6)]
    public string Message { get; }

    private static ImmutableArray<long> ValidateAffectedDocumentIds(ImmutableArray<long> affectedDocumentIds)
    {
        if (affectedDocumentIds.IsDefault)
        {
            return [];
        }

        if (affectedDocumentIds.Any(documentId => documentId <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(affectedDocumentIds),
                "Affected document ids must be positive."
            );
        }

        return affectedDocumentIds;
    }
}

public sealed record DocumentCacheAdministrativeDiagnostic
{
    [JsonConstructor]
    public DocumentCacheAdministrativeDiagnostic(
        DocumentCacheAdministrativeDiagnosticCategory category,
        string message
    )
    {
        Category = category;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public DocumentCacheAdministrativeDiagnostic(
        DocumentCacheTargetDiagnosticCategory category,
        string message
    )
        : this(DocumentCacheAdministrativeDiagnosticCategoryMapper.FromTargetCategory(category), message) { }

    [JsonPropertyName("category")]
    public DocumentCacheAdministrativeDiagnosticCategory Category { get; }

    [JsonPropertyName("message")]
    public string Message { get; }
}

file static class DocumentCacheAdministrativeDiagnosticCategoryMapper
{
    public static DocumentCacheAdministrativeDiagnosticCategory FromTargetCategory(
        DocumentCacheTargetDiagnosticCategory category
    ) =>
        category switch
        {
            DocumentCacheTargetDiagnosticCategory.TargetNotConfigured =>
                DocumentCacheAdministrativeDiagnosticCategory.TargetNotConfigured,
            DocumentCacheTargetDiagnosticCategory.TargetUnresolved =>
                DocumentCacheAdministrativeDiagnosticCategory.TargetUnresolved,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing =>
                DocumentCacheAdministrativeDiagnosticCategory.ProviderMetadataMissing,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown =>
                DocumentCacheAdministrativeDiagnosticCategory.ProviderMetadataUnknown,
            DocumentCacheTargetDiagnosticCategory.ProviderMismatch =>
                DocumentCacheAdministrativeDiagnosticCategory.ProviderMismatch,
            DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing =>
                DocumentCacheAdministrativeDiagnosticCategory.ConnectionInputMissing,
            DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure =>
                DocumentCacheAdministrativeDiagnosticCategory.PhysicalSourceFingerprintFailure,
            DocumentCacheTargetDiagnosticCategory.InventoryFailure =>
                DocumentCacheAdministrativeDiagnosticCategory.InventoryFailure,
            DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure =>
                DocumentCacheAdministrativeDiagnosticCategory.EnqueueTriggerFailure,
            DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed =>
                DocumentCacheAdministrativeDiagnosticCategory.ProviderPrerequisiteFailed,
            DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident =>
                DocumentCacheAdministrativeDiagnosticCategory.UnsupportedPrerequisiteIncident,
            DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure =>
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleObservationFailure,
            DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure =>
                DocumentCacheAdministrativeDiagnosticCategory.TransientCmsRefreshFailure,
            DocumentCacheTargetDiagnosticCategory.TargetReplaced =>
                DocumentCacheAdministrativeDiagnosticCategory.TargetReplaced,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch =>
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery =>
                DocumentCacheAdministrativeDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery,
            DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet =>
                DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet,
            DocumentCacheTargetDiagnosticCategory.NonemptyGuardedActivationState =>
                DocumentCacheAdministrativeDiagnosticCategory.NonemptyGuardedActivationState,
            DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown =>
                DocumentCacheAdministrativeDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown,
            DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure =>
                DocumentCacheAdministrativeDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
            DocumentCacheTargetDiagnosticCategory.ResourceKeyCompatibilityFailure =>
                DocumentCacheAdministrativeDiagnosticCategory.ResourceKeyCompatibilityFailure,
            DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch =>
                DocumentCacheAdministrativeDiagnosticCategory.ExpectedSourceMismatch,
            DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure =>
                DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
        };
}

public sealed record DocumentCacheAdministrativeNoMutationGuarantee
{
    [JsonConstructor]
    public DocumentCacheAdministrativeNoMutationGuarantee(
        bool guaranteed,
        DocumentCacheAdministrativeNoMutationScope scope,
        string message
    )
    {
        Guaranteed = guaranteed;
        Scope = scope;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    [JsonPropertyName("guaranteed")]
    public bool Guaranteed { get; }

    [JsonPropertyName("scope")]
    public DocumentCacheAdministrativeNoMutationScope Scope { get; }

    [JsonPropertyName("message")]
    public string Message { get; }
}

public sealed class DocumentCachePhysicalSourceFingerprintJsonConverter
    : JsonConverter<DocumentCachePhysicalSourceFingerprint>
{
    public override DocumentCachePhysicalSourceFingerprint Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Physical-source fingerprint must be a string.");
        }

        string? value = reader.GetString();
        if (value is null)
        {
            throw new JsonException("Physical-source fingerprint must be a string.");
        }

        try
        {
            return new DocumentCachePhysicalSourceFingerprint(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "Physical-source fingerprint must be `sha256:` followed by 64 lowercase hexadecimal characters.",
                exception
            );
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        DocumentCachePhysicalSourceFingerprint value,
        JsonSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStringValue(value.Value);
    }
}

public sealed class LowerCamelJsonStringEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public LowerCamelJsonStringEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

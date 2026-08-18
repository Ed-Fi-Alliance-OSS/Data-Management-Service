// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.DocumentCache;

public enum DocumentCacheTargetResolutionState
{
    Configured,
    Unresolved,
    Resolved,
}

public enum DocumentCacheTargetEligibilityState
{
    NotEvaluated,
    Eligible,
    Ineligible,
}

public enum DocumentCacheLifecycleState
{
    Disabled,
    Resetting,
    Rebuilding,
    Tracking,
}

public enum DocumentCacheInventoryStatus
{
    NotEvaluated,
    Satisfied,
    Missing,
    Invalid,
    Unreadable,
}

public enum DocumentCacheEnqueueTriggerStatus
{
    NotEvaluated,
    Satisfied,
    Missing,
    Disabled,
    Invalid,
    Unreadable,
}

public enum DocumentCacheProviderPrerequisiteStatus
{
    Satisfied,
    Disabled,
    Unreadable,
    NotApplicable,
}

public enum DocumentCacheProviderPrerequisiteName
{
    ReadCommittedSnapshot,
    NestedTriggers,
}

public enum DocumentCacheTargetDiagnosticCategory
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
    DeterministicInvariantFailure,
    UnexpectedProviderFailure,
}

public sealed record DocumentCacheProcessProviderToken
{
    public DocumentCacheProcessProviderToken(RelationalProviderToken providerToken)
    {
        ArgumentNullException.ThrowIfNull(providerToken);

        ProviderToken = providerToken;
    }

    public RelationalProviderToken ProviderToken { get; }

    public static bool TryCreate(
        string? datastore,
        out DocumentCacheProcessProviderToken? processProviderToken
    )
    {
        processProviderToken = null;

        if (string.Equals(datastore, "mssql", StringComparison.OrdinalIgnoreCase))
        {
            processProviderToken = new DocumentCacheProcessProviderToken(RelationalProviderToken.SqlServer);
            return true;
        }

        if (RelationalProviderToken.TryNormalize(datastore, out RelationalProviderToken? providerToken))
        {
            processProviderToken = new DocumentCacheProcessProviderToken(providerToken);
            return true;
        }

        return false;
    }
}

public sealed record DocumentCacheTargetContextGeneration
{
    public DocumentCacheTargetContextGeneration(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Generation must be positive.");
        }

        Value = value;
    }

    public long Value { get; }

    public override string ToString() => Value.ToString();
}

public sealed record DocumentCachePhysicalSourceFingerprint
{
    private const string Prefix = "sha256:";
    private const int PrefixLength = 7;
    private const int HexDigestLength = 64;
    private const int ExpectedLength = PrefixLength + HexDigestLength;

    public DocumentCachePhysicalSourceFingerprint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!IsCanonical(value))
        {
            throw new ArgumentException(
                "Fingerprint must use the canonical sha256 lowercase hexadecimal format.",
                nameof(value)
            );
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static bool IsCanonical(string value)
    {
        if (value.Length != ExpectedLength)
        {
            return false;
        }

        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (int index = PrefixLength; index < value.Length; index++)
        {
            char character = value[index];
            if (!IsLowercaseHex(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowercaseHex(char character) =>
        (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f');
}

public sealed record DocumentCacheTargetEffectiveSettings
{
    public DocumentCacheTargetEffectiveSettings(
        bool readAccelerationEnabled,
        TimeSpan directFillTimeout,
        TimeSpan projectorPollInterval,
        int projectorPageSize,
        int projectorMaxConcurrentTargets,
        TimeSpan projectorFailureBackoff,
        int projectorBaselineHighWaterMark,
        TimeSpan administrationWorkflowTimeout,
        TimeSpan? statusObservationTimeout = null,
        TimeSpan? statusEndpointTimeout = null
    )
    {
        ReadAccelerationEnabled = readAccelerationEnabled;
        DirectFillTimeout = directFillTimeout;
        ProjectorPollInterval = projectorPollInterval;
        ProjectorPageSize = projectorPageSize;
        ProjectorMaxConcurrentTargets = projectorMaxConcurrentTargets;
        ProjectorFailureBackoff = projectorFailureBackoff;
        ProjectorBaselineHighWaterMark = projectorBaselineHighWaterMark;
        AdministrationWorkflowTimeout = administrationWorkflowTimeout;
        StatusObservationTimeout =
            statusObservationTimeout ?? DocumentCacheStatusOptions.DefaultStatusObservationTimeout;
        StatusEndpointTimeout = statusEndpointTimeout ?? DocumentCacheStatusOptions.DefaultEndpointTimeout;
    }

    public bool ReadAccelerationEnabled { get; }

    public TimeSpan DirectFillTimeout { get; }

    public TimeSpan ProjectorPollInterval { get; }

    public int ProjectorPageSize { get; }

    public int ProjectorMaxConcurrentTargets { get; }

    public TimeSpan ProjectorFailureBackoff { get; }

    public int ProjectorBaselineHighWaterMark { get; }

    public TimeSpan AdministrationWorkflowTimeout { get; }

    public TimeSpan StatusObservationTimeout { get; }

    public TimeSpan StatusEndpointTimeout { get; }

    public static DocumentCacheTargetEffectiveSettings FromOptions(DocumentCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new DocumentCacheTargetEffectiveSettings(
            options.ReadAcceleration.Enabled,
            options.ReadAcceleration.DirectFillTimeout,
            options.Projector.PollInterval,
            options.Projector.PageSize,
            options.Projector.MaxConcurrentTargets,
            options.Projector.FailureBackoff,
            options.Projector.BaselineHighWaterMark,
            options.Administration.WorkflowTimeout,
            options.Status.StatusObservationTimeout,
            options.Status.EndpointTimeout
        );
    }
}

public sealed record DocumentCacheLifecycleObservation(
    DocumentCacheLifecycleState State,
    bool CacheAheadRecoveryRequired
);

public enum DocumentCacheLifecycleReadStatus
{
    Succeeded,
    Missing,
    Invalid,
    Unreadable,
}

public sealed record DocumentCacheLifecycleReadResult
{
    private DocumentCacheLifecycleReadResult(
        DocumentCacheLifecycleReadStatus status,
        DocumentCacheLifecycleObservation? lifecycle,
        string message
    )
    {
        if (status == DocumentCacheLifecycleReadStatus.Succeeded && lifecycle is null)
        {
            throw new ArgumentException("Successful lifecycle reads require an observation.");
        }

        if (status != DocumentCacheLifecycleReadStatus.Succeeded && lifecycle is not null)
        {
            throw new ArgumentException("Failed lifecycle reads must not carry an observation.");
        }

        Status = status;
        Lifecycle = lifecycle;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public DocumentCacheLifecycleReadStatus Status { get; }

    public DocumentCacheLifecycleObservation? Lifecycle { get; }

    public string Message { get; }

    public bool Succeeded => Status == DocumentCacheLifecycleReadStatus.Succeeded;

    public static DocumentCacheLifecycleReadResult Success(DocumentCacheLifecycleObservation lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        return new(
            DocumentCacheLifecycleReadStatus.Succeeded,
            lifecycle,
            "DocumentCache lifecycle observed."
        );
    }

    public static DocumentCacheLifecycleReadResult Failure(
        DocumentCacheLifecycleReadStatus status,
        string message
    )
    {
        if (status == DocumentCacheLifecycleReadStatus.Succeeded)
        {
            throw new ArgumentException("Use Success for successful lifecycle reads.", nameof(status));
        }

        return new(status, lifecycle: null, message);
    }
}

public interface IDocumentCacheLifecycleReader
{
    RelationalProviderToken ProviderToken { get; }

    Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    );
}

public sealed record DocumentCacheInventoryValidationResult
{
    public DocumentCacheInventoryValidationResult(DocumentCacheInventoryStatus status, string message)
    {
        Status = status;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public DocumentCacheInventoryStatus Status { get; }

    public string Message { get; }
}

public sealed record DocumentCacheInventoryValidationComponents(
    DocumentCacheInventoryValidationResult State,
    DocumentCacheInventoryValidationResult Work,
    DocumentCacheInventoryValidationResult Cache,
    DocumentCacheInventoryValidationResult DataStoreIdentity
)
{
    public static DocumentCacheInventoryValidationComponents FromAggregate(
        DocumentCacheInventoryValidationResult inventory
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return new(inventory, inventory, inventory, inventory);
    }
}

public sealed record DocumentCacheEnqueueTriggerValidationResult
{
    public DocumentCacheEnqueueTriggerValidationResult(
        DocumentCacheEnqueueTriggerStatus status,
        string message
    )
    {
        Status = status;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public DocumentCacheEnqueueTriggerStatus Status { get; }

    public string Message { get; }
}

public sealed record DocumentCacheProviderInventoryValidationResult
{
    public DocumentCacheProviderInventoryValidationResult(
        DocumentCacheInventoryValidationResult inventory,
        DocumentCacheEnqueueTriggerValidationResult enqueueTrigger
    )
        : this(inventory, DocumentCacheInventoryValidationComponents.FromAggregate(inventory), enqueueTrigger)
    { }

    public DocumentCacheProviderInventoryValidationResult(
        DocumentCacheInventoryValidationResult inventory,
        DocumentCacheInventoryValidationComponents inventoryComponents,
        DocumentCacheEnqueueTriggerValidationResult enqueueTrigger
    )
    {
        Inventory = inventory;
        InventoryComponents = inventoryComponents;
        EnqueueTrigger = enqueueTrigger;
    }

    public DocumentCacheInventoryValidationResult Inventory { get; }

    public DocumentCacheInventoryValidationComponents InventoryComponents { get; }

    public DocumentCacheEnqueueTriggerValidationResult EnqueueTrigger { get; }

    public bool IsSatisfied =>
        Inventory.Status == DocumentCacheInventoryStatus.Satisfied
        && EnqueueTrigger.Status == DocumentCacheEnqueueTriggerStatus.Satisfied;
}

public interface IDocumentCacheInventoryValidator
{
    RelationalProviderToken ProviderToken { get; }

    Task<DocumentCacheProviderInventoryValidationResult> ValidateInventoryAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    );
}

public sealed record DocumentCacheProviderPrerequisiteResult
{
    public DocumentCacheProviderPrerequisiteResult(
        DocumentCacheProviderPrerequisiteName name,
        DocumentCacheProviderPrerequisiteStatus status,
        string message
    )
    {
        Name = name;
        Status = status;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public DocumentCacheProviderPrerequisiteName Name { get; }

    public DocumentCacheProviderPrerequisiteStatus Status { get; }

    public string Message { get; }
}

public sealed record DocumentCacheSqlServerPrerequisiteDetails
{
    public DocumentCacheSqlServerPrerequisiteDetails(
        DocumentCacheProviderPrerequisiteResult readCommittedSnapshot,
        DocumentCacheProviderPrerequisiteResult nestedTriggers
    )
    {
        if (readCommittedSnapshot.Name != DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot)
        {
            throw new ArgumentException(
                "Read committed snapshot result must use the ReadCommittedSnapshot prerequisite name.",
                nameof(readCommittedSnapshot)
            );
        }

        if (nestedTriggers.Name != DocumentCacheProviderPrerequisiteName.NestedTriggers)
        {
            throw new ArgumentException(
                "Nested triggers result must use the NestedTriggers prerequisite name.",
                nameof(nestedTriggers)
            );
        }

        ReadCommittedSnapshot = readCommittedSnapshot;
        NestedTriggers = nestedTriggers;
    }

    public DocumentCacheProviderPrerequisiteResult ReadCommittedSnapshot { get; }

    public DocumentCacheProviderPrerequisiteResult NestedTriggers { get; }

    public bool HasFailure => IsFailure(ReadCommittedSnapshot.Status) || IsFailure(NestedTriggers.Status);

    public static DocumentCacheSqlServerPrerequisiteDetails NotApplicable() =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.NotApplicable,
                "Not applicable."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.NotApplicable,
                "Not applicable."
            )
        );

    private static bool IsFailure(DocumentCacheProviderPrerequisiteStatus status) =>
        status
            is DocumentCacheProviderPrerequisiteStatus.Disabled
                or DocumentCacheProviderPrerequisiteStatus.Unreadable;
}

public sealed record DocumentCacheProviderPrerequisiteValidationResult
{
    private DocumentCacheProviderPrerequisiteValidationResult(
        DocumentCacheSqlServerPrerequisiteDetails sqlServerPrerequisites,
        DocumentCacheTargetDiagnosticCategory? failureCategory,
        string message
    )
    {
        if (
            failureCategory is not null
            && failureCategory != DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
            && failureCategory != DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident
        )
        {
            throw new ArgumentException(
                "Provider prerequisite validation supports only prerequisite failure categories.",
                nameof(failureCategory)
            );
        }

        if (!sqlServerPrerequisites.HasFailure && failureCategory is not null)
        {
            throw new ArgumentException(
                "Satisfied provider prerequisites must not carry a failure category.",
                nameof(failureCategory)
            );
        }

        if (sqlServerPrerequisites.HasFailure && failureCategory is null)
        {
            throw new ArgumentException(
                "Failed provider prerequisites require a failure category.",
                nameof(failureCategory)
            );
        }

        SqlServerPrerequisites = sqlServerPrerequisites;
        FailureCategory = failureCategory;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public DocumentCacheSqlServerPrerequisiteDetails SqlServerPrerequisites { get; }

    public DocumentCacheTargetDiagnosticCategory? FailureCategory { get; }

    public string Message { get; }

    public bool IsSatisfied => FailureCategory is null;

    public static DocumentCacheProviderPrerequisiteValidationResult Initialization(
        DocumentCacheSqlServerPrerequisiteDetails sqlServerPrerequisites,
        DocumentCacheLifecycleObservation lifecycle
    )
    {
        ArgumentNullException.ThrowIfNull(sqlServerPrerequisites);
        ArgumentNullException.ThrowIfNull(lifecycle);

        if (!sqlServerPrerequisites.HasFailure)
        {
            return new(
                sqlServerPrerequisites,
                failureCategory: null,
                sqlServerPrerequisites.ReadCommittedSnapshot.Status
                == DocumentCacheProviderPrerequisiteStatus.NotApplicable
                    ? "Provider prerequisites are not applicable."
                    : "Provider prerequisites satisfied."
            );
        }

        return new(
            sqlServerPrerequisites,
            lifecycle.State == DocumentCacheLifecycleState.Disabled
                ? DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
                : DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
            lifecycle.State == DocumentCacheLifecycleState.Disabled
                ? "Provider prerequisite failed; correction can be retried by startup, CMS refresh, or supervisor tick."
                : "Provider prerequisite failure was observed outside the supported Disabled lifecycle; process restart or target replacement is required."
        );
    }

    public static DocumentCacheProviderPrerequisiteValidationResult ActivationPreflight(
        DocumentCacheSqlServerPrerequisiteDetails sqlServerPrerequisites
    )
    {
        ArgumentNullException.ThrowIfNull(sqlServerPrerequisites);

        if (!sqlServerPrerequisites.HasFailure)
        {
            return new(
                sqlServerPrerequisites,
                failureCategory: null,
                sqlServerPrerequisites.ReadCommittedSnapshot.Status
                == DocumentCacheProviderPrerequisiteStatus.NotApplicable
                    ? "Provider prerequisites are not applicable."
                    : "Provider prerequisites satisfied."
            );
        }

        return new(
            sqlServerPrerequisites,
            DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
            "Activation preflight provider prerequisite failed; correct provider settings and retry."
        );
    }
}

public interface IDocumentCacheProviderPrerequisiteValidator
{
    RelationalProviderToken ProviderToken { get; }

    Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateInitializationAsync(
        string connectionString,
        DocumentCacheLifecycleObservation lifecycle,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPreflightAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    );
}

public sealed record DocumentCacheResolutionRetryState
{
    public DocumentCacheResolutionRetryState(
        int attemptCount,
        DateTimeOffset? lastAttemptedAt,
        DateTimeOffset? nextRetryAt,
        DocumentCacheTargetDiagnosticCategory? lastFailureCategory,
        string? lastFailureMessage
    )
    {
        if (attemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptCount),
                "Attempt count must not be negative."
            );
        }

        AttemptCount = attemptCount;
        LastAttemptedAt = lastAttemptedAt;
        NextRetryAt = nextRetryAt;
        LastFailureCategory = lastFailureCategory;
        LastFailureMessage = lastFailureMessage is null
            ? null
            : DocumentCacheDiagnosticText.Sanitize(lastFailureMessage);
    }

    public int AttemptCount { get; }

    public DateTimeOffset? LastAttemptedAt { get; }

    public DateTimeOffset? NextRetryAt { get; }

    public DocumentCacheTargetDiagnosticCategory? LastFailureCategory { get; }

    public string? LastFailureMessage { get; }
}

public sealed record DocumentCacheTargetDiagnostic
{
    public DocumentCacheTargetDiagnostic(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetResolutionState resolutionState,
        RelationalProviderToken? providerToken,
        DocumentCacheTargetContextGeneration? generation,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint,
        DocumentCacheLifecycleObservation? lifecycle,
        DocumentCacheInventoryValidationResult? inventory,
        DocumentCacheEnqueueTriggerValidationResult? enqueueTrigger,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites,
        DocumentCacheResolutionRetryState? retryState,
        DocumentCacheTargetDiagnosticCategory category,
        string message,
        DocumentCacheInventoryValidationComponents? inventoryComponents = null
    )
    {
        TargetKey = targetKey;
        ResolutionState = resolutionState;
        ProviderToken = providerToken;
        Generation = generation;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        Lifecycle = lifecycle;
        Inventory = inventory;
        InventoryComponents =
            inventoryComponents
            ?? (
                inventory is null ? null : DocumentCacheInventoryValidationComponents.FromAggregate(inventory)
            );
        EnqueueTrigger = enqueueTrigger;
        SqlServerPrerequisites = sqlServerPrerequisites;
        RetryState = retryState;
        Category = category;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public DocumentCacheTargetKey TargetKey { get; }

    public DocumentCacheTargetResolutionState ResolutionState { get; }

    public RelationalProviderToken? ProviderToken { get; }

    public DocumentCacheTargetContextGeneration? Generation { get; }

    public DocumentCachePhysicalSourceFingerprint? PhysicalSourceFingerprint { get; }

    public DocumentCacheLifecycleObservation? Lifecycle { get; }

    public DocumentCacheInventoryValidationResult? Inventory { get; }

    public DocumentCacheInventoryValidationComponents? InventoryComponents { get; }

    public DocumentCacheEnqueueTriggerValidationResult? EnqueueTrigger { get; }

    public DocumentCacheSqlServerPrerequisiteDetails? SqlServerPrerequisites { get; }

    public DocumentCacheResolutionRetryState? RetryState { get; }

    public DocumentCacheTargetDiagnosticCategory Category { get; }

    public string Message { get; }
}

public sealed record DocumentCacheTargetObservation
{
    private DocumentCacheTargetObservation(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetResolutionState resolutionState,
        DocumentCacheTargetEligibilityState eligibilityState,
        DocumentCacheTargetEffectiveSettings effectiveSettings,
        DocumentCacheTargetContextGeneration? generation,
        RelationalProviderToken? providerToken,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint,
        DocumentCacheLifecycleObservation? lifecycle,
        DocumentCacheInventoryValidationResult? inventory,
        DocumentCacheInventoryValidationComponents? inventoryComponents,
        DocumentCacheEnqueueTriggerValidationResult? enqueueTrigger,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites,
        DocumentCacheResolutionRetryState? retryState,
        IEnumerable<DocumentCacheTargetDiagnostic>? diagnostics
    )
    {
        TargetKey = targetKey;
        ResolutionState = resolutionState;
        EligibilityState = eligibilityState;
        EffectiveSettings = effectiveSettings;
        Generation = generation;
        ProviderToken = providerToken;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        Lifecycle = lifecycle;
        Inventory = inventory;
        InventoryComponents = inventoryComponents;
        EnqueueTrigger = enqueueTrigger;
        SqlServerPrerequisites = sqlServerPrerequisites;
        RetryState = retryState;
        Diagnostics = diagnostics?.ToImmutableArray() ?? [];
    }

    public DocumentCacheTargetKey TargetKey { get; }

    public DocumentCacheTargetResolutionState ResolutionState { get; }

    public DocumentCacheTargetEligibilityState EligibilityState { get; }

    public DocumentCacheTargetEffectiveSettings EffectiveSettings { get; }

    public DocumentCacheTargetContextGeneration? Generation { get; }

    public RelationalProviderToken? ProviderToken { get; }

    public DocumentCachePhysicalSourceFingerprint? PhysicalSourceFingerprint { get; }

    public DocumentCacheLifecycleObservation? Lifecycle { get; }

    public DocumentCacheInventoryValidationResult? Inventory { get; }

    public DocumentCacheInventoryValidationComponents? InventoryComponents { get; }

    public DocumentCacheEnqueueTriggerValidationResult? EnqueueTrigger { get; }

    public DocumentCacheSqlServerPrerequisiteDetails? SqlServerPrerequisites { get; }

    public DocumentCacheResolutionRetryState? RetryState { get; }

    public ImmutableArray<DocumentCacheTargetDiagnostic> Diagnostics { get; }

    public static DocumentCacheTargetObservation Configured(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetEffectiveSettings effectiveSettings
    ) =>
        new(
            targetKey,
            DocumentCacheTargetResolutionState.Configured,
            DocumentCacheTargetEligibilityState.NotEvaluated,
            effectiveSettings,
            generation: null,
            providerToken: null,
            physicalSourceFingerprint: null,
            lifecycle: null,
            inventory: null,
            inventoryComponents: null,
            enqueueTrigger: null,
            sqlServerPrerequisites: null,
            retryState: null,
            diagnostics: []
        );

    public static DocumentCacheTargetObservation Unresolved(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetEffectiveSettings effectiveSettings,
        DocumentCacheResolutionRetryState? retryState,
        IEnumerable<DocumentCacheTargetDiagnostic>? diagnostics
    ) =>
        new(
            targetKey,
            DocumentCacheTargetResolutionState.Unresolved,
            DocumentCacheTargetEligibilityState.Ineligible,
            effectiveSettings,
            generation: null,
            providerToken: null,
            physicalSourceFingerprint: null,
            lifecycle: null,
            inventory: null,
            inventoryComponents: null,
            enqueueTrigger: null,
            sqlServerPrerequisites: null,
            retryState,
            diagnostics
        );

    public static DocumentCacheTargetObservation ResolvedEligible(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetEffectiveSettings effectiveSettings,
        DocumentCacheTargetContextGeneration generation,
        RelationalProviderToken providerToken,
        DocumentCachePhysicalSourceFingerprint physicalSourceFingerprint,
        DocumentCacheLifecycleObservation lifecycle,
        DocumentCacheInventoryValidationResult inventory,
        DocumentCacheEnqueueTriggerValidationResult enqueueTrigger,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites,
        IEnumerable<DocumentCacheTargetDiagnostic>? diagnostics = null,
        DocumentCacheInventoryValidationComponents? inventoryComponents = null
    ) =>
        new(
            targetKey,
            DocumentCacheTargetResolutionState.Resolved,
            DocumentCacheTargetEligibilityState.Eligible,
            effectiveSettings,
            generation,
            providerToken,
            physicalSourceFingerprint,
            lifecycle,
            inventory,
            inventoryComponents ?? DocumentCacheInventoryValidationComponents.FromAggregate(inventory),
            enqueueTrigger,
            sqlServerPrerequisites,
            retryState: null,
            diagnostics
        );

    public static DocumentCacheTargetObservation ResolvedIneligible(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetEffectiveSettings effectiveSettings,
        DocumentCacheTargetContextGeneration? generation,
        RelationalProviderToken? providerToken,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint,
        DocumentCacheLifecycleObservation? lifecycle,
        DocumentCacheInventoryValidationResult? inventory,
        DocumentCacheEnqueueTriggerValidationResult? enqueueTrigger,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites,
        DocumentCacheResolutionRetryState? retryState,
        IEnumerable<DocumentCacheTargetDiagnostic>? diagnostics,
        DocumentCacheInventoryValidationComponents? inventoryComponents = null
    ) =>
        new(
            targetKey,
            DocumentCacheTargetResolutionState.Resolved,
            DocumentCacheTargetEligibilityState.Ineligible,
            effectiveSettings,
            generation,
            providerToken,
            physicalSourceFingerprint,
            lifecycle,
            inventory,
            inventoryComponents
                ?? (
                    inventory is null
                        ? null
                        : DocumentCacheInventoryValidationComponents.FromAggregate(inventory)
                ),
            enqueueTrigger,
            sqlServerPrerequisites,
            retryState,
            diagnostics
        );

    public DocumentCacheTargetObservation WithAdditionalDiagnostic(DocumentCacheTargetDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new(
            TargetKey,
            ResolutionState,
            EligibilityState,
            EffectiveSettings,
            Generation,
            ProviderToken,
            PhysicalSourceFingerprint,
            Lifecycle,
            Inventory,
            InventoryComponents,
            EnqueueTrigger,
            SqlServerPrerequisites,
            RetryState,
            Diagnostics.Add(diagnostic)
        );
    }

    public DocumentCacheTargetObservation WithRetryDiagnostic(
        DocumentCacheResolutionRetryState retryState,
        DocumentCacheTargetDiagnostic diagnostic
    )
    {
        ArgumentNullException.ThrowIfNull(retryState);
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new(
            TargetKey,
            ResolutionState,
            EligibilityState,
            EffectiveSettings,
            Generation,
            ProviderToken,
            PhysicalSourceFingerprint,
            Lifecycle,
            Inventory,
            InventoryComponents,
            EnqueueTrigger,
            SqlServerPrerequisites,
            retryState,
            Diagnostics.Add(diagnostic)
        );
    }
}

internal static class DocumentCacheDiagnosticText
{
    private const int MaximumLength = 512;

    public static string Sanitize(string? message)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(message);
        return sanitized.Length <= MaximumLength ? sanitized : sanitized[..MaximumLength];
    }
}

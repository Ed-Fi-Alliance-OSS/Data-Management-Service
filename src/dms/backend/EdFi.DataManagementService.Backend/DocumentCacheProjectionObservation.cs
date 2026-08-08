// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using DocumentCacheActiveAdministrativeCommandSnapshots = System.Collections.Immutable.ImmutableDictionary<
    EdFi.DataManagementService.Backend.DocumentCacheAdministrativeCommandExecutionId,
    EdFi.DataManagementService.Backend.DocumentCacheAdministrativeCommandObservationSnapshot
>;
using DocumentCacheCurrentTargetHealthSnapshots = System.Collections.Immutable.ImmutableDictionary<
    EdFi.DataManagementService.Backend.DocumentCacheProjectionTargetContextKey,
    EdFi.DataManagementService.Backend.DocumentCacheProjectionTargetHealthSnapshot
>;
using DocumentCacheLastEndedTargetDiagnosticSnapshots = System.Collections.Immutable.ImmutableDictionary<
    EdFi.DataManagementService.Core.Configuration.DocumentCacheTargetKey,
    EdFi.DataManagementService.Backend.DocumentCacheProjectionTargetEndedDiagnosticSnapshot
>;
using DocumentCacheNoncurrentTargetHealthSnapshots = System.Collections.Immutable.ImmutableDictionary<
    EdFi.DataManagementService.Backend.DocumentCacheProjectionTargetContextKey,
    EdFi.DataManagementService.Backend.DocumentCacheProjectionTargetHealthSnapshot
>;

namespace EdFi.DataManagementService.Backend;

public sealed record DocumentCacheProjectionTargetContextKey
{
    public DocumentCacheProjectionTargetContextKey(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration generation
    )
    {
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
    }

    public DocumentCacheTargetKey TargetKey { get; }

    public DocumentCacheTargetContextGeneration Generation { get; }
}

public sealed record DocumentCacheAdministrativeCommandExecutionId
{
    public DocumentCacheAdministrativeCommandExecutionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Command execution id must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static DocumentCacheAdministrativeCommandExecutionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public enum DocumentCacheProjectionDocumentDiagnosticCategory
{
    PoisonSuppressed = 1,
    WorkAnomaly = 2,
    WriterOutcome = 5,
    ProviderFailure = 6,
    DeterministicInvariantFailure = 7,
    PossibleUnseededBaseline = 8,
}

public enum DocumentCacheProjectionLifecycleFenceState
{
    Unknown = 1,
    Eligible = 2,
    Fenced = 3,
}

public enum DocumentCacheProjectionTargetEndReason
{
    Replaced = 1,
    Removed = 2,
    Cancelled = 3,
    Shutdown = 4,
    Faulted = 5,
    Ineligible = 6,
}

public sealed record DocumentCacheProjectionExecutionStateSnapshot
{
    public DocumentCacheProjectionExecutionStateSnapshot(
        bool isRunning,
        bool isActivelyProcessing,
        bool isWaitingForWorkerGate,
        bool isInBackoff,
        DateTimeOffset? backoffUntil,
        bool cancellationRequested,
        DateTimeOffset? cancellationObservedAt
    )
    {
        IsRunning = isRunning;
        IsActivelyProcessing = isActivelyProcessing;
        IsWaitingForWorkerGate = isWaitingForWorkerGate;
        IsInBackoff = isInBackoff;
        BackoffUntil = backoffUntil;
        CancellationRequested = cancellationRequested;
        CancellationObservedAt = cancellationObservedAt;
    }

    public static DocumentCacheProjectionExecutionStateSnapshot Idle { get; } =
        new(
            isRunning: false,
            isActivelyProcessing: false,
            isWaitingForWorkerGate: false,
            isInBackoff: false,
            backoffUntil: null,
            cancellationRequested: false,
            cancellationObservedAt: null
        );

    public bool IsRunning { get; }

    public bool IsActivelyProcessing { get; }

    public bool IsWaitingForWorkerGate { get; }

    public bool IsInBackoff { get; }

    public DateTimeOffset? BackoffUntil { get; }

    public bool CancellationRequested { get; }

    public DateTimeOffset? CancellationObservedAt { get; }
}

public sealed record DocumentCacheProjectionThroughputSnapshot
{
    public DocumentCacheProjectionThroughputSnapshot(
        long startedCount,
        long completedCount,
        long itemCount,
        long failureCount,
        DateTimeOffset? lastStartedAt = null,
        DateTimeOffset? lastCompletedAt = null,
        TimeSpan? lastDuration = null
    )
    {
        StartedCount = DocumentCacheProjectionObservationGuard.RequireNonNegative(
            startedCount,
            nameof(startedCount)
        );
        CompletedCount = DocumentCacheProjectionObservationGuard.RequireNonNegative(
            completedCount,
            nameof(completedCount)
        );
        ItemCount = DocumentCacheProjectionObservationGuard.RequireNonNegative(itemCount, nameof(itemCount));
        FailureCount = DocumentCacheProjectionObservationGuard.RequireNonNegative(
            failureCount,
            nameof(failureCount)
        );
        if (lastDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastDuration),
                "Last duration must not be negative when supplied."
            );
        }

        LastStartedAt = lastStartedAt;
        LastCompletedAt = lastCompletedAt;
        LastDuration = lastDuration;
    }

    public static DocumentCacheProjectionThroughputSnapshot Empty { get; } =
        new(startedCount: 0, completedCount: 0, itemCount: 0, failureCount: 0);

    public long StartedCount { get; }

    public long CompletedCount { get; }

    public long ItemCount { get; }

    public long FailureCount { get; }

    public DateTimeOffset? LastStartedAt { get; }

    public DateTimeOffset? LastCompletedAt { get; }

    public TimeSpan? LastDuration { get; }
}

public sealed record DocumentCacheProjectionSuccessSnapshot
{
    public DocumentCacheProjectionSuccessSnapshot(
        long documentId,
        long contentVersion,
        DateTimeOffset completedAt
    )
    {
        DocumentId = DocumentCacheProjectionObservationGuard.RequirePositive(documentId, nameof(documentId));
        ContentVersion = DocumentCacheProjectionObservationGuard.RequirePositive(
            contentVersion,
            nameof(contentVersion)
        );
        CompletedAt = completedAt;
    }

    public long DocumentId { get; }

    public long ContentVersion { get; }

    public DateTimeOffset CompletedAt { get; }
}

public sealed record DocumentCacheProjectionDocumentDiagnostic
{
    public DocumentCacheProjectionDocumentDiagnostic(
        long documentId,
        DocumentCacheProjectionDocumentDiagnosticCategory category,
        string message,
        DateTimeOffset observedAt,
        DateTimeOffset? nextRetryAt = null
    )
    {
        DocumentId = DocumentCacheProjectionObservationGuard.RequirePositive(documentId, nameof(documentId));
        Category = DocumentCacheProjectionObservationGuard.RequireDefined(
            category,
            nameof(category),
            "Unsupported projection document diagnostic category."
        );
        Message = DocumentCacheProjectionObservationText.Sanitize(message);
        ObservedAt = observedAt;
        NextRetryAt = nextRetryAt;
    }

    public long DocumentId { get; }

    public DocumentCacheProjectionDocumentDiagnosticCategory Category { get; }

    public string Message { get; }

    public DateTimeOffset ObservedAt { get; }

    public DateTimeOffset? NextRetryAt { get; }
}

public sealed record DocumentCacheProjectionFailureDiagnostics
{
    public DocumentCacheProjectionFailureDiagnostics(
        int effectiveProjectorPageSize,
        int failureCount,
        DateTimeOffset? earliestRetryAt,
        long evictionCount,
        IEnumerable<DocumentCacheProjectionDocumentDiagnostic>? documentDiagnostics
    )
    {
        EffectiveProjectorPageSize = DocumentCacheProjectionObservationGuard.RequirePositive(
            effectiveProjectorPageSize,
            nameof(effectiveProjectorPageSize)
        );
        FailureCount = DocumentCacheProjectionObservationGuard.RequireNonNegative(
            failureCount,
            nameof(failureCount)
        );
        EvictionCount = DocumentCacheProjectionObservationGuard.RequireNonNegative(
            evictionCount,
            nameof(evictionCount)
        );
        EarliestRetryAt = earliestRetryAt;
        DocumentDiagnostics = DocumentCacheProjectionObservationBounds.Cap(
            documentDiagnostics,
            EffectiveProjectorPageSize
        );
        DocumentIds = DocumentDiagnostics.Select(diagnostic => diagnostic.DocumentId).ToImmutableArray();
    }

    public int EffectiveProjectorPageSize { get; }

    public int FailureCount { get; }

    public DateTimeOffset? EarliestRetryAt { get; }

    public long EvictionCount { get; }

    public ImmutableArray<DocumentCacheProjectionDocumentDiagnostic> DocumentDiagnostics { get; }

    public ImmutableArray<long> DocumentIds { get; }

    public static DocumentCacheProjectionFailureDiagnostics Empty(int effectiveProjectorPageSize) =>
        new(
            effectiveProjectorPageSize,
            failureCount: 0,
            earliestRetryAt: null,
            evictionCount: 0,
            documentDiagnostics: []
        );
}

public sealed record DocumentCacheProjectionPoisonTraversalSnapshot
{
    public DocumentCacheProjectionPoisonTraversalSnapshot(
        int effectiveProjectorPageSize,
        int suppressedDocumentCount,
        DateTimeOffset? earliestRetryAt,
        IEnumerable<long>? suppressedDocumentIds
    )
    {
        EffectiveProjectorPageSize = DocumentCacheProjectionObservationGuard.RequirePositive(
            effectiveProjectorPageSize,
            nameof(effectiveProjectorPageSize)
        );
        SuppressedDocumentCount = DocumentCacheProjectionObservationGuard.RequireNonNegative(
            suppressedDocumentCount,
            nameof(suppressedDocumentCount)
        );
        EarliestRetryAt = earliestRetryAt;
        SuppressedDocumentIds = DocumentCacheProjectionObservationBounds.CapPositiveIds(
            suppressedDocumentIds,
            EffectiveProjectorPageSize,
            nameof(suppressedDocumentIds)
        );
    }

    public int EffectiveProjectorPageSize { get; }

    public int SuppressedDocumentCount { get; }

    public DateTimeOffset? EarliestRetryAt { get; }

    public ImmutableArray<long> SuppressedDocumentIds { get; }

    public static DocumentCacheProjectionPoisonTraversalSnapshot Empty(int effectiveProjectorPageSize) =>
        new(
            effectiveProjectorPageSize,
            suppressedDocumentCount: 0,
            earliestRetryAt: null,
            suppressedDocumentIds: []
        );
}

public sealed record DocumentCacheProjectionLifecycleFenceSnapshot
{
    public DocumentCacheProjectionLifecycleFenceSnapshot(
        DocumentCacheProjectionLifecycleFenceState state,
        DocumentCacheLifecycleObservation? lifecycle,
        DateTimeOffset? observedAt,
        DocumentCacheTargetDiagnosticCategory? diagnosticCategory,
        string? message
    )
    {
        State = DocumentCacheProjectionObservationGuard.RequireDefined(
            state,
            nameof(state),
            "Unsupported projection lifecycle fence state."
        );
        Lifecycle = lifecycle;
        ObservedAt = observedAt;
        DiagnosticCategory = diagnosticCategory;
        Message = DocumentCacheProjectionObservationText.Sanitize(message ?? state.ToString());
    }

    public static DocumentCacheProjectionLifecycleFenceSnapshot Unknown { get; } =
        new(
            DocumentCacheProjectionLifecycleFenceState.Unknown,
            lifecycle: null,
            observedAt: null,
            diagnosticCategory: null,
            message: "Lifecycle/latch fence has not been observed."
        );

    public DocumentCacheProjectionLifecycleFenceState State { get; }

    public DocumentCacheLifecycleObservation? Lifecycle { get; }

    public DateTimeOffset? ObservedAt { get; }

    public DocumentCacheTargetDiagnosticCategory? DiagnosticCategory { get; }

    public string Message { get; }
}

public sealed record DocumentCacheProjectionTargetHealthSnapshot
{
    public DocumentCacheProjectionTargetHealthSnapshot(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration generation,
        int effectiveProjectorPageSize,
        DateTimeOffset observedAt,
        RelationalProviderToken? providerToken = null,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint = null,
        DocumentCacheProjectionExecutionStateSnapshot? executionState = null,
        DocumentCacheProjectionSuccessSnapshot? lastSuccess = null,
        DocumentCacheProjectionThroughputSnapshot? pageThroughput = null,
        DocumentCacheProjectionThroughputSnapshot? drainThroughput = null,
        DocumentCacheProjectionLifecycleFenceSnapshot? lifecycleFence = null,
        DocumentCacheProjectionPoisonTraversalSnapshot? poisonTraversal = null,
        DocumentCacheProjectionFailureDiagnostics? failureDiagnostics = null,
        DocumentCacheAdministrativeCommandExecutionId? activeCommandExecutionId = null,
        DocumentCacheAdministrativeCommand? activeAdministrativeCommand = null,
        DocumentCacheAdministrativeCommandPhase? activeAdministrativePhase = null,
        IEnumerable<DocumentCacheTargetDiagnostic>? targetDiagnostics = null
    )
    {
        EffectiveProjectorPageSize = DocumentCacheProjectionObservationGuard.RequirePositive(
            effectiveProjectorPageSize,
            nameof(effectiveProjectorPageSize)
        );
        if (
            activeCommandExecutionId is null
            && (activeAdministrativeCommand is not null || activeAdministrativePhase is not null)
        )
        {
            throw new ArgumentException(
                "Active administrative command observations require a command execution id."
            );
        }

        if (activeCommandExecutionId is not null && activeAdministrativeCommand is null)
        {
            throw new ArgumentException(
                "Active administrative command observations require the command value."
            );
        }

        ContextKey = new DocumentCacheProjectionTargetContextKey(targetKey, generation);
        ObservedAt = observedAt;
        ProviderToken = providerToken;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        ExecutionState = executionState ?? DocumentCacheProjectionExecutionStateSnapshot.Idle;
        LastSuccess = lastSuccess;
        PageThroughput = pageThroughput ?? DocumentCacheProjectionThroughputSnapshot.Empty;
        DrainThroughput = drainThroughput ?? DocumentCacheProjectionThroughputSnapshot.Empty;
        LifecycleFence = lifecycleFence ?? DocumentCacheProjectionLifecycleFenceSnapshot.Unknown;
        PoisonTraversal = EnsureMatchingPageSize(
            poisonTraversal
                ?? DocumentCacheProjectionPoisonTraversalSnapshot.Empty(EffectiveProjectorPageSize),
            EffectiveProjectorPageSize,
            nameof(poisonTraversal)
        );
        FailureDiagnostics = EnsureMatchingPageSize(
            failureDiagnostics ?? DocumentCacheProjectionFailureDiagnostics.Empty(EffectiveProjectorPageSize),
            EffectiveProjectorPageSize,
            nameof(failureDiagnostics)
        );
        ActiveCommandExecutionId = activeCommandExecutionId;
        ActiveAdministrativeCommand = activeAdministrativeCommand;
        ActiveAdministrativePhase = activeAdministrativePhase;
        TargetDiagnostics = DocumentCacheProjectionObservationBounds.CapLatest(
            targetDiagnostics,
            EffectiveProjectorPageSize
        );
    }

    public DocumentCacheProjectionTargetContextKey ContextKey { get; }

    public DocumentCacheTargetKey TargetKey => ContextKey.TargetKey;

    public DocumentCacheTargetContextGeneration Generation => ContextKey.Generation;

    public int EffectiveProjectorPageSize { get; }

    public DateTimeOffset ObservedAt { get; }

    public RelationalProviderToken? ProviderToken { get; }

    public DocumentCachePhysicalSourceFingerprint? PhysicalSourceFingerprint { get; }

    public DocumentCacheProjectionExecutionStateSnapshot ExecutionState { get; }

    public DocumentCacheProjectionSuccessSnapshot? LastSuccess { get; }

    public DocumentCacheProjectionThroughputSnapshot PageThroughput { get; }

    public DocumentCacheProjectionThroughputSnapshot DrainThroughput { get; }

    public DocumentCacheProjectionLifecycleFenceSnapshot LifecycleFence { get; }

    public DocumentCacheProjectionPoisonTraversalSnapshot PoisonTraversal { get; }

    public DocumentCacheProjectionFailureDiagnostics FailureDiagnostics { get; }

    public DocumentCacheAdministrativeCommandExecutionId? ActiveCommandExecutionId { get; }

    public DocumentCacheAdministrativeCommand? ActiveAdministrativeCommand { get; }

    public DocumentCacheAdministrativeCommandPhase? ActiveAdministrativePhase { get; }

    public ImmutableArray<DocumentCacheTargetDiagnostic> TargetDiagnostics { get; }

    private static DocumentCacheProjectionPoisonTraversalSnapshot EnsureMatchingPageSize(
        DocumentCacheProjectionPoisonTraversalSnapshot snapshot,
        int effectiveProjectorPageSize,
        string parameterName
    )
    {
        if (snapshot.EffectiveProjectorPageSize != effectiveProjectorPageSize)
        {
            throw new ArgumentException(
                "Projection poison traversal diagnostics must use the target effective projector page size.",
                parameterName
            );
        }

        return snapshot;
    }

    private static DocumentCacheProjectionFailureDiagnostics EnsureMatchingPageSize(
        DocumentCacheProjectionFailureDiagnostics snapshot,
        int effectiveProjectorPageSize,
        string parameterName
    )
    {
        if (snapshot.EffectiveProjectorPageSize != effectiveProjectorPageSize)
        {
            throw new ArgumentException(
                "Projection failure diagnostics must use the target effective projector page size.",
                parameterName
            );
        }

        return snapshot;
    }
}

public sealed record DocumentCacheProjectionTargetEndedDiagnosticSnapshot
{
    public DocumentCacheProjectionTargetEndedDiagnosticSnapshot(
        DocumentCacheProjectionTargetHealthSnapshot finalSnapshot,
        DocumentCacheProjectionTargetEndReason endReason,
        DateTimeOffset endedAt
    )
    {
        FinalSnapshot = finalSnapshot ?? throw new ArgumentNullException(nameof(finalSnapshot));
        EndReason = DocumentCacheProjectionObservationGuard.RequireDefined(
            endReason,
            nameof(endReason),
            "Unsupported projection target end reason."
        );
        EndedAt = endedAt;
    }

    public DocumentCacheProjectionTargetContextKey ContextKey => FinalSnapshot.ContextKey;

    public DocumentCacheTargetKey TargetKey => FinalSnapshot.TargetKey;

    public DocumentCacheTargetContextGeneration Generation => FinalSnapshot.Generation;

    public DocumentCacheProjectionTargetEndReason EndReason { get; }

    public DateTimeOffset EndedAt { get; }

    public DocumentCacheProjectionTargetHealthSnapshot FinalSnapshot { get; }
}

public sealed record DocumentCacheAdministrativeCommandObservationSnapshot
{
    public DocumentCacheAdministrativeCommandObservationSnapshot(
        DocumentCacheAdministrativeCommandExecutionId executionId,
        DocumentCacheAdministrativeCommand command,
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration targetGeneration,
        int effectiveProjectorPageSize,
        TimeSpan effectiveWorkflowTimeout,
        DateTimeOffset startedAt,
        DateTimeOffset observedAt,
        DocumentCacheAdministrativeCommandPhase currentPhase,
        DocumentCacheAdministrativeCommandPhase? lastCompletedPhase,
        bool mutated,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint = null,
        DocumentCacheLifecycleState? lifecycle = null,
        bool? cacheAheadRecoveryRequired = null,
        DocumentCacheOfflineWriterAdmissionConfirmation? offlineWriterAdmission = null,
        TimeSpan? elapsedCommandTime = null,
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> phaseDiagnostics = default,
        bool cancellationRequested = false,
        bool isCurrentGeneration = true,
        DocumentCacheTargetContextGeneration? currentTargetGeneration = null
    )
    {
        ExecutionId = executionId ?? throw new ArgumentNullException(nameof(executionId));
        Command = DocumentCacheProjectionObservationGuard.RequireDefined(
            command,
            nameof(command),
            "Unsupported administrative command."
        );
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        TargetGeneration = targetGeneration ?? throw new ArgumentNullException(nameof(targetGeneration));
        EffectiveProjectorPageSize = DocumentCacheProjectionObservationGuard.RequirePositive(
            effectiveProjectorPageSize,
            nameof(effectiveProjectorPageSize)
        );
        if (effectiveWorkflowTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveWorkflowTimeout),
                "Effective workflow timeout must be positive."
            );
        }

        if (elapsedCommandTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsedCommandTime),
                "Elapsed command time must not be negative when supplied."
            );
        }

        EffectiveWorkflowTimeout = effectiveWorkflowTimeout;
        StartedAt = startedAt;
        ObservedAt = observedAt;
        CurrentPhase = DocumentCacheProjectionObservationGuard.RequireDefined(
            currentPhase,
            nameof(currentPhase),
            "Unsupported administrative command phase."
        );
        LastCompletedPhase = lastCompletedPhase;
        Mutated = mutated;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        Lifecycle = lifecycle;
        CacheAheadRecoveryRequired = cacheAheadRecoveryRequired;
        OfflineWriterAdmission = offlineWriterAdmission;
        ElapsedCommandTime = elapsedCommandTime;
        PhaseDiagnostics = DocumentCacheProjectionObservationBounds.CapPhaseDiagnostics(
            phaseDiagnostics,
            EffectiveProjectorPageSize
        );
        CancellationRequested = cancellationRequested;
        IsCurrentGeneration = isCurrentGeneration;
        CurrentTargetGeneration = currentTargetGeneration;
    }

    public DocumentCacheAdministrativeCommandExecutionId ExecutionId { get; }

    public DocumentCacheAdministrativeCommand Command { get; }

    public DocumentCacheTargetKey TargetKey { get; }

    public DocumentCacheTargetContextGeneration TargetGeneration { get; }

    public int EffectiveProjectorPageSize { get; }

    public TimeSpan EffectiveWorkflowTimeout { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset ObservedAt { get; }

    public DocumentCacheAdministrativeCommandPhase CurrentPhase { get; }

    public DocumentCacheAdministrativeCommandPhase? LastCompletedPhase { get; }

    public bool Mutated { get; }

    public DocumentCachePhysicalSourceFingerprint? PhysicalSourceFingerprint { get; }

    public DocumentCacheLifecycleState? Lifecycle { get; }

    public bool? CacheAheadRecoveryRequired { get; }

    public DocumentCacheOfflineWriterAdmissionConfirmation? OfflineWriterAdmission { get; }

    public TimeSpan? ElapsedCommandTime { get; }

    public ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> PhaseDiagnostics { get; }

    public bool CancellationRequested { get; }

    public bool IsCurrentGeneration { get; }

    public DocumentCacheTargetContextGeneration? CurrentTargetGeneration { get; }

    internal DocumentCacheAdministrativeCommandObservationSnapshot WithGenerationCurrency(
        bool isCurrentGeneration,
        DocumentCacheTargetContextGeneration? currentTargetGeneration
    ) =>
        new(
            ExecutionId,
            Command,
            TargetKey,
            TargetGeneration,
            EffectiveProjectorPageSize,
            EffectiveWorkflowTimeout,
            StartedAt,
            ObservedAt,
            CurrentPhase,
            LastCompletedPhase,
            Mutated,
            PhysicalSourceFingerprint,
            Lifecycle,
            CacheAheadRecoveryRequired,
            OfflineWriterAdmission,
            ElapsedCommandTime,
            PhaseDiagnostics,
            CancellationRequested,
            isCurrentGeneration,
            currentTargetGeneration
        );
}

public sealed record DocumentCacheProjectionObservationSnapshot
{
    public DocumentCacheProjectionObservationSnapshot(
        DocumentCacheCurrentTargetHealthSnapshots currentTargetHealth,
        DocumentCacheLastEndedTargetDiagnosticSnapshots lastEndedTargetDiagnostics,
        DocumentCacheActiveAdministrativeCommandSnapshots activeAdministrativeCommands,
        DateTimeOffset observedAt
    )
    {
        CurrentTargetHealth =
            currentTargetHealth ?? throw new ArgumentNullException(nameof(currentTargetHealth));
        LastEndedTargetDiagnostics =
            lastEndedTargetDiagnostics ?? throw new ArgumentNullException(nameof(lastEndedTargetDiagnostics));
        ActiveAdministrativeCommands =
            activeAdministrativeCommands
            ?? throw new ArgumentNullException(nameof(activeAdministrativeCommands));
        ObservedAt = observedAt;
    }

    public DocumentCacheCurrentTargetHealthSnapshots CurrentTargetHealth { get; }

    public DocumentCacheLastEndedTargetDiagnosticSnapshots LastEndedTargetDiagnostics { get; }

    public DocumentCacheActiveAdministrativeCommandSnapshots ActiveAdministrativeCommands { get; }

    public DateTimeOffset ObservedAt { get; }

    public DocumentCacheProjectionTargetHealthSnapshot? GetCurrentTarget(
        DocumentCacheProjectionTargetContextKey contextKey
    )
    {
        ArgumentNullException.ThrowIfNull(contextKey);

        return CurrentTargetHealth.TryGetValue(
            contextKey,
            out DocumentCacheProjectionTargetHealthSnapshot? snapshot
        )
            ? snapshot
            : null;
    }

    public DocumentCacheProjectionTargetHealthSnapshot? GetCurrentTarget(DocumentCacheTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        return CurrentTargetHealth.Values.FirstOrDefault(snapshot => snapshot.TargetKey.Equals(targetKey));
    }

    public DocumentCacheAdministrativeCommandObservationSnapshot? GetActiveCommand(
        DocumentCacheAdministrativeCommandExecutionId executionId
    )
    {
        ArgumentNullException.ThrowIfNull(executionId);

        return ActiveAdministrativeCommands.TryGetValue(
            executionId,
            out DocumentCacheAdministrativeCommandObservationSnapshot? snapshot
        )
            ? snapshot
            : null;
    }
}

public interface IDocumentCacheProjectionObservationProvider
{
    DocumentCacheProjectionObservationSnapshot CurrentSnapshot { get; }
}

public interface IDocumentCacheProjectionObservationSink
{
    void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot);

    void EndTargetContext(
        DocumentCacheProjectionTargetContextKey contextKey,
        DocumentCacheProjectionTargetEndReason endReason,
        DateTimeOffset? endedAt = null
    );

    void ObserveAdministrativeCommand(DocumentCacheAdministrativeCommandObservationSnapshot snapshot);

    void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId);
}

internal interface IDocumentCacheProjectionCurrentTargetHealthSink
{
    void MarkTargetContextNoncurrent(
        DocumentCacheProjectionTargetContextKey contextKey,
        DateTimeOffset? observedAt = null
    );
}

public sealed class DocumentCacheProjectionObservationStore
    : IDocumentCacheProjectionObservationProvider,
        IDocumentCacheProjectionObservationSink,
        IDocumentCacheProjectionCurrentTargetHealthSink
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly IDocumentCacheProjectionTelemetry _telemetry;

    private ImmutableDictionary<
        DocumentCacheTargetKey,
        DocumentCacheProjectionTargetHealthSnapshot
    > _currentTargets = ImmutableDictionary<
        DocumentCacheTargetKey,
        DocumentCacheProjectionTargetHealthSnapshot
    >.Empty;

    private ImmutableDictionary<
        DocumentCacheTargetKey,
        DocumentCacheProjectionTargetEndedDiagnosticSnapshot
    > _lastEndedTargets = ImmutableDictionary<
        DocumentCacheTargetKey,
        DocumentCacheProjectionTargetEndedDiagnosticSnapshot
    >.Empty;

    private DocumentCacheNoncurrentTargetHealthSnapshots _noncurrentTargets =
        DocumentCacheNoncurrentTargetHealthSnapshots.Empty;

    private DocumentCacheActiveAdministrativeCommandSnapshots _activeCommands =
        DocumentCacheActiveAdministrativeCommandSnapshots.Empty;

    public DocumentCacheProjectionObservationStore()
        : this(TimeProvider.System) { }

    public DocumentCacheProjectionObservationStore(
        TimeProvider timeProvider,
        IDocumentCacheProjectionTelemetry? telemetry = null
    )
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _telemetry = telemetry ?? NoOpDocumentCacheProjectionTelemetry.Instance;
    }

    public DocumentCacheProjectionObservationSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                DocumentCacheCurrentTargetHealthSnapshots currentTargetHealth =
                    _currentTargets.Values.ToImmutableDictionary(snapshot => snapshot.ContextKey);

                DocumentCacheActiveAdministrativeCommandSnapshots activeCommands =
                    _activeCommands.Values.ToImmutableDictionary(
                        snapshot => snapshot.ExecutionId,
                        AttachGenerationCurrency
                    );

                return new(currentTargetHealth, _lastEndedTargets, activeCommands, _timeProvider.GetUtcNow());
            }
        }
    }

    public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            if (_noncurrentTargets.ContainsKey(snapshot.ContextKey))
            {
                _noncurrentTargets = _noncurrentTargets.SetItem(snapshot.ContextKey, snapshot);
                return;
            }

            if (
                _currentTargets.TryGetValue(
                    snapshot.TargetKey,
                    out DocumentCacheProjectionTargetHealthSnapshot? currentSnapshot
                )
                && currentSnapshot.Generation != snapshot.Generation
            )
            {
                _lastEndedTargets = _lastEndedTargets.SetItem(
                    snapshot.TargetKey,
                    new DocumentCacheProjectionTargetEndedDiagnosticSnapshot(
                        currentSnapshot,
                        DocumentCacheProjectionTargetEndReason.Replaced,
                        snapshot.ObservedAt
                    )
                );
            }

            _currentTargets = _currentTargets.SetItem(snapshot.TargetKey, snapshot);
        }

        _telemetry.RecordTargetObservation(snapshot);
    }

    public void MarkTargetContextNoncurrent(
        DocumentCacheProjectionTargetContextKey contextKey,
        DateTimeOffset? observedAt = null
    )
    {
        ArgumentNullException.ThrowIfNull(contextKey);

        lock (_sync)
        {
            if (
                !_currentTargets.TryGetValue(
                    contextKey.TargetKey,
                    out DocumentCacheProjectionTargetHealthSnapshot? currentSnapshot
                )
                || currentSnapshot.Generation != contextKey.Generation
            )
            {
                return;
            }

            _currentTargets = _currentTargets.Remove(contextKey.TargetKey);
            _noncurrentTargets = _noncurrentTargets.SetItem(contextKey, currentSnapshot);
        }
    }

    public void EndTargetContext(
        DocumentCacheProjectionTargetContextKey contextKey,
        DocumentCacheProjectionTargetEndReason endReason,
        DateTimeOffset? endedAt = null
    )
    {
        ArgumentNullException.ThrowIfNull(contextKey);

        lock (_sync)
        {
            if (
                _currentTargets.TryGetValue(
                    contextKey.TargetKey,
                    out DocumentCacheProjectionTargetHealthSnapshot? currentSnapshot
                )
                && currentSnapshot.Generation == contextKey.Generation
            )
            {
                _currentTargets = _currentTargets.Remove(contextKey.TargetKey);
                _lastEndedTargets = _lastEndedTargets.SetItem(
                    contextKey.TargetKey,
                    new DocumentCacheProjectionTargetEndedDiagnosticSnapshot(
                        currentSnapshot,
                        endReason,
                        endedAt ?? _timeProvider.GetUtcNow()
                    )
                );
                return;
            }

            if (!_noncurrentTargets.TryGetValue(contextKey, out currentSnapshot))
            {
                return;
            }

            _noncurrentTargets = _noncurrentTargets.Remove(contextKey);
            _lastEndedTargets = _lastEndedTargets.SetItem(
                contextKey.TargetKey,
                new DocumentCacheProjectionTargetEndedDiagnosticSnapshot(
                    currentSnapshot,
                    endReason,
                    endedAt ?? _timeProvider.GetUtcNow()
                )
            );
        }
    }

    public void ObserveAdministrativeCommand(DocumentCacheAdministrativeCommandObservationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            _activeCommands = _activeCommands.SetItem(snapshot.ExecutionId, snapshot);
        }
    }

    public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId)
    {
        ArgumentNullException.ThrowIfNull(executionId);

        lock (_sync)
        {
            _activeCommands = _activeCommands.Remove(executionId);
        }
    }

    private DocumentCacheAdministrativeCommandObservationSnapshot AttachGenerationCurrency(
        DocumentCacheAdministrativeCommandObservationSnapshot commandSnapshot
    )
    {
        DocumentCacheTargetContextGeneration? currentGeneration = _currentTargets.TryGetValue(
            commandSnapshot.TargetKey,
            out DocumentCacheProjectionTargetHealthSnapshot? currentTarget
        )
            ? currentTarget.Generation
            : null;

        bool isCurrentGeneration =
            currentGeneration is not null && currentGeneration == commandSnapshot.TargetGeneration;

        return commandSnapshot.WithGenerationCurrency(isCurrentGeneration, currentGeneration);
    }
}

internal static class DocumentCacheProjectionObservationBounds
{
    public static ImmutableArray<T> Cap<T>(IEnumerable<T>? values, int maximumCount) =>
        (values ?? []).Take(maximumCount).ToImmutableArray();

    public static ImmutableArray<T> CapLatest<T>(IEnumerable<T>? values, int maximumCount) =>
        (values ?? []).TakeLast(maximumCount).ToImmutableArray();

    public static ImmutableArray<long> CapPositiveIds(
        IEnumerable<long>? values,
        int maximumCount,
        string parameterName
    )
    {
        ImmutableArray<long> cappedValues = Cap(values, maximumCount);
        if (cappedValues.Any(documentId => documentId <= 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Document ids must be positive.");
        }

        return cappedValues;
    }

    public static ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> CapPhaseDiagnostics(
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> diagnostics,
        int effectiveProjectorPageSize
    )
    {
        DocumentCacheProjectionObservationGuard.RequirePositive(
            effectiveProjectorPageSize,
            nameof(effectiveProjectorPageSize)
        );

        if (diagnostics.IsDefaultOrEmpty)
        {
            return [];
        }

        return diagnostics
            .TakeLast(effectiveProjectorPageSize)
            .Select(diagnostic => new DocumentCacheAdministrativePhaseDiagnostic(
                diagnostic.CurrentPhase,
                diagnostic.LastCompletedPhase,
                diagnostic.Retryable,
                diagnostic.DiagnosticCategory,
                CapPositiveIds(
                    diagnostic.AffectedDocumentIds,
                    effectiveProjectorPageSize,
                    nameof(DocumentCacheAdministrativePhaseDiagnostic.AffectedDocumentIds)
                ),
                diagnostic.Message
            ))
            .ToImmutableArray();
    }

    public static ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> AppendPhaseDiagnostic(
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> diagnostics,
        DocumentCacheAdministrativePhaseDiagnostic diagnostic,
        int effectiveProjectorPageSize
    )
    {
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> existingDiagnostics = diagnostics.IsDefault
            ? []
            : diagnostics;

        return CapPhaseDiagnostics(existingDiagnostics.Add(diagnostic), effectiveProjectorPageSize);
    }
}

file static class DocumentCacheProjectionObservationGuard
{
    public static int RequirePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be positive.");
        }

        return value;
    }

    public static long RequirePositive(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be positive.");
        }

        return value;
    }

    public static int RequireNonNegative(int value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must not be negative.");
        }

        return value;
    }

    public static long RequireNonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must not be negative.");
        }

        return value;
    }

    public static TEnum RequireDefined<TEnum>(TEnum value, string parameterName, string message)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, message);
        }

        return value;
    }
}

file static class DocumentCacheProjectionObservationText
{
    private const int MaximumLength = 512;

    public static string Sanitize(string? message)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(message);
        return sanitized.Length <= MaximumLength ? sanitized : sanitized[..MaximumLength];
    }
}

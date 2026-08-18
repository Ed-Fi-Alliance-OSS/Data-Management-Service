// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal sealed class DocumentCacheStatusService : IDocumentCacheStatusService
{
    private const string EndpointTimeoutNotStartedMessage =
        "DocumentCache status target evaluation did not start before the endpoint timeout expired.";

    private const string EndpointTimeoutStartedMessage =
        "DocumentCache status target evaluation started but the endpoint timeout expired before durable observation completed.";

    private const string ObservationTimeoutMessage = "DocumentCache status observation timed out.";

    private readonly IDocumentCacheTargetRegistry _targetRegistry;
    private readonly IDocumentCacheProjectionObservationProvider _projectionObservationProvider;
    private readonly ImmutableDictionary<
        RelationalProviderToken,
        IDocumentCacheStatusCurrentSourceObserver
    > _currentSourceObservers;
    private readonly TimeProvider _timeProvider;
    private readonly IDocumentCacheEnqueueFailureObservationProvider? _enqueueFailureObservationProvider;
    private readonly IDocumentCacheStatusTelemetry _statusTelemetry;

    public DocumentCacheStatusService(
        IDocumentCacheTargetRegistry targetRegistry,
        IDocumentCacheProjectionObservationProvider projectionObservationProvider,
        IEnumerable<IDocumentCacheStatusCurrentSourceObserver> currentSourceObservers,
        TimeProvider timeProvider,
        IDocumentCacheEnqueueFailureObservationProvider? enqueueFailureObservationProvider = null,
        IDocumentCacheStatusTelemetry? statusTelemetry = null
    )
    {
        _targetRegistry = targetRegistry ?? throw new ArgumentNullException(nameof(targetRegistry));
        _projectionObservationProvider =
            projectionObservationProvider
            ?? throw new ArgumentNullException(nameof(projectionObservationProvider));
        _currentSourceObservers = (
            currentSourceObservers ?? throw new ArgumentNullException(nameof(currentSourceObservers))
        )
            .GroupBy(observer => observer.ProviderToken)
            .ToImmutableDictionary(group => group.Key, group => group.First());
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _enqueueFailureObservationProvider = enqueueFailureObservationProvider;
        _statusTelemetry = statusTelemetry ?? NoOpDocumentCacheStatusTelemetry.Instance;
    }

    public async Task<DocumentCacheStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        DocumentCacheTargetRegistrySnapshot registrySnapshot = _targetRegistry.CurrentSnapshot;
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = _targetRegistry.CurrentRuntimeSnapshot;
        DocumentCacheProjectionObservationSnapshot projectionSnapshot =
            _projectionObservationProvider.CurrentSnapshot;
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();

        ImmutableArray<DocumentCacheTargetObservation> targetObservations = registrySnapshot
            .Targets.OrderBy(target => target.TargetKey.TenantKey, StringComparer.Ordinal)
            .ThenBy(target => target.TargetKey.DataStoreId)
            .ToImmutableArray();

        if (targetObservations.IsEmpty)
        {
            return new DocumentCacheStatusResponse(observedAt, []);
        }

        TimeSpan endpointTimeout = targetObservations[0].EffectiveSettings.StatusEndpointTimeout;
        using CancellationTokenSource endpointTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        endpointTimeoutSource.CancelAfter(endpointTimeout);

        ConcurrentDictionary<int, DocumentCacheStatusTarget> results = new();
        ParallelOptions parallelOptions = new()
        {
            CancellationToken = endpointTimeoutSource.Token,
            MaxDegreeOfParallelism = SelectMaxDegreeOfParallelism(targetObservations),
        };

        try
        {
            await Parallel
                .ForEachAsync(
                    Enumerable.Range(0, targetObservations.Length),
                    parallelOptions,
                    async (index, endpointCancellationToken) =>
                    {
                        results[index] = await EvaluateTargetAsync(
                                targetObservations[index],
                                registrySnapshot.ObservedAt,
                                observedAt,
                                runtimeSnapshot,
                                projectionSnapshot,
                                endpointCancellationToken,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && endpointTimeoutSource.IsCancellationRequested)
        {
            // Unstarted targets are filled below with an endpoint-budget diagnostic.
        }

        cancellationToken.ThrowIfCancellationRequested();

        DocumentCacheStatusTarget[] orderedTargets = new DocumentCacheStatusTarget[targetObservations.Length];
        for (int index = 0; index < targetObservations.Length; index++)
        {
            orderedTargets[index] = results.TryGetValue(index, out DocumentCacheStatusTarget? target)
                ? target
                : CreateEndpointTimeoutTarget(
                    targetObservations[index],
                    registrySnapshot.ObservedAt,
                    observedAt,
                    projectionSnapshot,
                    EndpointTimeoutNotStartedMessage,
                    forceEndpointTimeoutClassification: true
                );
        }

        return new DocumentCacheStatusResponse(observedAt, orderedTargets);
    }

    private async Task<DocumentCacheStatusTarget> EvaluateTargetAsync(
        DocumentCacheTargetObservation targetObservation,
        DateTimeOffset registryObservedAt,
        DateTimeOffset processObservedAt,
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot,
        DocumentCacheProjectionObservationSnapshot projectionSnapshot,
        CancellationToken endpointCancellationToken,
        CancellationToken callerCancellationToken
    )
    {
        callerCancellationToken.ThrowIfCancellationRequested();

        DocumentCacheProjectionTargetHealthSnapshot? targetHealth = GetCurrentGenerationTargetHealth(
            targetObservation,
            projectionSnapshot
        );
        DocumentCacheStatusRuntimeObservation? runtimeObservation = ToRuntimeObservation(targetHealth);

        if (endpointCancellationToken.IsCancellationRequested)
        {
            return CreateEndpointTimeoutTarget(
                targetObservation,
                registryObservedAt,
                processObservedAt,
                projectionSnapshot,
                EndpointTimeoutStartedMessage,
                targetHealth,
                runtimeObservation
            );
        }

        DocumentCacheStatusProcessEligibility processEligibility =
            DocumentCacheStatusClassifier.ClassifyProcessEligibility(targetObservation, runtimeObservation);

        if (!processEligibility.IsEligible)
        {
            DocumentCacheStatusClassificationResult processClassification =
                DocumentCacheStatusClassifier.Classify(
                    targetObservation,
                    runtimeObservation,
                    durableObservation: null
                );

            return BuildStatusTarget(
                targetObservation,
                registryObservedAt,
                processObservedAt,
                projectionSnapshot,
                targetHealth,
                processClassification
            );
        }

        DocumentCacheStatusDurableObservation durableObservation = await ObserveDurableStatusAsync(
                targetObservation,
                runtimeSnapshot,
                endpointCancellationToken,
                callerCancellationToken
            )
            .ConfigureAwait(false);

        DocumentCacheStatusClassificationResult classification = DocumentCacheStatusClassifier.Classify(
            targetObservation,
            runtimeObservation,
            durableObservation
        );

        return BuildStatusTarget(
            targetObservation,
            registryObservedAt,
            processObservedAt,
            projectionSnapshot,
            targetHealth,
            classification
        );
    }

    private async Task<DocumentCacheStatusDurableObservation> ObserveDurableStatusAsync(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot,
        CancellationToken endpointCancellationToken,
        CancellationToken callerCancellationToken
    )
    {
        if (
            targetObservation.ProviderToken is null
            || targetObservation.Generation is null
            || !_currentSourceObservers.TryGetValue(
                targetObservation.ProviderToken,
                out IDocumentCacheStatusCurrentSourceObserver? observer
            )
        )
        {
            return DocumentCacheStatusDurableObservation.ProviderObservationFailed(
                "DocumentCache status current-source observer is not available for the target provider."
            );
        }

        RelationalProviderToken providerToken =
            targetObservation.ProviderToken
            ?? throw new InvalidOperationException(
                "DocumentCache status provider token is required after observer selection."
            );

        DocumentCacheTargetExecutionContext? executionContext = runtimeSnapshot.GetExecutionContext(
            targetObservation.TargetKey,
            targetObservation.Generation
        );

        if (executionContext is null)
        {
            return DocumentCacheStatusDurableObservation.ProviderObservationFailed(
                "DocumentCache target execution context is not available for status observation."
            );
        }

        using CancellationTokenSource targetObservationTimeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                endpointCancellationToken,
                callerCancellationToken
            );
        targetObservationTimeoutSource.CancelAfter(
            executionContext.EffectiveSettings.StatusObservationTimeout
        );

        long providerObservationStartedAt = Stopwatch.GetTimestamp();
        try
        {
            DocumentCacheStatusCurrentSourceObservationResult result = await observer
                .ObserveAsync(
                    new DocumentCacheStatusCurrentSourceObservationRequest(executionContext),
                    targetObservationTimeoutSource.Token
                )
                .ConfigureAwait(false);

            RecordProviderObservationTelemetry(
                targetObservation.TargetKey,
                providerToken,
                result,
                endpointCancellationToken,
                targetObservationTimeoutSource.Token,
                DocumentCacheProjectionTelemetry.GetElapsedTime(providerObservationStartedAt)
            );

            return ToDurableObservation(
                result,
                endpointCancellationToken,
                callerCancellationToken,
                targetObservationTimeoutSource.Token
            );
        }
        catch (OperationCanceledException)
        {
            callerCancellationToken.ThrowIfCancellationRequested();

            if (endpointCancellationToken.IsCancellationRequested)
            {
                _statusTelemetry.RecordProviderObservation(
                    targetObservation.TargetKey,
                    providerToken,
                    DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut,
                    DocumentCacheStatusReason.StatusEndpointTimeout,
                    DocumentCacheProjectionTelemetry.GetElapsedTime(providerObservationStartedAt),
                    lifecycleState: null,
                    oldestWorkAgeSeconds: null
                );
                return DocumentCacheStatusDurableObservation.EndpointTimeout(EndpointTimeoutStartedMessage);
            }

            _statusTelemetry.RecordProviderObservation(
                targetObservation.TargetKey,
                providerToken,
                DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut,
                DocumentCacheStatusReason.StatusObservationTimeout,
                DocumentCacheProjectionTelemetry.GetElapsedTime(providerObservationStartedAt),
                lifecycleState: null,
                oldestWorkAgeSeconds: null
            );
            return DocumentCacheStatusDurableObservation.ObservationTimeout(ObservationTimeoutMessage);
        }
        catch
        {
            _statusTelemetry.RecordProviderObservation(
                targetObservation.TargetKey,
                providerToken,
                DocumentCacheStatusProviderObservationTelemetryOutcome.Failed,
                DocumentCacheStatusReason.ProviderObservationFailed,
                DocumentCacheProjectionTelemetry.GetElapsedTime(providerObservationStartedAt),
                lifecycleState: null,
                oldestWorkAgeSeconds: null
            );
            return DocumentCacheStatusDurableObservation.ProviderObservationFailed(
                "DocumentCache status current-source observer failed."
            );
        }
    }

    private void RecordProviderObservationTelemetry(
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken,
        DocumentCacheStatusCurrentSourceObservationResult result,
        CancellationToken endpointCancellationToken,
        CancellationToken targetObservationCancellationToken,
        TimeSpan duration
    )
    {
        (DocumentCacheStatusProviderObservationTelemetryOutcome Outcome, DocumentCacheStatusReason Reason) =
            result.Outcome switch
            {
                DocumentCacheStatusCurrentSourceObservationOutcome.Succeeded
                or DocumentCacheStatusCurrentSourceObservationOutcome.StateMissingOrInvalid => (
                    DocumentCacheStatusProviderObservationTelemetryOutcome.Succeeded,
                    DocumentCacheStatusReason.None
                ),
                DocumentCacheStatusCurrentSourceObservationOutcome.ProviderTimeout => (
                    DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut,
                    DocumentCacheStatusReason.StatusObservationTimeout
                ),
                DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled
                    when endpointCancellationToken.IsCancellationRequested => (
                    DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut,
                    DocumentCacheStatusReason.StatusEndpointTimeout
                ),
                DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled
                    when targetObservationCancellationToken.IsCancellationRequested => (
                    DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut,
                    DocumentCacheStatusReason.StatusObservationTimeout
                ),
                DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled
                or DocumentCacheStatusCurrentSourceObservationOutcome.Failed => (
                    DocumentCacheStatusProviderObservationTelemetryOutcome.Failed,
                    DocumentCacheStatusReason.ProviderObservationFailed
                ),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.Outcome,
                    "Unsupported outcome."
                ),
            };

        _statusTelemetry.RecordProviderObservation(
            targetKey,
            providerToken,
            Outcome,
            Reason,
            duration,
            result.LifecycleState,
            result.OldestWorkAgeSeconds
        );
    }

    private static DocumentCacheStatusDurableObservation ToDurableObservation(
        DocumentCacheStatusCurrentSourceObservationResult result,
        CancellationToken endpointCancellationToken,
        CancellationToken callerCancellationToken,
        CancellationToken targetObservationCancellationToken
    )
    {
        callerCancellationToken.ThrowIfCancellationRequested();

        return result.Outcome switch
        {
            DocumentCacheStatusCurrentSourceObservationOutcome.Succeeded =>
                DocumentCacheStatusDurableObservation.Success(
                    result.LifecycleState!.Value,
                    result.CacheAheadRecoveryRequired!.Value,
                    result.QueuePresence == DocumentCacheStatusDurableQueuePresence.Empty
                        ? DocumentCacheStatusQueuePresence.Empty
                        : DocumentCacheStatusQueuePresence.NotEmpty,
                    result.OldestWorkFirstEnqueuedAt,
                    result.OldestWorkAgeSeconds,
                    result.DurableObservedAt!.Value
                ),
            DocumentCacheStatusCurrentSourceObservationOutcome.StateMissingOrInvalid =>
                DocumentCacheStatusDurableObservation.StateMissingOrInvalid(
                    result.DurableObservedAt!.Value,
                    result.Message
                ),
            DocumentCacheStatusCurrentSourceObservationOutcome.ProviderTimeout =>
                DocumentCacheStatusDurableObservation.ObservationTimeout(result.Message),
            DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled
                when endpointCancellationToken.IsCancellationRequested =>
                DocumentCacheStatusDurableObservation.EndpointTimeout(EndpointTimeoutStartedMessage),
            DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled
                when targetObservationCancellationToken.IsCancellationRequested =>
                DocumentCacheStatusDurableObservation.ObservationTimeout(ObservationTimeoutMessage),
            DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled =>
                DocumentCacheStatusDurableObservation.ProviderObservationFailed(result.Message),
            DocumentCacheStatusCurrentSourceObservationOutcome.Failed =>
                DocumentCacheStatusDurableObservation.ProviderObservationFailed(result.Message),
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.Outcome,
                "Unsupported outcome."
            ),
        };
    }

    private DocumentCacheStatusTarget CreateEndpointTimeoutTarget(
        DocumentCacheTargetObservation targetObservation,
        DateTimeOffset registryObservedAt,
        DateTimeOffset processObservedAt,
        DocumentCacheProjectionObservationSnapshot projectionSnapshot,
        string message,
        DocumentCacheProjectionTargetHealthSnapshot? targetHealth = null,
        DocumentCacheStatusRuntimeObservation? runtimeObservation = null,
        bool forceEndpointTimeoutClassification = false
    )
    {
        targetHealth ??= GetCurrentGenerationTargetHealth(targetObservation, projectionSnapshot);
        runtimeObservation ??= ToRuntimeObservation(targetHealth);

        DocumentCacheStatusClassificationResult classification = forceEndpointTimeoutClassification
            ? CreateEndpointTimeoutClassification(message)
            : DocumentCacheStatusClassifier.Classify(
                targetObservation,
                runtimeObservation,
                DocumentCacheStatusDurableObservation.EndpointTimeout(message)
            );

        return BuildStatusTarget(
            targetObservation,
            registryObservedAt,
            processObservedAt,
            projectionSnapshot,
            targetHealth,
            classification
        );
    }

    private static DocumentCacheStatusClassificationResult CreateEndpointTimeoutClassification(string message)
    {
        DocumentCacheStatusProcessEligibility processEligibility =
            DocumentCacheStatusProcessEligibility.Unknown(
                DocumentCacheStatusReason.StatusEndpointTimeout,
                message
            );

        return new(
            processEligibility,
            DurableObservationRequired: false,
            DurableObservedAt: null,
            new DocumentCacheStatusLifecycleComponent(
                DocumentCacheStatusLifecycleState.Unknown,
                DocumentCacheStatusAvailability.Unknown,
                message
            ),
            new DocumentCacheStatusCacheAheadComponent(
                DocumentCacheStatusCacheAheadState.Unknown,
                recoveryRequired: null,
                message
            ),
            new DocumentCacheStatusQueueSummary(
                DocumentCacheStatusQueuePresence.Unknown,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DocumentCacheStatusBacklogEstimate.Unavailable
            ),
            new DocumentCacheOperationalHealthComponent(
                DocumentCacheOperationalHealthStatus.Unknown,
                DocumentCacheStatusReason.StatusEndpointTimeout,
                message
            ),
            new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.Unknown,
                DocumentCacheStatusReason.StatusEndpointTimeout,
                message
            )
        );
    }

    private DocumentCacheStatusTarget BuildStatusTarget(
        DocumentCacheTargetObservation targetObservation,
        DateTimeOffset registryObservedAt,
        DateTimeOffset processObservedAt,
        DocumentCacheProjectionObservationSnapshot projectionSnapshot,
        DocumentCacheProjectionTargetHealthSnapshot? targetHealth,
        DocumentCacheStatusClassificationResult classification
    )
    {
        DocumentCacheStatusTarget statusTarget = new(
            DocumentCacheStatusTargetKey.FromTargetKey(targetObservation.TargetKey),
            targetObservation.Generation?.Value,
            processObservedAt,
            classification.DurableObservedAt,
            targetObservation.ProviderToken?.Value ?? targetHealth?.ProviderToken?.Value,
            targetObservation.PhysicalSourceFingerprint?.Value
                ?? targetHealth?.PhysicalSourceFingerprint?.Value,
            ToResolutionComponent(targetObservation, registryObservedAt),
            ToEligibilityComponent(classification.ProcessEligibility),
            ToInventoryComponentGroup(targetObservation, registryObservedAt),
            ToProviderPrerequisitesComponent(targetObservation, registryObservedAt),
            classification.Lifecycle,
            classification.CacheAhead,
            classification.OperationalHealth,
            classification.CaughtUp,
            classification.QueueSummary,
            ToExecutionStateComponent(targetHealth),
            ToActiveCommand(
                projectionSnapshot.GetCurrentGenerationActiveCommand(targetObservation.TargetKey)
            ),
            ToLastEndedDiagnostic(
                projectionSnapshot.GetCurrentGenerationLastEndedAdministrativeCommandDiagnostic(
                    targetObservation.TargetKey
                )
            ),
            ToTargetDiagnostics(targetHealth),
            ToDocumentDiagnostics(targetHealth),
            ToPoisonTraversalDiagnostics(targetHealth),
            DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(targetObservation.EffectiveSettings),
            ToEnqueueFailures(targetObservation.TargetKey)
        );

        _statusTelemetry.RecordStatusObservation(targetObservation, statusTarget);
        return statusTarget;
    }

    private DocumentCacheStatusEnqueueFailures ToEnqueueFailures(DocumentCacheTargetKey targetKey)
    {
        if (_enqueueFailureObservationProvider is null)
        {
            return new DocumentCacheStatusEnqueueFailures();
        }

        DocumentCacheEnqueueFailureSnapshot snapshot = _enqueueFailureObservationProvider.GetFailureSnapshot(
            targetKey
        );

        ImmutableArray<DocumentCacheStatusEnqueueFailureEvent> recentEvents = snapshot
            .RecentEvents.Select(failureEvent => new DocumentCacheStatusEnqueueFailureEvent(
                failureEvent.ObservedAt,
                ToStatusCategory(failureEvent.Category),
                ToStatusCanonicalOperation(failureEvent.CanonicalOperation),
                ToStatusResourceKind(failureEvent.ResourceKind),
                failureEvent.Message
            ))
            .ToImmutableArray();

        ImmutableArray<DocumentCacheStatusEnqueueFailureCategoryCount> byCategory = snapshot
            .RecentEvents.GroupBy(failureEvent => failureEvent.Category)
            .Select(group => new DocumentCacheStatusEnqueueFailureCategoryCount(
                ToStatusCategory(group.Key),
                group.Count()
            ))
            .ToImmutableArray();

        return new DocumentCacheStatusEnqueueFailures(recentEvents, byCategory, snapshot.EvictedCount);
    }

    private static DocumentCacheStatusEnqueueFailureCategory ToStatusCategory(
        DocumentCacheEnqueueFailureCategory category
    ) =>
        category switch
        {
            DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid =>
                DocumentCacheStatusEnqueueFailureCategory.StateMissingOrInvalid,
            DocumentCacheEnqueueFailureCategory.EnqueueTriggerUnavailable =>
                DocumentCacheStatusEnqueueFailureCategory.EnqueueTriggerUnavailable,
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed =>
                DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed,
            DocumentCacheEnqueueFailureCategory.ProviderTimeout =>
                DocumentCacheStatusEnqueueFailureCategory.ProviderTimeout,
            DocumentCacheEnqueueFailureCategory.ProviderUnavailable =>
                DocumentCacheStatusEnqueueFailureCategory.ProviderUnavailable,
            _ => DocumentCacheStatusEnqueueFailureCategory.UnclassifiedProviderFailure,
        };

    private static DocumentCacheStatusCanonicalOperation ToStatusCanonicalOperation(
        DocumentCacheEnqueueTelemetryCanonicalOperation operation
    ) =>
        operation switch
        {
            DocumentCacheEnqueueTelemetryCanonicalOperation.Insert =>
                DocumentCacheStatusCanonicalOperation.Insert,
            _ => DocumentCacheStatusCanonicalOperation.Update,
        };

    private static DocumentCacheStatusResourceKind ToStatusResourceKind(
        DocumentCacheEnqueueTelemetryResourceKind resourceKind
    ) =>
        resourceKind switch
        {
            DocumentCacheEnqueueTelemetryResourceKind.Descriptor =>
                DocumentCacheStatusResourceKind.Descriptor,
            _ => DocumentCacheStatusResourceKind.Resource,
        };

    private static DocumentCacheProjectionTargetHealthSnapshot? GetCurrentGenerationTargetHealth(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheProjectionObservationSnapshot projectionSnapshot
    )
    {
        if (targetObservation.Generation is null)
        {
            return null;
        }

        return projectionSnapshot.GetCurrentTarget(
            new DocumentCacheProjectionTargetContextKey(
                targetObservation.TargetKey,
                targetObservation.Generation
            )
        );
    }

    private static DocumentCacheStatusRuntimeObservation? ToRuntimeObservation(
        DocumentCacheProjectionTargetHealthSnapshot? targetHealth
    )
    {
        if (targetHealth is null)
        {
            return null;
        }

        DocumentCacheStatusExecutionState status = SelectExecutionStatus(targetHealth);
        return new DocumentCacheStatusRuntimeObservation(
            status,
            targetHealth.ObservedAt,
            targetHealth.ExecutionState.BackoffUntil,
            status switch
            {
                DocumentCacheStatusExecutionState.TargetBackoff =>
                    "Current-generation DocumentCache projection runtime is in target-level backoff.",
                DocumentCacheStatusExecutionState.Cancelling or DocumentCacheStatusExecutionState.Cancelled =>
                    "Current-generation DocumentCache projection runtime is cancelled.",
                DocumentCacheStatusExecutionState.Faulted or DocumentCacheStatusExecutionState.Stopped =>
                    "Current-generation DocumentCache projection runtime is faulted.",
                _ => null,
            }
        );
    }

    private static DocumentCacheStatusExecutionStateComponent ToExecutionStateComponent(
        DocumentCacheProjectionTargetHealthSnapshot? targetHealth
    )
    {
        if (targetHealth is null)
        {
            return new(
                DocumentCacheStatusExecutionState.NotObserved,
                observedAt: null,
                activeWorkers: null,
                concurrencySlotsUsed: null,
                targetBackoffUntil: null,
                lastSuccessfulWorkAt: null,
                lastFailureAt: null,
                message: null
            );
        }

        DocumentCacheStatusExecutionState status = SelectExecutionStatus(targetHealth);
        int activeWorkerCount = targetHealth.ExecutionState.IsActivelyProcessing ? 1 : 0;

        return new(
            status,
            targetHealth.ObservedAt,
            activeWorkerCount,
            activeWorkerCount,
            targetHealth.ExecutionState.BackoffUntil,
            targetHealth.LastSuccess?.CompletedAt,
            targetHealth.FailureDiagnostics.DocumentDiagnostics.LastOrDefault()?.ObservedAt,
            status switch
            {
                DocumentCacheStatusExecutionState.TargetBackoff =>
                    "Current-generation DocumentCache projection runtime is in target-level backoff.",
                DocumentCacheStatusExecutionState.Cancelling or DocumentCacheStatusExecutionState.Cancelled =>
                    "Current-generation DocumentCache projection runtime is cancelled.",
                DocumentCacheStatusExecutionState.Faulted or DocumentCacheStatusExecutionState.Stopped =>
                    "Current-generation DocumentCache projection runtime is faulted.",
                _ => null,
            }
        );
    }

    private static DocumentCacheStatusExecutionState SelectExecutionStatus(
        DocumentCacheProjectionTargetHealthSnapshot targetHealth
    )
    {
        DocumentCacheProjectionExecutionStateSnapshot executionState = targetHealth.ExecutionState;

        if (executionState.CancellationRequested)
        {
            return DocumentCacheStatusExecutionState.Cancelling;
        }

        if (executionState.CancellationObservedAt is not null)
        {
            return DocumentCacheStatusExecutionState.Cancelled;
        }

        if (executionState.IsInBackoff)
        {
            return DocumentCacheStatusExecutionState.TargetBackoff;
        }

        if (executionState.IsActivelyProcessing)
        {
            return DocumentCacheStatusExecutionState.Active;
        }

        if (executionState.IsWaitingForWorkerGate)
        {
            return DocumentCacheStatusExecutionState.WaitingForConcurrency;
        }

        return executionState.IsRunning
            ? DocumentCacheStatusExecutionState.WaitingForPoll
            : DocumentCacheStatusExecutionState.Idle;
    }

    private static DocumentCacheStatusResolutionComponent ToResolutionComponent(
        DocumentCacheTargetObservation targetObservation,
        DateTimeOffset registryObservedAt
    )
    {
        DocumentCacheTargetDiagnostic? latestDiagnostic = targetObservation.Diagnostics.LastOrDefault();
        return new(
            targetObservation.ResolutionState switch
            {
                DocumentCacheTargetResolutionState.Resolved => DocumentCacheStatusResolutionStatus.Resolved,
                DocumentCacheTargetResolutionState.Unresolved =>
                    DocumentCacheStatusResolutionStatus.Unresolved,
                _ => DocumentCacheStatusResolutionStatus.Unknown,
            },
            targetObservation.ResolutionState == DocumentCacheTargetResolutionState.Resolved
                ? DocumentCacheStatusResolutionReason.None
                : ToResolutionReason(latestDiagnostic?.Category),
            registryObservedAt,
            targetObservation.ResolutionState == DocumentCacheTargetResolutionState.Resolved
                ? null
                : latestDiagnostic?.Message ?? "DocumentCache target is not currently resolved."
        );
    }

    private static DocumentCacheStatusEligibilityComponent ToEligibilityComponent(
        DocumentCacheStatusProcessEligibility processEligibility
    ) =>
        new(
            processEligibility.Status switch
            {
                DocumentCacheStatusProcessEligibilityStatus.Eligible =>
                    DocumentCacheStatusEligibilityStatus.Eligible,
                DocumentCacheStatusProcessEligibilityStatus.Ineligible =>
                    DocumentCacheStatusEligibilityStatus.Ineligible,
                _ => DocumentCacheStatusEligibilityStatus.Unknown,
            },
            processEligibility.Reason,
            processEligibility.Message
        );

    private static DocumentCacheStatusInventoryComponentGroup ToInventoryComponentGroup(
        DocumentCacheTargetObservation targetObservation,
        DateTimeOffset registryObservedAt
    )
    {
        DocumentCacheInventoryValidationComponents? inventoryComponents =
            targetObservation.InventoryComponents;

        return new(
            inventoryComponents is null && targetObservation.EnqueueTrigger is null
                ? null
                : registryObservedAt,
            ToInventoryComponent(inventoryComponents?.State),
            ToInventoryComponent(inventoryComponents?.Work),
            ToInventoryComponent(inventoryComponents?.Cache),
            ToInventoryComponent(inventoryComponents?.DataStoreIdentity),
            ToEnqueueTriggerComponent(targetObservation.EnqueueTrigger)
        );
    }

    private static DocumentCacheStatusInventoryComponent ToInventoryComponent(
        DocumentCacheInventoryValidationResult? result
    )
    {
        if (result is null)
        {
            return new(
                DocumentCacheStatusInventoryStatus.NotObserved,
                DocumentCacheStatusInventoryReason.None,
                message: null
            );
        }

        return new(
            result.Status switch
            {
                DocumentCacheInventoryStatus.Satisfied => DocumentCacheStatusInventoryStatus.Valid,
                DocumentCacheInventoryStatus.NotEvaluated => DocumentCacheStatusInventoryStatus.NotObserved,
                DocumentCacheInventoryStatus.Unreadable => DocumentCacheStatusInventoryStatus.Unknown,
                _ => DocumentCacheStatusInventoryStatus.Invalid,
            },
            ToInventoryReason(result.Status),
            result.Status == DocumentCacheInventoryStatus.Satisfied ? null : result.Message
        );
    }

    private static DocumentCacheStatusEnqueueTriggerComponent ToEnqueueTriggerComponent(
        DocumentCacheEnqueueTriggerValidationResult? result
    )
    {
        if (result is null)
        {
            return new(
                DocumentCacheStatusEnqueueTriggerStatus.NotObserved,
                DocumentCacheStatusInventoryReason.None,
                message: null
            );
        }

        return new(
            result.Status switch
            {
                DocumentCacheEnqueueTriggerStatus.Satisfied =>
                    DocumentCacheStatusEnqueueTriggerStatus.Enabled,
                DocumentCacheEnqueueTriggerStatus.Disabled or DocumentCacheEnqueueTriggerStatus.Missing =>
                    DocumentCacheStatusEnqueueTriggerStatus.Disabled,
                DocumentCacheEnqueueTriggerStatus.Unreadable =>
                    DocumentCacheStatusEnqueueTriggerStatus.Unknown,
                DocumentCacheEnqueueTriggerStatus.NotEvaluated =>
                    DocumentCacheStatusEnqueueTriggerStatus.NotObserved,
                _ => DocumentCacheStatusEnqueueTriggerStatus.Invalid,
            },
            ToInventoryReason(result.Status),
            result.Status == DocumentCacheEnqueueTriggerStatus.Satisfied ? null : result.Message
        );
    }

    private static DocumentCacheStatusProviderPrerequisitesComponent ToProviderPrerequisitesComponent(
        DocumentCacheTargetObservation targetObservation,
        DateTimeOffset registryObservedAt
    )
    {
        if (targetObservation.SqlServerPrerequisites is null)
        {
            DocumentCacheStatusProviderPrerequisiteComponent unknown = new(
                DocumentCacheStatusProviderPrerequisiteStatus.Unknown,
                DocumentCacheStatusProviderPrerequisiteReason.None,
                message: null
            );

            return new(
                DocumentCacheStatusProviderPrerequisiteStatus.Unknown,
                DocumentCacheStatusProviderPrerequisiteReason.None,
                observedAt: null,
                unknown,
                unknown
            );
        }

        DocumentCacheProviderPrerequisiteResult readCommittedSnapshot = targetObservation
            .SqlServerPrerequisites
            .ReadCommittedSnapshot;
        DocumentCacheProviderPrerequisiteResult nestedTriggers = targetObservation
            .SqlServerPrerequisites
            .NestedTriggers;

        return new(
            ToProviderPrerequisitesStatus(readCommittedSnapshot.Status, nestedTriggers.Status),
            ToProviderPrerequisitesReason(readCommittedSnapshot.Status, nestedTriggers.Status),
            registryObservedAt,
            ToProviderPrerequisiteComponent(readCommittedSnapshot),
            ToProviderPrerequisiteComponent(nestedTriggers)
        );
    }

    private static DocumentCacheStatusProviderPrerequisiteComponent ToProviderPrerequisiteComponent(
        DocumentCacheProviderPrerequisiteResult result
    ) =>
        new(
            result.Status switch
            {
                DocumentCacheProviderPrerequisiteStatus.Satisfied =>
                    DocumentCacheStatusProviderPrerequisiteStatus.Satisfied,
                DocumentCacheProviderPrerequisiteStatus.Disabled =>
                    DocumentCacheStatusProviderPrerequisiteStatus.Unsatisfied,
                DocumentCacheProviderPrerequisiteStatus.Unreadable =>
                    DocumentCacheStatusProviderPrerequisiteStatus.Unknown,
                _ => DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
            },
            result.Status switch
            {
                DocumentCacheProviderPrerequisiteStatus.Disabled =>
                    DocumentCacheStatusProviderPrerequisiteReason.Disabled,
                DocumentCacheProviderPrerequisiteStatus.Unreadable =>
                    DocumentCacheStatusProviderPrerequisiteReason.Unreadable,
                _ => DocumentCacheStatusProviderPrerequisiteReason.None,
            },
            result.Status
                is DocumentCacheProviderPrerequisiteStatus.Satisfied
                    or DocumentCacheProviderPrerequisiteStatus.NotApplicable
                ? null
                : result.Message
        );

    private static DocumentCacheStatusActiveCommand? ToActiveCommand(
        DocumentCacheAdministrativeCommandObservationSnapshot? snapshot
    )
    {
        if (snapshot is null)
        {
            return null;
        }

        return new(
            snapshot.Command,
            snapshot.CurrentPhase,
            snapshot.CancellationRequested
                ? DocumentCacheStatusActiveCommandStatus.Cancelling
                : DocumentCacheStatusActiveCommandStatus.Running,
            snapshot.StartedAt,
            snapshot.ObservedAt,
            snapshot.PhaseDiagnostics.LastOrDefault()?.Message,
            snapshot.PhaseDiagnostics
        );
    }

    private static DocumentCacheStatusLastEndedDiagnostic? ToLastEndedDiagnostic(
        DocumentCacheAdministrativeCommandEndedDiagnosticSnapshot? snapshot
    )
    {
        if (snapshot is null)
        {
            return null;
        }

        return new(
            snapshot.Command,
            snapshot.Phase,
            snapshot.Outcome switch
            {
                DocumentCacheAdministrativeCommandEndedOutcome.Succeeded =>
                    DocumentCacheStatusEndedCommandOutcome.Succeeded,
                DocumentCacheAdministrativeCommandEndedOutcome.Cancelled =>
                    DocumentCacheStatusEndedCommandOutcome.Cancelled,
                DocumentCacheAdministrativeCommandEndedOutcome.Rejected =>
                    DocumentCacheStatusEndedCommandOutcome.Rejected,
                DocumentCacheAdministrativeCommandEndedOutcome.TimedOut =>
                    DocumentCacheStatusEndedCommandOutcome.TimedOut,
                _ => DocumentCacheStatusEndedCommandOutcome.Failed,
            },
            snapshot.StartedAt,
            snapshot.EndedAt,
            snapshot.ObservedAt,
            snapshot.Message
        );
    }

    private static DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent> ToTargetDiagnostics(
        DocumentCacheProjectionTargetHealthSnapshot? targetHealth
    ) =>
        targetHealth is null
            ? new()
            : new(
                targetHealth
                    .TargetDiagnosticEvents.Select(diagnostic => new DocumentCacheStatusTargetDiagnosticEvent(
                        diagnostic.ObservedAt,
                        ToTargetDiagnosticCategory(diagnostic.Category),
                        diagnostic.Message
                    ))
                    .ToImmutableArray(),
                ToIntEvictionCount(targetHealth.TargetDiagnosticEvictionCount)
            );

    private static DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent> ToDocumentDiagnostics(
        DocumentCacheProjectionTargetHealthSnapshot? targetHealth
    ) =>
        targetHealth is null
            ? new()
            : new(
                targetHealth
                    .FailureDiagnostics.DocumentDiagnostics.Select(
                        diagnostic => new DocumentCacheStatusDocumentDiagnosticEvent(
                            diagnostic.DocumentId,
                            diagnostic.ObservedAt,
                            ToDocumentDiagnosticCategory(diagnostic.Category),
                            diagnostic.NextRetryAt,
                            diagnostic.Message
                        )
                    )
                    .ToImmutableArray(),
                ToIntEvictionCount(targetHealth.FailureDiagnostics.EvictionCount)
            );

    private static DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent> ToPoisonTraversalDiagnostics(
        DocumentCacheProjectionTargetHealthSnapshot? targetHealth
    )
    {
        if (targetHealth is null)
        {
            return new();
        }

        return new(
            targetHealth
                .PoisonTraversal.SuppressedDocumentIds.Select(
                    documentId => new DocumentCacheStatusPoisonTraversalDiagnosticEvent(
                        documentId,
                        targetHealth.ObservedAt,
                        DocumentCacheStatusPoisonTraversalDiagnosticCategory.SkippedUntilRetry,
                        targetHealth.PoisonTraversal.EarliestRetryAt,
                        "DocumentCache poison traversal skipped document until retry."
                    )
                )
                .ToImmutableArray(),
            ToIntEvictionCount(targetHealth.PoisonTraversal.EvictionCount)
        );
    }

    private static int SelectMaxDegreeOfParallelism(ImmutableArray<DocumentCacheTargetObservation> targets) =>
        targets.Min(target => target.EffectiveSettings.ProjectorMaxConcurrentTargets);

    private static DocumentCacheStatusResolutionReason ToResolutionReason(
        DocumentCacheTargetDiagnosticCategory? category
    ) =>
        category switch
        {
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing =>
                DocumentCacheStatusResolutionReason.ProviderMetadataMissing,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown =>
                DocumentCacheStatusResolutionReason.ProviderMetadataUnknown,
            DocumentCacheTargetDiagnosticCategory.ProviderMismatch =>
                DocumentCacheStatusResolutionReason.ProviderMismatch,
            DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing =>
                DocumentCacheStatusResolutionReason.ConnectionInputMissing,
            DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure =>
                DocumentCacheStatusResolutionReason.PhysicalSourceFingerprintFailure,
            DocumentCacheTargetDiagnosticCategory.TargetReplaced =>
                DocumentCacheStatusResolutionReason.TargetReplaced,
            DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure =>
                DocumentCacheStatusResolutionReason.CmsUnavailable,
            _ => DocumentCacheStatusResolutionReason.TargetNotFound,
        };

    private static DocumentCacheStatusInventoryReason ToInventoryReason(
        DocumentCacheInventoryStatus status
    ) =>
        status switch
        {
            DocumentCacheInventoryStatus.Missing => DocumentCacheStatusInventoryReason.Missing,
            DocumentCacheInventoryStatus.Invalid => DocumentCacheStatusInventoryReason.Invalid,
            DocumentCacheInventoryStatus.Unreadable => DocumentCacheStatusInventoryReason.Unreadable,
            _ => DocumentCacheStatusInventoryReason.None,
        };

    private static DocumentCacheStatusInventoryReason ToInventoryReason(
        DocumentCacheEnqueueTriggerStatus status
    ) =>
        status switch
        {
            DocumentCacheEnqueueTriggerStatus.Missing => DocumentCacheStatusInventoryReason.Missing,
            DocumentCacheEnqueueTriggerStatus.Disabled => DocumentCacheStatusInventoryReason.Disabled,
            DocumentCacheEnqueueTriggerStatus.Invalid => DocumentCacheStatusInventoryReason.Invalid,
            DocumentCacheEnqueueTriggerStatus.Unreadable => DocumentCacheStatusInventoryReason.Unreadable,
            _ => DocumentCacheStatusInventoryReason.None,
        };

    private static DocumentCacheStatusProviderPrerequisiteStatus ToProviderPrerequisitesStatus(
        DocumentCacheProviderPrerequisiteStatus first,
        DocumentCacheProviderPrerequisiteStatus second
    )
    {
        if (
            first == DocumentCacheProviderPrerequisiteStatus.Disabled
            || second == DocumentCacheProviderPrerequisiteStatus.Disabled
        )
        {
            return DocumentCacheStatusProviderPrerequisiteStatus.Unsatisfied;
        }

        if (
            first == DocumentCacheProviderPrerequisiteStatus.Unreadable
            || second == DocumentCacheProviderPrerequisiteStatus.Unreadable
        )
        {
            return DocumentCacheStatusProviderPrerequisiteStatus.UnsupportedIncident;
        }

        return DocumentCacheStatusProviderPrerequisiteStatus.Satisfied;
    }

    private static DocumentCacheStatusProviderPrerequisiteReason ToProviderPrerequisitesReason(
        DocumentCacheProviderPrerequisiteStatus first,
        DocumentCacheProviderPrerequisiteStatus second
    )
    {
        if (
            first == DocumentCacheProviderPrerequisiteStatus.Disabled
            || second == DocumentCacheProviderPrerequisiteStatus.Disabled
        )
        {
            return DocumentCacheStatusProviderPrerequisiteReason.Disabled;
        }

        if (
            first == DocumentCacheProviderPrerequisiteStatus.Unreadable
            || second == DocumentCacheProviderPrerequisiteStatus.Unreadable
        )
        {
            return DocumentCacheStatusProviderPrerequisiteReason.UnsupportedIncident;
        }

        return DocumentCacheStatusProviderPrerequisiteReason.None;
    }

    private static DocumentCacheStatusTargetDiagnosticCategory ToTargetDiagnosticCategory(
        DocumentCacheTargetDiagnosticCategory category
    ) =>
        category switch
        {
            DocumentCacheTargetDiagnosticCategory.TargetNotConfigured
            or DocumentCacheTargetDiagnosticCategory.TargetUnresolved
            or DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing
            or DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown
            or DocumentCacheTargetDiagnosticCategory.ProviderMismatch
            or DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing
            or DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure
            or DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure
            or DocumentCacheTargetDiagnosticCategory.TargetReplaced
            or DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch =>
                DocumentCacheStatusTargetDiagnosticCategory.TargetResolution,
            DocumentCacheTargetDiagnosticCategory.InventoryFailure
            or DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure
            or DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure
            or DocumentCacheTargetDiagnosticCategory.ResourceKeyCompatibilityFailure =>
                DocumentCacheStatusTargetDiagnosticCategory.Inventory,
            DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
            or DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident =>
                DocumentCacheStatusTargetDiagnosticCategory.ProviderPrerequisite,
            DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure
            or DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure =>
                DocumentCacheStatusTargetDiagnosticCategory.ProviderObservationFailed,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch
            or DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery
            or DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet
            or DocumentCacheTargetDiagnosticCategory.NonemptyGuardedActivationState
            or DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown
            or DocumentCacheTargetDiagnosticCategory.DeterministicInvariantFailure =>
                DocumentCacheStatusTargetDiagnosticCategory.TargetInvariant,
            _ => DocumentCacheStatusTargetDiagnosticCategory.TargetInvariant,
        };

    private static DocumentCacheStatusDocumentDiagnosticCategory ToDocumentDiagnosticCategory(
        DocumentCacheProjectionDocumentDiagnosticCategory category
    ) =>
        category switch
        {
            DocumentCacheProjectionDocumentDiagnosticCategory.PoisonSuppressed =>
                DocumentCacheStatusDocumentDiagnosticCategory.PoisonRetryScheduled,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly =>
                DocumentCacheStatusDocumentDiagnosticCategory.SourceChanged,
            DocumentCacheProjectionDocumentDiagnosticCategory.WriterOutcome =>
                DocumentCacheStatusDocumentDiagnosticCategory.WriterFailed,
            DocumentCacheProjectionDocumentDiagnosticCategory.ProviderFailure =>
                DocumentCacheStatusDocumentDiagnosticCategory.MaterializationFailed,
            DocumentCacheProjectionDocumentDiagnosticCategory.PossibleUnseededBaseline =>
                DocumentCacheStatusDocumentDiagnosticCategory.CacheAheadSuspected,
            _ => DocumentCacheStatusDocumentDiagnosticCategory.MaterializationFailed,
        };

    private static int ToIntEvictionCount(long evictionCount) =>
        evictionCount > int.MaxValue ? int.MaxValue : (int)evictionCount;
}

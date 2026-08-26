// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    private enum ProviderObservationCancellationSource
    {
        None = 0,
        StatusObservationTimeout = 1,
        EndpointTimeout = 2,
    }

    private sealed record ObservedDurableStatus(
        DocumentCacheStatusDurableObservation DurableObservation,
        TimeSpan? ProviderObservationDuration
    );

    private static readonly ImmutableArray<DocumentCacheEnqueueFailureCategory> EnqueueFailureCategoryOrder =
    [
        DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid,
        DocumentCacheEnqueueFailureCategory.EnqueueTriggerUnavailable,
        DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed,
        DocumentCacheEnqueueFailureCategory.ProviderTimeout,
        DocumentCacheEnqueueFailureCategory.ProviderUnavailable,
        DocumentCacheEnqueueFailureCategory.UnclassifiedProviderFailure,
    ];

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
        CancellationToken cancellationToken = default,
        DocumentCacheStatusEvaluationMode evaluationMode = DocumentCacheStatusEvaluationMode.RuntimeEndpoint
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(evaluationMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evaluationMode),
                evaluationMode,
                "Unsupported DocumentCache status evaluation mode."
            );
        }

        DocumentCacheTargetStatusSnapshot statusSnapshot = _targetRegistry.CurrentStatusSnapshot;
        DocumentCacheTargetRegistrySnapshot registrySnapshot = statusSnapshot.RegistrySnapshot;
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = statusSnapshot.RuntimeSnapshot;
        DocumentCacheProjectionObservationSnapshot projectionSnapshot =
            _projectionObservationProvider.CurrentSnapshot;
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();

        ImmutableArray<DocumentCacheTargetObservation> targetObservations = registrySnapshot
            .Targets.OrderBy(target => target.TargetKey, DocumentCacheStatusTargetKeyComparer.Instance)
            .ToImmutableArray();

        if (targetObservations.IsEmpty)
        {
            return new DocumentCacheStatusResponse(observedAt, []);
        }

        TimeSpan endpointTimeout = targetObservations.Min(target =>
            target.EffectiveSettings.StatusEndpointTimeout
        );
        using CancellationTokenSource endpointTimeoutSource = new(endpointTimeout, _timeProvider);
        using CancellationTokenSource endpointCancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, endpointTimeoutSource.Token);

        ConcurrentDictionary<int, DocumentCacheStatusTarget> results = new();
        ParallelOptions parallelOptions = new()
        {
            CancellationToken = endpointCancellationSource.Token,
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
                                evaluationMode,
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
                    evaluationMode,
                    EndpointTimeoutNotStartedMessage
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
        DocumentCacheStatusEvaluationMode evaluationMode,
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
                evaluationMode,
                EndpointTimeoutStartedMessage,
                targetHealth,
                runtimeObservation
            );
        }

        DocumentCacheStatusProcessEligibility processEligibility =
            DocumentCacheStatusClassifier.ClassifyProcessEligibility(targetObservation, runtimeObservation);

        if (!ShouldObserveDurableStatus(processEligibility, evaluationMode))
        {
            DocumentCacheStatusClassificationResult processClassification =
                DocumentCacheStatusClassifier.Classify(
                    targetObservation,
                    runtimeObservation,
                    durableObservation: null,
                    evaluationMode
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

        ObservedDurableStatus observedDurableStatus = await ObserveDurableStatusAsync(
                targetObservation,
                runtimeSnapshot,
                endpointCancellationToken,
                callerCancellationToken
            )
            .ConfigureAwait(false);

        DocumentCacheStatusClassificationResult classification = DocumentCacheStatusClassifier.Classify(
            targetObservation,
            runtimeObservation,
            observedDurableStatus.DurableObservation,
            evaluationMode
        );

        return BuildStatusTarget(
            targetObservation,
            registryObservedAt,
            processObservedAt,
            projectionSnapshot,
            targetHealth,
            classification,
            observedDurableStatus.ProviderObservationDuration
        );
    }

    private static bool ShouldObserveDurableStatus(
        DocumentCacheStatusProcessEligibility processEligibility,
        DocumentCacheStatusEvaluationMode evaluationMode
    ) =>
        processEligibility.IsEligible
        || (
            evaluationMode == DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            && processEligibility.Status == DocumentCacheStatusProcessEligibilityStatus.Unknown
            && processEligibility.Reason == DocumentCacheStatusReason.RuntimeNotObserved
        );

    private async Task<ObservedDurableStatus> ObserveDurableStatusAsync(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot,
        CancellationToken endpointCancellationToken,
        CancellationToken callerCancellationToken
    )
    {
        if (
            !TrySelectCurrentSourceObserver(
                targetObservation,
                _currentSourceObservers,
                out IDocumentCacheStatusCurrentSourceObserver? observer,
                out string? observerSelectionFailureMessage
            )
        )
        {
            return new ObservedDurableStatus(
                DocumentCacheStatusDurableObservation.ProviderObservationFailed(
                    observerSelectionFailureMessage
                ),
                ProviderObservationDuration: null
            );
        }

        RelationalProviderToken providerToken =
            targetObservation.ProviderToken
            ?? throw new InvalidOperationException(
                "DocumentCache status provider token is required after observer selection."
            );
        DocumentCacheTargetContextGeneration generation =
            targetObservation.Generation
            ?? throw new InvalidOperationException(
                "DocumentCache target generation is required after observer selection."
            );

        DocumentCacheTargetExecutionContext? executionContext = runtimeSnapshot.GetExecutionContext(
            targetObservation.TargetKey,
            generation
        );

        if (executionContext is null)
        {
            return new ObservedDurableStatus(
                DocumentCacheStatusDurableObservation.ProviderObservationFailed(
                    "DocumentCache target execution context is not available for status observation."
                ),
                ProviderObservationDuration: null
            );
        }

        using CancellationTokenSource statusObservationTimeoutSource = new(
            executionContext.EffectiveSettings.StatusObservationTimeout,
            _timeProvider
        );
        int firstCancellationSource = (int)ProviderObservationCancellationSource.None;

        void RecordFirstCancellationSource(ProviderObservationCancellationSource cancellationSource)
        {
            _ = Interlocked.CompareExchange(
                ref firstCancellationSource,
                (int)cancellationSource,
                (int)ProviderObservationCancellationSource.None
            );
        }

        using CancellationTokenRegistration statusObservationTimeoutRegistration =
            statusObservationTimeoutSource.Token.Register(() =>
                RecordFirstCancellationSource(ProviderObservationCancellationSource.StatusObservationTimeout)
            );
        using CancellationTokenRegistration endpointTimeoutRegistration = endpointCancellationToken.Register(
            () =>
                RecordFirstCancellationSource(ProviderObservationCancellationSource.EndpointTimeout)
        );
        using CancellationTokenSource providerObservationCancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                endpointCancellationToken,
                callerCancellationToken,
                statusObservationTimeoutSource.Token
            );
        ProviderObservationCancellationSource GetFirstCancellationSource() =>
            SelectFirstCancellationSource(
                (ProviderObservationCancellationSource)Volatile.Read(ref firstCancellationSource),
                statusObservationTimeoutSource.Token,
                endpointCancellationToken
            );

        CancellationToken providerObservationCancellationToken = providerObservationCancellationSource.Token;

        DocumentCacheStatusDurableObservation CreateTimedOutObservation(
            ProviderObservationCancellationSource cancellationSource
        ) =>
            cancellationSource == ProviderObservationCancellationSource.EndpointTimeout
                ? DocumentCacheStatusDurableObservation.EndpointTimeout(EndpointTimeoutStartedMessage)
                : DocumentCacheStatusDurableObservation.ObservationTimeout(ObservationTimeoutMessage);

        DocumentCacheStatusReason CreateTimedOutReason(
            ProviderObservationCancellationSource cancellationSource
        ) =>
            cancellationSource == ProviderObservationCancellationSource.EndpointTimeout
                ? DocumentCacheStatusReason.StatusEndpointTimeout
                : DocumentCacheStatusReason.StatusObservationTimeout;

        long providerObservationStartedAt = Stopwatch.GetTimestamp();
        try
        {
            Task<DocumentCacheStatusCurrentSourceObservationResult> observationTask = observer.ObserveAsync(
                new DocumentCacheStatusCurrentSourceObservationRequest(executionContext),
                providerObservationCancellationToken
            );
            DocumentCacheStatusCurrentSourceObservationResult result;

            try
            {
                result = await observationTask
                    .WaitAsync(providerObservationCancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (observationTask.IsCompletedSuccessfully)
            {
                result = observationTask.Result;
            }

            if (result.Outcome == DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled)
            {
                callerCancellationToken.ThrowIfCancellationRequested();
            }

            ProviderObservationCancellationSource cancellationSource = GetFirstCancellationSource();
            TimeSpan providerObservationDuration = DocumentCacheProjectionTelemetry.GetElapsedTime(
                providerObservationStartedAt
            );

            RecordProviderObservationTelemetry(
                targetObservation.TargetKey,
                providerToken,
                result,
                cancellationSource,
                providerObservationDuration
            );

            return new ObservedDurableStatus(
                ToDurableObservation(result, callerCancellationToken, cancellationSource),
                providerObservationDuration
            );
        }
        catch (OperationCanceledException)
        {
            callerCancellationToken.ThrowIfCancellationRequested();

            ProviderObservationCancellationSource cancellationSource = GetFirstCancellationSource();
            TimeSpan providerObservationDuration = DocumentCacheProjectionTelemetry.GetElapsedTime(
                providerObservationStartedAt
            );

            _statusTelemetry.RecordProviderObservation(
                targetObservation.TargetKey,
                providerToken,
                DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut,
                CreateTimedOutReason(cancellationSource),
                providerObservationDuration,
                lifecycleState: null,
                oldestWorkAgeSeconds: null
            );
            return new ObservedDurableStatus(
                CreateTimedOutObservation(cancellationSource),
                providerObservationDuration
            );
        }
        catch
        {
            TimeSpan providerObservationDuration = DocumentCacheProjectionTelemetry.GetElapsedTime(
                providerObservationStartedAt
            );

            _statusTelemetry.RecordProviderObservation(
                targetObservation.TargetKey,
                providerToken,
                DocumentCacheStatusProviderObservationTelemetryOutcome.Failed,
                DocumentCacheStatusReason.ProviderObservationFailed,
                providerObservationDuration,
                lifecycleState: null,
                oldestWorkAgeSeconds: null
            );
            return new ObservedDurableStatus(
                DocumentCacheStatusDurableObservation.ProviderObservationFailed(
                    "DocumentCache status current-source observer failed."
                ),
                providerObservationDuration
            );
        }
    }

    internal static bool TrySelectCurrentSourceObserver(
        DocumentCacheTargetObservation targetObservation,
        IReadOnlyDictionary<RelationalProviderToken, IDocumentCacheStatusCurrentSourceObserver> observers,
        [NotNullWhen(true)] out IDocumentCacheStatusCurrentSourceObserver? observer,
        [NotNullWhen(false)] out string? failureMessage
    )
    {
        ArgumentNullException.ThrowIfNull(targetObservation);
        ArgumentNullException.ThrowIfNull(observers);

        if (targetObservation.Generation is null)
        {
            observer = null;
            failureMessage =
                "DocumentCache current target generation is not available for status observation.";
            return false;
        }

        if (
            targetObservation.ProviderToken is null
            || !observers.TryGetValue(targetObservation.ProviderToken, out observer)
        )
        {
            observer = null;
            failureMessage =
                "DocumentCache status current-source observer is not available for the target provider.";
            return false;
        }

        failureMessage = null;
        return true;
    }

    private static ProviderObservationCancellationSource SelectFirstCancellationSource(
        ProviderObservationCancellationSource firstCancellationSource,
        CancellationToken statusObservationTimeoutToken,
        CancellationToken endpointCancellationToken
    )
    {
        if (firstCancellationSource != ProviderObservationCancellationSource.None)
        {
            return firstCancellationSource;
        }

        if (statusObservationTimeoutToken.IsCancellationRequested)
        {
            return ProviderObservationCancellationSource.StatusObservationTimeout;
        }

        if (endpointCancellationToken.IsCancellationRequested)
        {
            return ProviderObservationCancellationSource.EndpointTimeout;
        }

        return ProviderObservationCancellationSource.None;
    }

    private void RecordProviderObservationTelemetry(
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken,
        DocumentCacheStatusCurrentSourceObservationResult result,
        ProviderObservationCancellationSource cancellationSource,
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
                    when cancellationSource
                        == ProviderObservationCancellationSource.StatusObservationTimeout => (
                    DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut,
                    DocumentCacheStatusReason.StatusObservationTimeout
                ),
                DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled
                    when cancellationSource == ProviderObservationCancellationSource.EndpointTimeout => (
                    DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut,
                    DocumentCacheStatusReason.StatusEndpointTimeout
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
        CancellationToken callerCancellationToken,
        ProviderObservationCancellationSource cancellationSource
    )
    {
        callerCancellationToken.ThrowIfCancellationRequested();

        return result.Outcome switch
        {
            // A completed provider statement reports the facts it obtained; a timeout token may
            // be observed as cancelled after the durable statement has already succeeded.
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
                    ToStatusQueuePresence(result.QueuePresence),
                    result.OldestWorkFirstEnqueuedAt,
                    result.OldestWorkAgeSeconds,
                    result.Message
                ),
            DocumentCacheStatusCurrentSourceObservationOutcome.ProviderTimeout =>
                DocumentCacheStatusDurableObservation.ObservationTimeout(result.Message),
            DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled
                when cancellationSource == ProviderObservationCancellationSource.StatusObservationTimeout =>
                DocumentCacheStatusDurableObservation.ObservationTimeout(ObservationTimeoutMessage),
            DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled
                when cancellationSource == ProviderObservationCancellationSource.EndpointTimeout =>
                DocumentCacheStatusDurableObservation.EndpointTimeout(EndpointTimeoutStartedMessage),
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

    private static DocumentCacheStatusQueuePresence? ToStatusQueuePresence(
        DocumentCacheStatusDurableQueuePresence? queuePresence
    ) =>
        queuePresence switch
        {
            DocumentCacheStatusDurableQueuePresence.Empty => DocumentCacheStatusQueuePresence.Empty,
            DocumentCacheStatusDurableQueuePresence.NotEmpty => DocumentCacheStatusQueuePresence.NotEmpty,
            null => null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(queuePresence),
                queuePresence,
                "Unsupported queue presence."
            ),
        };

    private DocumentCacheStatusTarget CreateEndpointTimeoutTarget(
        DocumentCacheTargetObservation targetObservation,
        DateTimeOffset registryObservedAt,
        DateTimeOffset processObservedAt,
        DocumentCacheProjectionObservationSnapshot projectionSnapshot,
        DocumentCacheStatusEvaluationMode evaluationMode,
        string message,
        DocumentCacheProjectionTargetHealthSnapshot? targetHealth = null,
        DocumentCacheStatusRuntimeObservation? runtimeObservation = null
    )
    {
        targetHealth ??= GetCurrentGenerationTargetHealth(targetObservation, projectionSnapshot);
        runtimeObservation ??= ToRuntimeObservation(targetHealth);

        DocumentCacheStatusClassificationResult classification = DocumentCacheStatusClassifier.Classify(
            targetObservation,
            runtimeObservation,
            DocumentCacheStatusDurableObservation.EndpointTimeout(message),
            evaluationMode
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

    private DocumentCacheStatusTarget BuildStatusTarget(
        DocumentCacheTargetObservation targetObservation,
        DateTimeOffset registryObservedAt,
        DateTimeOffset processObservedAt,
        DocumentCacheProjectionObservationSnapshot projectionSnapshot,
        DocumentCacheProjectionTargetHealthSnapshot? targetHealth,
        DocumentCacheStatusClassificationResult classification,
        TimeSpan? providerObservationDuration = null
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
            ToActiveCommand(GetCurrentGenerationActiveCommand(targetObservation, projectionSnapshot)),
            ToLastEndedDiagnostic(
                GetCurrentGenerationEndedAdministrativeCommandDiagnostic(
                    targetObservation,
                    projectionSnapshot
                )
            ),
            ToTargetDiagnostics(targetHealth),
            ToDocumentDiagnostics(targetHealth),
            ToPoisonTraversalDiagnostics(targetHealth),
            DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(targetObservation.EffectiveSettings),
            ToEnqueueFailures(targetObservation.TargetKey)
        );

        _statusTelemetry.RecordStatusObservation(
            targetObservation,
            statusTarget,
            providerObservationDuration
        );
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

        ImmutableArray<DocumentCacheStatusEnqueueFailureCategoryCount> byCategory =
        [
            .. EnqueueFailureCategoryOrder
                .Select(category =>
                    (
                        Category: category,
                        Count: snapshot.RecentEvents.Count(failureEvent => failureEvent.Category == category)
                    )
                )
                .Where(categoryCount => categoryCount.Count > 0)
                .Select(categoryCount => new DocumentCacheStatusEnqueueFailureCategoryCount(
                    ToStatusCategory(categoryCount.Category),
                    categoryCount.Count
                )),
        ];

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

    private static DocumentCacheAdministrativeCommandObservationSnapshot? GetCurrentGenerationActiveCommand(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheProjectionObservationSnapshot projectionSnapshot
    )
    {
        if (targetObservation.Generation is null)
        {
            return null;
        }

        DocumentCacheAdministrativeCommandObservationSnapshot? snapshot =
            projectionSnapshot.GetCurrentGenerationActiveCommand(targetObservation.TargetKey);

        return snapshot?.TargetGeneration == targetObservation.Generation ? snapshot : null;
    }

    private static DocumentCacheAdministrativeCommandEndedDiagnosticSnapshot? GetCurrentGenerationEndedAdministrativeCommandDiagnostic(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheProjectionObservationSnapshot projectionSnapshot
    )
    {
        if (targetObservation.Generation is null)
        {
            return null;
        }

        DocumentCacheAdministrativeCommandEndedDiagnosticSnapshot? snapshot =
            projectionSnapshot.GetCurrentGenerationEndedAdministrativeCommandDiagnostic(
                targetObservation.TargetKey
            );

        return snapshot?.TargetGeneration == targetObservation.Generation ? snapshot : null;
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
            SelectLastFailureAt(targetHealth),
            status switch
            {
                DocumentCacheStatusExecutionState.TargetBackoff =>
                    "Current-generation DocumentCache projection runtime is in target-level backoff.",
                DocumentCacheStatusExecutionState.Cancelling or DocumentCacheStatusExecutionState.Cancelled =>
                    "Current-generation DocumentCache projection runtime is cancelled.",
                _ => null,
            }
        );
    }

    private static DateTimeOffset? SelectLastFailureAt(
        DocumentCacheProjectionTargetHealthSnapshot targetHealth
    )
    {
        return targetHealth
            .TargetDiagnosticEvents.Select(diagnostic => (DateTimeOffset?)diagnostic.ObservedAt)
            .Concat(
                targetHealth.FailureDiagnostics.DocumentDiagnostics.Select(diagnostic =>
                    (DateTimeOffset?)diagnostic.ObservedAt
                )
            )
            .Max();
    }

    private static DocumentCacheStatusExecutionState SelectExecutionStatus(
        DocumentCacheProjectionTargetHealthSnapshot targetHealth
    )
    {
        DocumentCacheProjectionExecutionStateSnapshot executionState = targetHealth.ExecutionState;

        if (executionState.CancellationRequested)
        {
            if (
                executionState.CancellationObservedAt is null
                || executionState.IsActivelyProcessing
                || executionState.IsWaitingForWorkerGate
            )
            {
                return DocumentCacheStatusExecutionState.Cancelling;
            }

            return DocumentCacheStatusExecutionState.Cancelled;
        }

        if (executionState.CancellationObservedAt is not null)
        {
            return DocumentCacheStatusExecutionState.Cancelling;
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
                .PoisonTraversal.DiagnosticEvents.Select(
                    diagnostic => new DocumentCacheStatusPoisonTraversalDiagnosticEvent(
                        diagnostic.DocumentId,
                        diagnostic.ObservedAt,
                        ToPoisonTraversalDiagnosticCategory(diagnostic.Category),
                        diagnostic.NextRetryAt,
                        diagnostic.Message
                    )
                )
                .ToImmutableArray(),
            ToIntEvictionCount(targetHealth.PoisonTraversal.DiagnosticEventEvictionCount)
        );
    }

    private static int SelectMaxDegreeOfParallelism(ImmutableArray<DocumentCacheTargetObservation> targets) =>
        targets.Min(target => target.EffectiveSettings.ProjectorMaxConcurrentTargets);

    private static DocumentCacheStatusResolutionReason ToResolutionReason(
        DocumentCacheTargetDiagnosticCategory? category
    ) =>
        category switch
        {
            null => DocumentCacheStatusResolutionReason.None,
            DocumentCacheTargetDiagnosticCategory.TargetNotConfigured
            or DocumentCacheTargetDiagnosticCategory.TargetUnresolved =>
                DocumentCacheStatusResolutionReason.TargetNotFound,
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

    private static DocumentCacheStatusPoisonTraversalDiagnosticCategory ToPoisonTraversalDiagnosticCategory(
        DocumentCacheProjectionPoisonTraversalDiagnosticCategory category
    ) =>
        category switch
        {
            DocumentCacheProjectionPoisonTraversalDiagnosticCategory.RetryScheduled =>
                DocumentCacheStatusPoisonTraversalDiagnosticCategory.RetryScheduled,
            DocumentCacheProjectionPoisonTraversalDiagnosticCategory.PageCapacityExhausted =>
                DocumentCacheStatusPoisonTraversalDiagnosticCategory.PageCapacityExhausted,
            DocumentCacheProjectionPoisonTraversalDiagnosticCategory.SkippedUntilRetry =>
                DocumentCacheStatusPoisonTraversalDiagnosticCategory.SkippedUntilRetry,
            _ => DocumentCacheStatusPoisonTraversalDiagnosticCategory.SkippedUntilRetry,
        };

    private static int ToIntEvictionCount(long evictionCount) =>
        evictionCount > int.MaxValue ? int.MaxValue : (int)evictionCount;
}

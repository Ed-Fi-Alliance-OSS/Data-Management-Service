// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheAdministrativeCommandRunner
{
    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        IDocumentCacheAdministrativeCommandWorkflow workflow,
        CancellationToken cancellationToken = default
    );
}

internal interface IDocumentCacheAdministrativeCommandWorkflow
{
    Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    );

    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    );
}

internal sealed record DocumentCacheAdministrativeCommandRunnerRequest
{
    public DocumentCacheAdministrativeCommandRunnerRequest(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null,
        DocumentCacheOfflineWriterAdmissionConfirmation? offlineWriterAdmission = null
    )
    {
        if (!Enum.IsDefined(command))
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                "Unsupported administrative command."
            );
        }

        Command = command;
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
        OfflineWriterAdmission = offlineWriterAdmission;
    }

    public DocumentCacheAdministrativeCommand Command { get; }

    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }

    public DocumentCacheOfflineWriterAdmissionConfirmation? OfflineWriterAdmission { get; }

    public static DocumentCacheAdministrativeCommandRunnerRequest From(
        DocumentCacheGuardedNewEmptyActivationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            request.TargetKey,
            request.ExpectedPhysicalSourceFingerprint
        );
    }

    public static DocumentCacheAdministrativeCommandRunnerRequest From(
        DocumentCacheOfflineActivationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            DocumentCacheAdministrativeCommand.OfflineActivation,
            request.TargetKey,
            request.ExpectedPhysicalSourceFingerprint,
            request.OfflineWriterAdmission.Confirmation
        );
    }

    public static DocumentCacheAdministrativeCommandRunnerRequest From(
        DocumentCacheOfflineDeactivationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            request.TargetKey,
            request.ExpectedPhysicalSourceFingerprint,
            request.OfflineWriterAdmission.Confirmation
        );
    }

    public static DocumentCacheAdministrativeCommandRunnerRequest From(
        DocumentCacheOnlineCacheRebuildRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            request.TargetKey,
            request.ExpectedPhysicalSourceFingerprint
        );
    }

    public static DocumentCacheAdministrativeCommandRunnerRequest From(
        DocumentCacheExplicitIntegrityScrubRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
            request.TargetKey,
            request.ExpectedPhysicalSourceFingerprint
        );
    }

    public static DocumentCacheAdministrativeCommandRunnerRequest From(
        DocumentCacheInternalOnlyCacheAheadRecoveryRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            request.TargetKey,
            request.ExpectedPhysicalSourceFingerprint,
            request.OfflineWriterAdmission.Confirmation
        );
    }
}

internal sealed class DocumentCacheAdministrativeCommandExecutionContext
{
    private readonly IDocumentCacheProjectionObservationSink _observationSink;
    private readonly TimeProvider _timeProvider;
    private ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> _phaseDiagnostics = [];

    internal DocumentCacheAdministrativeCommandExecutionContext(
        DocumentCacheAdministrativeCommandExecutionId executionId,
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        IDocumentCacheAdministrativeMutexLease mutexLease,
        IDocumentCacheAdministrativePrimitives primitives,
        IDocumentCacheProjectionObservationSink observationSink,
        TimeProvider timeProvider,
        DateTimeOffset startedAt,
        CancellationToken workflowCancellationToken
    )
    {
        ExecutionId = executionId ?? throw new ArgumentNullException(nameof(executionId));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        MutexLease = mutexLease ?? throw new ArgumentNullException(nameof(mutexLease));
        Primitives = primitives ?? throw new ArgumentNullException(nameof(primitives));
        _observationSink = observationSink ?? throw new ArgumentNullException(nameof(observationSink));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        StartedAt = startedAt;
        WorkflowCancellationToken = workflowCancellationToken;
        CurrentPhase = DocumentCacheAdministrativeCommandPhase.Preflight;
        ObservedLifecycle = targetContext.TargetExecutionContext.Lifecycle;
    }

    public DocumentCacheAdministrativeCommandExecutionId ExecutionId { get; }

    public DocumentCacheAdministrativeCommandRunnerRequest Request { get; }

    public DocumentCacheProjectionTargetRuntimeContext TargetContext { get; }

    public IDocumentCacheAdministrativeMutexLease MutexLease { get; }

    public IDocumentCacheAdministrativePrimitives Primitives { get; }

    public DocumentCacheTargetObservation? LiveTargetObservation { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public CancellationToken WorkflowCancellationToken { get; }

    public DocumentCacheAdministrativeCommandPhase CurrentPhase { get; private set; }

    public DocumentCacheAdministrativeCommandPhase? LastCompletedPhase { get; private set; }

    public bool Mutated { get; private set; }

    public DocumentCacheLifecycleObservation? ObservedLifecycle { get; private set; }

    public TimeSpan ElapsedCommandTime => _timeProvider.GetUtcNow() - StartedAt;

    public ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> PhaseDiagnostics => _phaseDiagnostics;

    public void SetLiveTargetObservation(DocumentCacheTargetObservation liveTargetObservation)
    {
        ArgumentNullException.ThrowIfNull(liveTargetObservation);

        if (!liveTargetObservation.TargetKey.Equals(TargetContext.TargetKey))
        {
            throw new ArgumentException(
                "Live target observation must be bound to the pinned command target.",
                nameof(liveTargetObservation)
            );
        }

        LiveTargetObservation = liveTargetObservation;
        ObservedLifecycle = liveTargetObservation.Lifecycle;
        Observe();
    }

    public void EnterPhase(DocumentCacheAdministrativeCommandPhase phase)
    {
        CurrentPhase = RequireDefinedPhase(phase);
        Observe();
    }

    public void CompletePhase(DocumentCacheAdministrativeCommandPhase phase)
    {
        LastCompletedPhase = RequireDefinedPhase(phase);
        Observe();
    }

    public void MarkMutated(DocumentCacheLifecycleObservation? lifecycle = null)
    {
        Mutated = true;
        if (lifecycle is not null)
        {
            ObservedLifecycle = lifecycle;
        }

        Observe();
    }

    public void AddPhaseDiagnostic(
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message,
        bool retryable = false,
        ImmutableArray<long> affectedDocumentIds = default
    )
    {
        _phaseDiagnostics = _phaseDiagnostics.Add(
            new DocumentCacheAdministrativePhaseDiagnostic(
                CurrentPhase,
                LastCompletedPhase,
                retryable,
                diagnosticCategory,
                affectedDocumentIds,
                message
            )
        );
        Observe();
    }

    public DocumentCacheAdministrativeCommandResult EligiblePreflightResult(
        DocumentCacheDownstreamPublicationStatus? downstreamPublicationStatus = null
    )
    {
        DocumentCacheTargetObservation liveTargetObservation =
            LiveTargetObservation
            ?? throw new InvalidOperationException(
                "Administrative command preflight requires a live target observation."
            );

        return new(
            Request.Command,
            Request.TargetKey,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            liveTargetObservation.Lifecycle?.State,
            liveTargetObservation.Lifecycle?.CacheAheadRecoveryRequired,
            liveTargetObservation.PhysicalSourceFingerprint,
            liveTargetObservation.Generation?.Value,
            downstreamPublicationStatus
        );
    }

    public DocumentCacheAdministrativeCommandResult Completed()
    {
        CompletePhase(DocumentCacheAdministrativeCommandPhase.Complete);

        return new(
            Request.Command,
            Request.TargetKey,
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            Mutated,
            TargetContext.Generation.Value,
            TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
            ObservedLifecycle?.State,
            ObservedLifecycle?.CacheAheadRecoveryRequired,
            _phaseDiagnostics,
            Request.OfflineWriterAdmission,
            ElapsedCommandTime
        );
    }

    public DocumentCacheAdministrativeCommandResult Failed(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message,
        bool retryable,
        ImmutableArray<long> affectedDocumentIds = default
    )
    {
        AddPhaseDiagnostic(diagnosticCategory, message, retryable, affectedDocumentIds);

        return new(
            Request.Command,
            Request.TargetKey,
            status,
            classification,
            Mutated,
            TargetContext.Generation.Value,
            TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
            ObservedLifecycle?.State,
            ObservedLifecycle?.CacheAheadRecoveryRequired,
            _phaseDiagnostics,
            Request.OfflineWriterAdmission,
            ElapsedCommandTime
        );
    }

    internal void Observe()
    {
        _observationSink.ObserveAdministrativeCommand(
            new DocumentCacheAdministrativeCommandObservationSnapshot(
                ExecutionId,
                Request.Command,
                TargetContext.TargetKey,
                TargetContext.Generation,
                TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize,
                TargetContext.TargetExecutionContext.EffectiveSettings.AdministrationWorkflowTimeout,
                StartedAt,
                _timeProvider.GetUtcNow(),
                CurrentPhase,
                LastCompletedPhase,
                Mutated,
                TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                ObservedLifecycle?.State,
                ObservedLifecycle?.CacheAheadRecoveryRequired,
                Request.OfflineWriterAdmission,
                ElapsedCommandTime,
                _phaseDiagnostics
            )
        );
    }

    private static DocumentCacheAdministrativeCommandPhase RequireDefinedPhase(
        DocumentCacheAdministrativeCommandPhase phase
    )
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unsupported command phase.");
        }

        return phase;
    }
}

internal sealed class DocumentCacheAdministrativeCommandRunner(
    IDocumentCacheProjectionSupervisor projectionSupervisor,
    IDocumentCacheTargetRegistry targetRegistry,
    IDocumentCacheAdministrativeMutex administrativeMutex,
    IDocumentCacheAdministrativePrimitives primitives,
    IDocumentCacheProjectionObservationSink observationSink,
    TimeProvider timeProvider,
    ILogger<DocumentCacheAdministrativeCommandRunner> logger
) : IDocumentCacheAdministrativeCommandRunner
{
    public async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        IDocumentCacheAdministrativeCommandWorkflow workflow,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workflow);

        PinnedTargetResolution pinnedTargetResolution = TryResolvePinnedTarget(request);
        if (pinnedTargetResolution.Rejection is not null)
        {
            return pinnedTargetResolution.Rejection;
        }

        DocumentCacheProjectionTargetRuntimeContext targetContext = pinnedTargetResolution.TargetContext!;
        if (administrativeMutex.ProviderToken != targetContext.TargetExecutionContext.ProviderToken)
        {
            return CreateProviderMismatchResult(request, targetContext);
        }

        if (primitives.ProviderToken != targetContext.TargetExecutionContext.ProviderToken)
        {
            return CreateProviderMismatchResult(request, targetContext);
        }

        DocumentCacheAdministrativeCommandExecutionId executionId =
            DocumentCacheAdministrativeCommandExecutionId.New();

        IDocumentCacheAdministrativeMutexLease mutexLease;
        try
        {
            mutexLease = await administrativeMutex
                .AcquireAsync(targetContext.TargetExecutionContext.ConnectionInput, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CreateAcquireMutexFailure(
                request,
                targetContext,
                DocumentCacheAdministrativeCommandClassification.MutexAcquisitionCancelled,
                DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionCancelled,
                "DocumentCache administrative mutex acquisition was cancelled."
            );
        }
        catch (OperationCanceledException)
        {
            return CreateAcquireMutexFailure(
                request,
                targetContext,
                DocumentCacheAdministrativeCommandClassification.MutexAcquisitionCancelled,
                DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionCancelled,
                "DocumentCache administrative mutex acquisition was cancelled by the provider."
            );
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "DocumentCache administrative mutex acquisition failed for command {Command} and target {TargetKey}.",
                request.Command,
                request.TargetKey.TargetKey
            );
            return CreateAcquireMutexFailure(
                request,
                targetContext,
                DocumentCacheAdministrativeCommandClassification.MutexAcquisitionFailed,
                DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionFailed,
                "DocumentCache administrative mutex acquisition failed."
            );
        }

        await using (mutexLease.ConfigureAwait(false))
        using (
            CancellationTokenSource workflowTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            )
        )
        {
            DateTimeOffset startedAt = timeProvider.GetUtcNow();
            workflowTimeout.CancelAfter(
                targetContext.TargetExecutionContext.EffectiveSettings.AdministrationWorkflowTimeout
            );

            DocumentCacheAdministrativeCommandExecutionContext commandContext = new(
                executionId,
                request,
                targetContext,
                mutexLease,
                primitives,
                observationSink,
                timeProvider,
                startedAt,
                workflowTimeout.Token
            );

            commandContext.Observe();

            try
            {
                DocumentCacheAdministrativeCommandResult result = await targetContext
                    .DrainExecutor.RunAdministrativeCommandAsync(
                        async drainCancellationToken =>
                        {
                            using IDisposable commandBinding = targetContext.BindAdministrativeCommand(
                                commandContext
                            );

                            return await ExecutePinnedCommandAsync(
                                    commandContext,
                                    workflow,
                                    drainCancellationToken
                                )
                                .ConfigureAwait(false);
                        },
                        workflowTimeout.Token
                    )
                    .ConfigureAwait(false);

                return AddRuntimeResultFields(result, commandContext);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CreateCancellationResult(commandContext);
            }
            catch (OperationCanceledException) when (workflowTimeout.IsCancellationRequested)
            {
                return CreateWorkflowTimeoutResult(commandContext);
            }
            catch (DocumentCacheAdministrativeMutexSessionLostException exception)
            {
                logger.LogWarning(
                    exception,
                    "DocumentCache administrative mutex session was lost for command {Command} and target {TargetKey}.",
                    request.Command,
                    request.TargetKey.TargetKey
                );
                return CreateSessionLossResult(commandContext);
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning(
                    exception,
                    "DocumentCache administrative provider command timed out for command {Command} and target {TargetKey}.",
                    request.Command,
                    request.TargetKey.TargetKey
                );
                return CreateProviderTimeoutResult(commandContext);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "DocumentCache administrative command {Command} failed unexpectedly for target {TargetKey}.",
                    request.Command,
                    request.TargetKey.TargetKey
                );
                return CreateUnexpectedFailureResult(commandContext);
            }
            finally
            {
                observationSink.EndAdministrativeCommand(executionId);
            }
        }
    }

    private async Task<DocumentCacheAdministrativeCommandResult> ExecutePinnedCommandAsync(
        DocumentCacheAdministrativeCommandExecutionContext commandContext,
        IDocumentCacheAdministrativeCommandWorkflow workflow,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        commandContext.EnterPhase(DocumentCacheAdministrativeCommandPhase.Preflight);

        DocumentCacheAdministrativeCommandResult? liveObservationFailure =
            await ObserveLiveTargetForPreflightAsync(commandContext, cancellationToken).ConfigureAwait(false);
        if (liveObservationFailure is not null)
        {
            return liveObservationFailure;
        }

        DocumentCacheAdministrativeCommandResult preflightResult = await workflow
            .RunPreflightAsync(commandContext, cancellationToken)
            .ConfigureAwait(false);
        if (preflightResult.Classification != DocumentCacheAdministrativeCommandClassification.Succeeded)
        {
            return AddRuntimeResultFields(preflightResult, commandContext);
        }

        commandContext.CompletePhase(DocumentCacheAdministrativeCommandPhase.Preflight);

        return await workflow.ExecuteAsync(commandContext, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DocumentCacheAdministrativeCommandResult?> ObserveLiveTargetForPreflightAsync(
        DocumentCacheAdministrativeCommandExecutionContext commandContext,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheLifecycleReadResult lifecycleReadResult = await primitives
            .ReadLifecycleAsync(
                commandContext.MutexLease.BeginTransactionAsync,
                DocumentCacheAdministrativeStateLockMode.Shared,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!lifecycleReadResult.Succeeded)
        {
            return commandContext.Failed(
                DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleObservationFailure,
                lifecycleReadResult.Message,
                retryable: false
            );
        }

        DocumentCacheTargetExecutionContext executionContext = commandContext
            .TargetContext
            .TargetExecutionContext;
        DocumentCacheTargetObservation liveTargetObservation =
            DocumentCacheTargetObservation.ResolvedEligible(
                executionContext.TargetKey,
                executionContext.EffectiveSettings,
                executionContext.Generation,
                executionContext.ProviderToken,
                executionContext.PhysicalSourceFingerprint,
                lifecycleReadResult.Lifecycle!,
                executionContext.Inventory,
                executionContext.EnqueueTrigger,
                executionContext.SqlServerPrerequisites
            );
        commandContext.SetLiveTargetObservation(liveTargetObservation);

        return null;
    }

    private PinnedTargetResolution TryResolvePinnedTarget(
        DocumentCacheAdministrativeCommandRunnerRequest request
    )
    {
        DocumentCacheTargetKey targetKey = request.TargetKey.TargetKey;
        DocumentCacheTargetObservation? targetObservation = targetRegistry.CurrentSnapshot.GetTarget(
            targetKey
        );
        DocumentCacheProjectionTargetRuntimeContext? targetContext =
            projectionSupervisor.CurrentTargetContexts.FirstOrDefault(context =>
                context.TargetKey.Equals(targetKey)
            );

        if (targetObservation is null && targetContext is null)
        {
            return PinnedTargetResolution.Rejected(CreateTargetNotConfiguredResult(request));
        }

        if (
            targetObservation is not null
            && targetObservation.EligibilityState != DocumentCacheTargetEligibilityState.Eligible
        )
        {
            return PinnedTargetResolution.Rejected(
                CreateTargetObservationRejection(request, targetObservation)
            );
        }

        if (targetContext is null)
        {
            return PinnedTargetResolution.Rejected(CreateTargetNotCurrentResult(request, targetObservation));
        }

        DocumentCacheTargetExecutionContext? currentExecutionContext =
            targetRegistry.CurrentRuntimeSnapshot.GetExecutionContext(targetKey);
        if (currentExecutionContext is null || currentExecutionContext.Generation != targetContext.Generation)
        {
            return PinnedTargetResolution.Rejected(
                CreateTargetReplacedResult(request, targetObservation, targetContext)
            );
        }

        if (
            request.ExpectedPhysicalSourceFingerprint is not null
            && targetContext.TargetExecutionContext.PhysicalSourceFingerprint
                != request.ExpectedPhysicalSourceFingerprint
        )
        {
            return PinnedTargetResolution.Rejected(
                CreateExpectedSourceMismatchResult(request, targetObservation, targetContext)
            );
        }

        if (targetContext.CancellationRequested)
        {
            return PinnedTargetResolution.Rejected(
                CreateTargetReplacedResult(request, targetObservation, targetContext)
            );
        }

        return PinnedTargetResolution.Pinned(targetContext);
    }

    private DocumentCacheAdministrativeCommandResult AddRuntimeResultFields(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        if (result.Command != commandContext.Request.Command)
        {
            throw new InvalidOperationException(
                "Administrative workflow returned a result for another command."
            );
        }

        if (!result.TargetKey.TargetKey.Equals(commandContext.TargetContext.TargetKey))
        {
            throw new InvalidOperationException(
                "Administrative workflow returned a result for another target."
            );
        }

        bool mutated = result.Mutated || commandContext.Mutated;
        DocumentCacheAdministrativeCommandStatus status =
            mutated
            && result.Status
                is DocumentCacheAdministrativeCommandStatus.RejectedNoMutation
                    or DocumentCacheAdministrativeCommandStatus.FailedNoMutation
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : result.Status;

        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> phaseDiagnostics = result
            .PhaseDiagnostics.Concat(commandContext.PhaseDiagnostics)
            .Distinct()
            .ToImmutableArray();

        phaseDiagnostics = AddNoncurrentGenerationDiagnostic(phaseDiagnostics, commandContext, status);

        return new(
            commandContext.Request.Command,
            commandContext.Request.TargetKey,
            status,
            result.Classification,
            mutated,
            commandContext.TargetContext.Generation.Value,
            commandContext.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
            commandContext.ObservedLifecycle?.State ?? result.Lifecycle,
            commandContext.ObservedLifecycle?.CacheAheadRecoveryRequired ?? result.CacheAheadRecoveryRequired,
            phaseDiagnostics,
            commandContext.Request.OfflineWriterAdmission,
            commandContext.ElapsedCommandTime
        );
    }

    private ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> AddNoncurrentGenerationDiagnostic(
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> phaseDiagnostics,
        DocumentCacheAdministrativeCommandExecutionContext commandContext,
        DocumentCacheAdministrativeCommandStatus status
    )
    {
        DocumentCacheTargetExecutionContext? currentContext =
            targetRegistry.CurrentRuntimeSnapshot.GetExecutionContext(commandContext.TargetContext.TargetKey);

        if (
            currentContext is not null
            && currentContext.Generation == commandContext.TargetContext.Generation
        )
        {
            return phaseDiagnostics;
        }

        return phaseDiagnostics.Add(
            new DocumentCacheAdministrativePhaseDiagnostic(
                commandContext.CurrentPhase,
                commandContext.LastCompletedPhase,
                retryable: status == DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                DocumentCacheAdministrativeDiagnosticCategory.TargetReplaced,
                affectedDocumentIds: [],
                "Pinned target generation became noncurrent while the administrative command was running."
            )
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreateCancellationResult(
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        bool mutated = commandContext.Mutated;
        return commandContext.Failed(
            mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            mutated
                ? DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation
                : DocumentCacheAdministrativeCommandClassification.CancellationBeforeMutation,
            DocumentCacheAdministrativeDiagnosticCategory.Cancellation,
            mutated
                ? "Administrative command was cancelled after durable mutation; reissue the same explicit command."
                : "Administrative command was cancelled before durable mutation.",
            retryable: mutated
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreateWorkflowTimeoutResult(
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        bool mutated = commandContext.Mutated;
        return commandContext.Failed(
            mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
            DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout,
            mutated
                ? "Administrative workflow timeout expired after durable mutation; reissue the same explicit command."
                : "Administrative workflow timeout expired before durable mutation.",
            retryable: mutated
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreateSessionLossResult(
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        bool mutated = commandContext.Mutated;
        return commandContext.Failed(
            mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            mutated
                ? DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation
                : DocumentCacheAdministrativeCommandClassification.SessionLossNoMutation,
            DocumentCacheAdministrativeDiagnosticCategory.SessionLoss,
            mutated
                ? "Administrative mutex session was lost after durable mutation."
                : "Administrative mutex session was lost before durable mutation.",
            retryable: mutated
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreateProviderTimeoutResult(
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        bool mutated = commandContext.Mutated;
        return commandContext.Failed(
            mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout,
            DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout,
            mutated
                ? "Provider command timeout occurred after durable mutation."
                : "Provider command timeout occurred before durable mutation.",
            retryable: mutated
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreateUnexpectedFailureResult(
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        bool mutated = commandContext.Mutated;
        return commandContext.Failed(
            mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
            DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
            mutated
                ? "Unexpected provider failure occurred after durable mutation."
                : "Unexpected provider failure occurred before durable mutation.",
            retryable: mutated
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreateAcquireMutexFailure(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message
    ) =>
        new(
            request.Command,
            request.TargetKey,
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            classification,
            mutated: false,
            targetContext.Generation.Value,
            targetContext.TargetExecutionContext.PhysicalSourceFingerprint,
            targetContext.TargetExecutionContext.Lifecycle.State,
            targetContext.TargetExecutionContext.Lifecycle.CacheAheadRecoveryRequired,
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.AcquireMutex,
                    DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                    retryable: false,
                    diagnosticCategory,
                    affectedDocumentIds: [],
                    message
                ),
            ],
            request.OfflineWriterAdmission,
            elapsedCommandTime: null
        );

    private static DocumentCacheAdministrativeCommandResult CreateTargetNotConfiguredResult(
        DocumentCacheAdministrativeCommandRunnerRequest request
    ) =>
        CreatePreMutexRejectedResult(
            request,
            targetObservation: null,
            DocumentCacheAdministrativeCommandClassification.TargetNotConfigured,
            DocumentCacheAdministrativeDiagnosticCategory.TargetNotConfigured,
            "DocumentCache target is not configured in the current process."
        );

    private static DocumentCacheAdministrativeCommandResult CreateTargetNotCurrentResult(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheTargetObservation? targetObservation
    ) =>
        CreatePreMutexRejectedResult(
            request,
            targetObservation,
            DocumentCacheAdministrativeCommandClassification.TargetReplacedBeforeExecution,
            DocumentCacheAdministrativeDiagnosticCategory.TargetReplaced,
            "DocumentCache target has no current runtime context for command execution."
        );

    private static DocumentCacheAdministrativeCommandResult CreateTargetReplacedResult(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheProjectionTargetRuntimeContext targetContext
    ) =>
        CreatePreMutexRejectedResult(
            request,
            targetObservation,
            DocumentCacheAdministrativeCommandClassification.TargetReplacedBeforeExecution,
            DocumentCacheAdministrativeDiagnosticCategory.TargetReplaced,
            "DocumentCache target context generation was replaced before administrative execution.",
            targetContext
        );

    private static DocumentCacheAdministrativeCommandResult CreateExpectedSourceMismatchResult(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheProjectionTargetRuntimeContext targetContext
    ) =>
        CreatePreMutexRejectedResult(
            request,
            targetObservation,
            DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch,
            DocumentCacheAdministrativeDiagnosticCategory.ExpectedSourceMismatch,
            "Expected physical-source fingerprint does not match the pinned target context.",
            targetContext
        );

    private static DocumentCacheAdministrativeCommandResult CreateProviderMismatchResult(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheProjectionTargetRuntimeContext targetContext
    ) =>
        CreatePreMutexRejectedResult(
            request,
            targetObservation: null,
            DocumentCacheAdministrativeCommandClassification.ProviderIneligible,
            DocumentCacheAdministrativeDiagnosticCategory.ProviderIneligible,
            "DocumentCache administrative provider services do not match the pinned target provider.",
            targetContext
        );

    private static DocumentCacheAdministrativeCommandResult CreateTargetObservationRejection(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheTargetObservation targetObservation
    )
    {
        (
            DocumentCacheAdministrativeCommandClassification classification,
            DocumentCacheAdministrativeDiagnosticCategory category,
            string message
        ) = ClassifyTargetObservation(targetObservation);

        ImmutableArray<DocumentCacheAdministrativeDiagnostic> diagnostics = targetObservation
            .Diagnostics.Select(diagnostic => new DocumentCacheAdministrativeDiagnostic(
                diagnostic.Category,
                diagnostic.Message
            ))
            .ToImmutableArray();

        return CreatePreMutexRejectedResult(
            request,
            targetObservation,
            classification,
            category,
            message,
            targetContext: null,
            diagnostics
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreatePreMutexRejectedResult(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message,
        DocumentCacheProjectionTargetRuntimeContext? targetContext = null,
        ImmutableArray<DocumentCacheAdministrativeDiagnostic> diagnostics = default
    )
    {
        ImmutableArray<DocumentCacheAdministrativeDiagnostic> resultDiagnostics = diagnostics.IsDefaultOrEmpty
            ? [new DocumentCacheAdministrativeDiagnostic(diagnosticCategory, message)]
            : diagnostics;

        DocumentCacheTargetExecutionContext? executionContext = targetContext?.TargetExecutionContext;

        return new(
            request.Command,
            request.TargetKey,
            classification,
            targetObservation?.Lifecycle?.State ?? executionContext?.Lifecycle.State,
            targetObservation?.Lifecycle?.CacheAheadRecoveryRequired
                ?? executionContext?.Lifecycle.CacheAheadRecoveryRequired,
            targetObservation?.PhysicalSourceFingerprint ?? executionContext?.PhysicalSourceFingerprint,
            targetObservation?.Generation?.Value ?? executionContext?.Generation.Value,
            downstreamPublicationStatus: null,
            resultDiagnostics
        );
    }

    private static (
        DocumentCacheAdministrativeCommandClassification Classification,
        DocumentCacheAdministrativeDiagnosticCategory Category,
        string Message
    ) ClassifyTargetObservation(DocumentCacheTargetObservation targetObservation)
    {
        if (
            targetObservation.ResolutionState
            is DocumentCacheTargetResolutionState.Configured
                or DocumentCacheTargetResolutionState.Unresolved
        )
        {
            return (
                DocumentCacheAdministrativeCommandClassification.TargetUnresolved,
                DocumentCacheAdministrativeDiagnosticCategory.TargetUnresolved,
                "DocumentCache target is not resolved."
            );
        }

        ImmutableHashSet<DocumentCacheTargetDiagnosticCategory> categories = targetObservation
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .ToImmutableHashSet();

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident))
        {
            return (
                DocumentCacheAdministrativeCommandClassification.UnsupportedPrerequisiteIncident,
                DocumentCacheAdministrativeDiagnosticCategory.UnsupportedPrerequisiteIncident,
                "Provider prerequisite failure was observed outside the supported lifecycle."
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed))
        {
            return (
                DocumentCacheAdministrativeCommandClassification.ProviderPrerequisiteFailed,
                DocumentCacheAdministrativeDiagnosticCategory.ProviderPrerequisiteFailed,
                "Provider prerequisite failed for the target context."
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing))
        {
            return (
                DocumentCacheAdministrativeCommandClassification.ProviderMetadataMissing,
                DocumentCacheAdministrativeDiagnosticCategory.ProviderMetadataMissing,
                "DocumentCache target is missing relational provider metadata."
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown))
        {
            return (
                DocumentCacheAdministrativeCommandClassification.ProviderMetadataUnknown,
                DocumentCacheAdministrativeDiagnosticCategory.ProviderMetadataUnknown,
                "DocumentCache target has unknown relational provider metadata."
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ProviderMismatch))
        {
            return (
                DocumentCacheAdministrativeCommandClassification.ProviderMismatch,
                DocumentCacheAdministrativeDiagnosticCategory.ProviderMismatch,
                "DocumentCache target provider does not match this DMS process provider."
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing))
        {
            return (
                DocumentCacheAdministrativeCommandClassification.ConnectionInputMissing,
                DocumentCacheAdministrativeDiagnosticCategory.ConnectionInputMissing,
                "DocumentCache target has no usable connection input."
            );
        }

        if (
            categories.Overlaps([
                DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
                DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure,
            ])
            || targetObservation.Inventory?.Status is not null and not DocumentCacheInventoryStatus.Satisfied
            || targetObservation.EnqueueTrigger?.Status
                is not null
                    and not DocumentCacheEnqueueTriggerStatus.Satisfied
        )
        {
            return (
                DocumentCacheAdministrativeCommandClassification.MissingOrInvalidInventory,
                DocumentCacheAdministrativeDiagnosticCategory.InventoryFailure,
                "DocumentCache target inventory is missing or invalid."
            );
        }

        return (
            DocumentCacheAdministrativeCommandClassification.ProviderIneligible,
            DocumentCacheAdministrativeDiagnosticCategory.ProviderIneligible,
            "DocumentCache target is not eligible for administrative command execution."
        );
    }

    private sealed record PinnedTargetResolution(
        DocumentCacheProjectionTargetRuntimeContext? TargetContext,
        DocumentCacheAdministrativeCommandResult? Rejection
    )
    {
        public static PinnedTargetResolution Pinned(
            DocumentCacheProjectionTargetRuntimeContext targetContext
        ) => new(targetContext, Rejection: null);

        public static PinnedTargetResolution Rejected(DocumentCacheAdministrativeCommandResult result) =>
            new(TargetContext: null, result);
    }
}

file static class DocumentCacheAdministrativeCommandPrimitiveExtensions
{
    public static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        this IDocumentCacheAdministrativePrimitives primitives,
        Func<
            System.Data.IsolationLevel,
            CancellationToken,
            Task<IRelationalWriteSession>
        > beginTransactionAsync,
        DocumentCacheAdministrativeStateLockMode lockMode,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentNullException.ThrowIfNull(beginTransactionAsync);

        await using IRelationalWriteSession session = await beginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted,
                cancellationToken
            )
            .ConfigureAwait(false);

        try
        {
            DocumentCacheLifecycleReadResult result = await primitives
                .ReadLifecycleAsync(session, lockMode, cancellationToken)
                .ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

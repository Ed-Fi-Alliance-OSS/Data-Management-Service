// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
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
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission = null
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

    public DocumentCacheOfflineWriterAdmission? OfflineWriterAdmission { get; }

    public DocumentCacheOfflineWriterAdmissionConfirmation? AcceptedOfflineWriterAdmissionConfirmation =>
        DocumentCachePreflightClassifier.AcceptedOfflineWriterAdmissionConfirmation(
            Command,
            OfflineWriterAdmission
        );

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
            request.OfflineWriterAdmission
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
            request.OfflineWriterAdmission
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
            request.OfflineWriterAdmission
        );
    }
}

internal sealed class DocumentCacheAdministrativeCommandExecutionContext
{
    private readonly IDocumentCacheProjectionObservationSink _observationSink;
    private readonly IDocumentCacheProjectionTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;
    private ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> _phaseDiagnostics = [];
    private DocumentCacheDownstreamPublicationStatus? _acceptedDownstreamPublicationStatus;

    internal DocumentCacheAdministrativeCommandExecutionContext(
        DocumentCacheAdministrativeCommandExecutionId executionId,
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        IDocumentCacheAdministrativeMutexLease mutexLease,
        IDocumentCacheAdministrativePrimitives primitives,
        IDocumentCacheProjectionObservationSink observationSink,
        TimeProvider timeProvider,
        DateTimeOffset startedAt,
        CancellationToken workflowCancellationToken,
        IDocumentCacheProjectionTelemetry? telemetry = null,
        DeadlockRetrySettings? providerConcurrencyRetrySettings = null,
        IRelationalWriteExceptionClassifier? writeExceptionClassifier = null
    )
    {
        ExecutionId = executionId ?? throw new ArgumentNullException(nameof(executionId));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        MutexLease = mutexLease ?? throw new ArgumentNullException(nameof(mutexLease));
        Primitives = primitives ?? throw new ArgumentNullException(nameof(primitives));
        _observationSink = observationSink ?? throw new ArgumentNullException(nameof(observationSink));
        _telemetry = telemetry ?? NoOpDocumentCacheProjectionTelemetry.Instance;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        StartedAt = startedAt;
        WorkflowCancellationToken = workflowCancellationToken;
        ProviderConcurrencyRetrySettings = providerConcurrencyRetrySettings ?? new DeadlockRetrySettings();
        WriteExceptionClassifier = writeExceptionClassifier ?? new NoOpRelationalWriteExceptionClassifier();
        CurrentPhase = DocumentCacheAdministrativeCommandPhase.Preflight;
        LifecycleObservation = targetContext.TargetExecutionContext.Lifecycle;
    }

    public DocumentCacheAdministrativeCommandExecutionId ExecutionId { get; }

    public DocumentCacheAdministrativeCommandRunnerRequest Request { get; }

    public DocumentCacheProjectionTargetRuntimeContext TargetContext { get; }

    public IDocumentCacheAdministrativeMutexLease MutexLease { get; }

    public IDocumentCacheAdministrativePrimitives Primitives { get; }

    public DocumentCacheTargetObservation? LiveTargetObservation { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public CancellationToken WorkflowCancellationToken { get; }

    public DeadlockRetrySettings ProviderConcurrencyRetrySettings { get; }

    public IRelationalWriteExceptionClassifier WriteExceptionClassifier { get; }

    public DocumentCacheAdministrativeCommandPhase CurrentPhase { get; private set; }

    public DocumentCacheAdministrativeCommandPhase? LastCompletedPhase { get; private set; }

    public bool Mutated { get; private set; }

    public DocumentCacheLifecycleObservation? LifecycleObservation { get; private set; }

    public TimeSpan ElapsedCommandTime => _timeProvider.GetUtcNow() - StartedAt;

    public ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> PhaseDiagnostics => _phaseDiagnostics;

    public int PhaseDiagnosticCapacity =>
        TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize;

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
        LifecycleObservation = liveTargetObservation.Lifecycle;
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
            LifecycleObservation = lifecycle;
        }

        Observe();
        _telemetry.RecordAdministrativeCommandMutation(
            CreateObservationSnapshot(),
            TargetContext.TargetExecutionContext.ProviderToken
        );
    }

    public void AcceptPreflightResult(DocumentCacheAdministrativeCommandResult preflightResult)
    {
        ArgumentNullException.ThrowIfNull(preflightResult);

        if (preflightResult.Command != Request.Command)
        {
            throw new InvalidOperationException(
                "Administrative command preflight returned a result for another command."
            );
        }

        if (!preflightResult.TargetKey.TargetKey.Equals(TargetContext.TargetKey))
        {
            throw new InvalidOperationException(
                "Administrative command preflight returned a result for another target."
            );
        }

        if (preflightResult.Classification != DocumentCacheAdministrativeCommandClassification.Succeeded)
        {
            throw new InvalidOperationException(
                "Administrative command execution requires a successful preflight result."
            );
        }

        _acceptedDownstreamPublicationStatus = preflightResult.DownstreamPublicationStatus;
    }

    public DocumentCacheDownstreamPublicationStatus RequireAcceptedDownstreamPublicationStatus() =>
        _acceptedDownstreamPublicationStatus
        ?? throw new InvalidOperationException(
            "Administrative command execution requires successful downstream-publication preflight proof."
        );

    public void AddPhaseDiagnostic(
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message,
        bool retryable = false,
        ImmutableArray<long> affectedDocumentIds = default
    )
    {
        _phaseDiagnostics = DocumentCacheProjectionObservationBounds.AppendPhaseDiagnostic(
            _phaseDiagnostics,
            new DocumentCacheAdministrativePhaseDiagnostic(
                CurrentPhase,
                LastCompletedPhase,
                retryable,
                diagnosticCategory,
                affectedDocumentIds,
                message
            ),
            PhaseDiagnosticCapacity
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
            LifecycleObservation?.State,
            LifecycleObservation?.CacheAheadRecoveryRequired,
            _phaseDiagnostics,
            Request.AcceptedOfflineWriterAdmissionConfirmation,
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
            LifecycleObservation?.State,
            LifecycleObservation?.CacheAheadRecoveryRequired,
            _phaseDiagnostics,
            Request.AcceptedOfflineWriterAdmissionConfirmation,
            ElapsedCommandTime
        );
    }

    internal void Observe()
    {
        DocumentCacheAdministrativeCommandObservationSnapshot snapshot = CreateObservationSnapshot();
        _observationSink.ObserveAdministrativeCommand(snapshot);
        _telemetry.RecordAdministrativeCommandObservation(
            snapshot,
            TargetContext.TargetExecutionContext.ProviderToken
        );
    }

    private DocumentCacheAdministrativeCommandObservationSnapshot CreateObservationSnapshot() =>
        new(
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
            LifecycleObservation?.State,
            LifecycleObservation?.CacheAheadRecoveryRequired,
            Request.AcceptedOfflineWriterAdmissionConfirmation,
            ElapsedCommandTime,
            _phaseDiagnostics
        );

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
    ILogger<DocumentCacheAdministrativeCommandRunner> logger,
    IDocumentCacheProjectionTelemetry? telemetry = null,
    DeadlockRetrySettings? providerConcurrencyRetrySettings = null,
    IRelationalWriteExceptionClassifier? writeExceptionClassifier = null
) : IDocumentCacheAdministrativeCommandRunner
{
    private readonly IDocumentCacheProjectionTelemetry _telemetry =
        telemetry ?? NoOpDocumentCacheProjectionTelemetry.Instance;
    private readonly DeadlockRetrySettings _providerConcurrencyRetrySettings =
        providerConcurrencyRetrySettings ?? new DeadlockRetrySettings();
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier =
        writeExceptionClassifier ?? new NoOpRelationalWriteExceptionClassifier();

    public async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        IDocumentCacheAdministrativeCommandWorkflow workflow,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workflow);

        DocumentCacheAdministrativeCommandResult? offlineWriterAdmissionRejection =
            DocumentCachePreflightClassifier.ClassifyOfflineWriterAdmission(
                request.Command,
                request.TargetKey,
                request.OfflineWriterAdmission
            );
        if (offlineWriterAdmissionRejection is not null)
        {
            return RecordAdministrativeCommandResult(offlineWriterAdmissionRejection);
        }

        PinnedTargetResolution pinnedTargetResolution = await TryResolveAndRetainPinnedTargetAsync(request)
            .ConfigureAwait(false);
        if (pinnedTargetResolution.Rejection is not null)
        {
            return RecordAdministrativeCommandResult(pinnedTargetResolution.Rejection);
        }

        DocumentCacheProjectionTargetRuntimeContext targetContext = pinnedTargetResolution.TargetContext!;
        IDisposable pinnedTargetRetention = pinnedTargetResolution.TargetRetention!;
        DocumentCacheAdministrativeCommandResult? classifiedResult = null;
        try
        {
            if (administrativeMutex.ProviderToken != targetContext.TargetExecutionContext.ProviderToken)
            {
                classifiedResult = RecordAdministrativeCommandResult(
                    CreateProviderMismatchResult(request, targetContext),
                    targetContext
                );
                return classifiedResult;
            }

            if (primitives.ProviderToken != targetContext.TargetExecutionContext.ProviderToken)
            {
                classifiedResult = RecordAdministrativeCommandResult(
                    CreateProviderMismatchResult(request, targetContext),
                    targetContext
                );
                return classifiedResult;
            }

            DocumentCacheAdministrativeCommandExecutionId executionId =
                DocumentCacheAdministrativeCommandExecutionId.New();

            IDocumentCacheAdministrativeMutexLease mutexLease;
            long mutexStartedAt = Stopwatch.GetTimestamp();
            try
            {
                mutexLease = await administrativeMutex
                    .AcquireAsync(targetContext.TargetExecutionContext.ConnectionInput, cancellationToken)
                    .ConfigureAwait(false);
                RecordAdministrativeMutexOutcome(
                    request,
                    targetContext,
                    DocumentCacheAdministrativeCommandClassification.Succeeded.ToString(),
                    category: null,
                    mutexStartedAt
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RecordAdministrativeMutexOutcome(
                    request,
                    targetContext,
                    DocumentCacheAdministrativeCommandClassification.MutexAcquisitionCancelled.ToString(),
                    DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionCancelled,
                    mutexStartedAt
                );
                classifiedResult = RecordAdministrativeCommandResult(
                    CreateAcquireMutexFailure(
                        request,
                        targetContext,
                        DocumentCacheAdministrativeCommandClassification.MutexAcquisitionCancelled,
                        DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionCancelled,
                        "DocumentCache administrative mutex acquisition was cancelled."
                    ),
                    targetContext
                );
                return classifiedResult;
            }
            catch (OperationCanceledException)
            {
                RecordAdministrativeMutexOutcome(
                    request,
                    targetContext,
                    DocumentCacheAdministrativeCommandClassification.MutexAcquisitionCancelled.ToString(),
                    DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionCancelled,
                    mutexStartedAt
                );
                classifiedResult = RecordAdministrativeCommandResult(
                    CreateAcquireMutexFailure(
                        request,
                        targetContext,
                        DocumentCacheAdministrativeCommandClassification.MutexAcquisitionCancelled,
                        DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionCancelled,
                        "DocumentCache administrative mutex acquisition was cancelled by the provider."
                    ),
                    targetContext,
                    DocumentCacheAdministrativeCommandPhase.AcquireMutex
                );
                return classifiedResult;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "DocumentCache administrative mutex acquisition failed for command {Command} and target {TargetKey}.",
                    request.Command,
                    LoggingSanitizer.SanitizeForLogging(request.TargetKey.TargetKey.ToString())
                );
                RecordAdministrativeMutexOutcome(
                    request,
                    targetContext,
                    DocumentCacheAdministrativeCommandClassification.MutexAcquisitionFailed.ToString(),
                    DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionFailed,
                    mutexStartedAt
                );
                classifiedResult = RecordAdministrativeCommandResult(
                    CreateAcquireMutexFailure(
                        request,
                        targetContext,
                        DocumentCacheAdministrativeCommandClassification.MutexAcquisitionFailed,
                        DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionFailed,
                        "DocumentCache administrative mutex acquisition failed."
                    ),
                    targetContext
                );
                return classifiedResult;
            }

            try
            {
                using CancellationTokenSource workflowTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                    workflowTimeout.Token,
                    _telemetry,
                    _providerConcurrencyRetrySettings,
                    _writeExceptionClassifier
                );

                IDisposable activeCommandTracking = targetContext.TrackActiveAdministrativeCommand(
                    commandContext
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

                    classifiedResult = RecordAdministrativeCommandResult(
                        AddRuntimeResultFields(result, commandContext),
                        commandContext
                    );
                    return classifiedResult;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    classifiedResult = RecordAdministrativeCommandResult(
                        CreateCancellationResult(commandContext),
                        commandContext
                    );
                    return classifiedResult;
                }
                catch (OperationCanceledException) when (workflowTimeout.IsCancellationRequested)
                {
                    classifiedResult = RecordAdministrativeCommandResult(
                        CreateWorkflowTimeoutResult(commandContext),
                        commandContext
                    );
                    return classifiedResult;
                }
                catch (DocumentCacheAdministrativeMutexSessionLostException exception)
                {
                    logger.LogWarning(
                        exception,
                        "DocumentCache administrative mutex session was lost for command {Command} and target {TargetKey}.",
                        request.Command,
                        LoggingSanitizer.SanitizeForLogging(request.TargetKey.TargetKey.ToString())
                    );
                    classifiedResult = RecordAdministrativeCommandResult(
                        CreateSessionLossResult(commandContext),
                        commandContext
                    );
                    return classifiedResult;
                }
                catch (Exception exception)
                    when (DocumentCacheProviderCommandTimeoutClassifier.IsProviderCommandTimeout(exception))
                {
                    logger.LogWarning(
                        exception,
                        "DocumentCache administrative provider command timed out for command {Command} and target {TargetKey}.",
                        request.Command,
                        LoggingSanitizer.SanitizeForLogging(request.TargetKey.TargetKey.ToString())
                    );
                    classifiedResult = RecordAdministrativeCommandResult(
                        CreateProviderTimeoutResult(commandContext),
                        commandContext
                    );
                    return classifiedResult;
                }
                catch (DocumentCacheAdministrativeProviderConcurrencyRetryExhaustedException exception)
                {
                    logger.LogWarning(
                        exception,
                        "DocumentCache administrative provider concurrency retry budget was exhausted after {AttemptCount} attempts for command {Command}, target {TargetKey}, and provider {Provider}.",
                        exception.AttemptCount,
                        request.Command,
                        LoggingSanitizer.SanitizeForLogging(request.TargetKey.TargetKey.ToString()),
                        LoggingSanitizer.SanitizeForLogging(exception.ProviderToken.Value)
                    );
                    classifiedResult = RecordAdministrativeCommandResult(
                        CreateProviderConcurrencyRetryExhaustedResult(commandContext, exception.AttemptCount),
                        commandContext
                    );
                    return classifiedResult;
                }
                catch (Exception exception)
                    when (DocumentCacheAdministrativeWorkflow.IsSessionLoss(
                            commandContext.MutexLease,
                            exception
                        )
                    )
                {
                    logger.LogWarning(
                        exception,
                        "DocumentCache administrative mutex session was lost for command {Command} and target {TargetKey}.",
                        request.Command,
                        LoggingSanitizer.SanitizeForLogging(request.TargetKey.TargetKey.ToString())
                    );
                    classifiedResult = RecordAdministrativeCommandResult(
                        CreateSessionLossResult(commandContext),
                        commandContext
                    );
                    return classifiedResult;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "DocumentCache administrative command {Command} failed unexpectedly for target {TargetKey}.",
                        request.Command,
                        LoggingSanitizer.SanitizeForLogging(request.TargetKey.TargetKey.ToString())
                    );
                    classifiedResult = RecordAdministrativeCommandResult(
                        CreateUnexpectedFailureResult(commandContext),
                        commandContext
                    );
                    return classifiedResult;
                }
                finally
                {
                    activeCommandTracking.Dispose();
                    EndAdministrativeCommandAfterCommand(
                        executionId,
                        request,
                        targetContext,
                        classifiedResult is not null
                    );
                }
            }
            finally
            {
                await DisposeMutexLeaseAfterCommandAsync(
                        mutexLease,
                        request,
                        targetContext,
                        classifiedResult is not null
                    )
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            pinnedTargetRetention.Dispose();
            if (
                projectionSupervisor
                is IDocumentCacheProjectionRetainedTargetContextReleaser retainedTargetContextReleaser
            )
            {
                await ReleaseRetainedCommandOwnedTargetContextAfterCommandAsync(
                        retainedTargetContextReleaser,
                        request,
                        targetContext,
                        classifiedResult is not null
                    )
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task DisposeMutexLeaseAfterCommandAsync(
        IDocumentCacheAdministrativeMutexLease mutexLease,
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        bool hasClassifiedResult
    )
    {
        try
        {
            await mutexLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "DocumentCache administrative mutex cleanup failed after command {Command} and target {TargetKey}. ClassifiedResultPreserved: {ClassifiedResultPreserved}.",
                request.Command,
                LoggingSanitizer.SanitizeForLogging(targetContext.TargetKey.ToString()),
                hasClassifiedResult
            );
        }
    }

    private void EndAdministrativeCommandAfterCommand(
        DocumentCacheAdministrativeCommandExecutionId executionId,
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        bool hasClassifiedResult
    )
    {
        try
        {
            observationSink.EndAdministrativeCommand(executionId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "DocumentCache administrative command observation cleanup failed after command {Command}, target {TargetKey}, generation {TargetGeneration}, and execution {ExecutionId}. ClassifiedResultPreserved: {ClassifiedResultPreserved}.",
                request.Command,
                LoggingSanitizer.SanitizeForLogging(targetContext.TargetKey.ToString()),
                targetContext.Generation.Value,
                LoggingSanitizer.SanitizeForLogging(executionId.ToString()),
                hasClassifiedResult
            );
        }
    }

    private async Task ReleaseRetainedCommandOwnedTargetContextAfterCommandAsync(
        IDocumentCacheProjectionRetainedTargetContextReleaser retainedTargetContextReleaser,
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        bool hasClassifiedResult
    )
    {
        try
        {
            await retainedTargetContextReleaser
                .ReleaseRetainedCommandOwnedTargetContextAsync(targetContext, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "DocumentCache administrative retained target-context cleanup failed after command {Command}, target {TargetKey}, and generation {TargetGeneration}. ClassifiedResultPreserved: {ClassifiedResultPreserved}.",
                request.Command,
                LoggingSanitizer.SanitizeForLogging(targetContext.TargetKey.ToString()),
                targetContext.Generation.Value,
                hasClassifiedResult
            );
        }
    }

    private DocumentCacheAdministrativeCommandResult RecordAdministrativeCommandResult(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheProjectionTargetRuntimeContext? targetContext = null,
        DocumentCacheAdministrativeCommandPhase? currentPhase = null
    )
    {
        _telemetry.RecordAdministrativeCommandResult(
            result,
            targetContext?.TargetExecutionContext.ProviderToken,
            targetContext?.TargetExecutionContext.EffectiveSettings.AdministrationWorkflowTimeout,
            currentPhase
        );
        return result;
    }

    private DocumentCacheAdministrativeCommandResult RecordAdministrativeCommandResult(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        _telemetry.RecordAdministrativeCommandResult(
            result,
            commandContext.TargetContext.TargetExecutionContext.ProviderToken,
            commandContext
                .TargetContext
                .TargetExecutionContext
                .EffectiveSettings
                .AdministrationWorkflowTimeout,
            commandContext.CurrentPhase
        );
        return result;
    }

    private void RecordAdministrativeMutexOutcome(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        string outcome,
        DocumentCacheAdministrativeDiagnosticCategory? category,
        long mutexStartedAt
    ) =>
        _telemetry.RecordAdministrativeMutexOutcome(
            request.Command,
            targetContext.TargetKey,
            targetContext.TargetExecutionContext.ProviderToken,
            outcome,
            category,
            DocumentCacheProjectionTelemetry.GetElapsedTime(mutexStartedAt)
        );

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
            return preflightResult;
        }

        commandContext.AcceptPreflightResult(preflightResult);
        commandContext.CompletePhase(DocumentCacheAdministrativeCommandPhase.Preflight);

        return await workflow.ExecuteAsync(commandContext, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DocumentCacheAdministrativeCommandResult?> ObserveLiveTargetForPreflightAsync(
        DocumentCacheAdministrativeCommandExecutionContext commandContext,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheLifecycleReadResult lifecycleReadResult = await DocumentCacheAdministrativeWorkflow
            .ExecuteInTransactionAsync(
                commandContext.MutexLease,
                System.Data.IsolationLevel.ReadCommitted,
                (session, transactionCancellationToken) =>
                    primitives.ReadLifecycleAsync(
                        session,
                        DocumentCacheAdministrativeStateLockMode.Shared,
                        transactionCancellationToken
                    ),
                commit: true,
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

        commandContext.SetLiveTargetObservation(
            DocumentCacheAdministrativeLiveTargetObservation.Create(
                commandContext,
                lifecycleReadResult.Lifecycle!
            )
        );

        return null;
    }

    private async Task<PinnedTargetResolution> TryResolveAndRetainPinnedTargetAsync(
        DocumentCacheAdministrativeCommandRunnerRequest request
    )
    {
        if (projectionSupervisor is IDocumentCacheProjectionAdministrativeTargetRetainer targetRetainer)
        {
            DocumentCacheProjectionAdministrativeTargetRetainResult retainResult = await targetRetainer
                .TryRetainCurrentTargetForAdministrativeCommandAsync(
                    request.TargetKey.TargetKey,
                    CancellationToken.None
                )
                .ConfigureAwait(false);

            return ClassifyRetainedPinnedTarget(request, retainResult);
        }

        PinnedTargetResolution resolution = TryResolvePinnedTarget(request);
        if (resolution.Rejection is not null)
        {
            return resolution;
        }

        DocumentCacheProjectionTargetRuntimeContext targetContext = resolution.TargetContext!;
        IDisposable? targetRetention = targetContext.TryRetainForAdministrativeCommand();
        if (targetRetention is null)
        {
            DocumentCacheTargetObservation? targetObservation = targetRegistry.CurrentSnapshot.GetTarget(
                request.TargetKey.TargetKey
            );

            return PinnedTargetResolution.Rejected(
                CreateTargetReplacedResult(request, targetObservation, targetContext)
            );
        }

        return PinnedTargetResolution.Pinned(targetContext, targetRetention);
    }

    private static PinnedTargetResolution ClassifyRetainedPinnedTarget(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheProjectionAdministrativeTargetRetainResult retainResult
    )
    {
        IDisposable? targetRetention = retainResult.Retention;

        PinnedTargetResolution resolution = ClassifyPinnedTarget(
            request,
            retainResult.TargetObservation,
            retainResult.TargetContext,
            targetRetention
        );

        if (resolution.Rejection is not null)
        {
            targetRetention?.Dispose();
        }

        return resolution;
    }

    private static PinnedTargetResolution ClassifyPinnedTarget(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheProjectionTargetRuntimeContext? targetContext,
        IDisposable? targetRetention
    )
    {
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

        if (targetRetention is null)
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

        return PinnedTargetResolution.Pinned(targetContext, targetRetention);
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

        return new PinnedTargetResolution(targetContext, TargetRetention: null, Rejection: null);
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

        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> phaseDiagnostics =
            DocumentCacheProjectionObservationBounds.CapPhaseDiagnostics(
                MergePhaseDiagnostics(result.PhaseDiagnostics, commandContext),
                commandContext.PhaseDiagnosticCapacity
            );

        phaseDiagnostics = AddNoncurrentGenerationDiagnostic(phaseDiagnostics, commandContext, status);

        return new(
            commandContext.Request.Command,
            commandContext.Request.TargetKey,
            status,
            result.Classification,
            mutated,
            commandContext.TargetContext.Generation.Value,
            commandContext.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
            commandContext.LifecycleObservation?.State ?? result.Lifecycle,
            commandContext.LifecycleObservation?.CacheAheadRecoveryRequired
                ?? result.CacheAheadRecoveryRequired,
            phaseDiagnostics,
            commandContext.Request.AcceptedOfflineWriterAdmissionConfirmation,
            commandContext.ElapsedCommandTime
        );
    }

    private static ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> MergePhaseDiagnostics(
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> resultDiagnostics,
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> contextDiagnostics =
            commandContext.PhaseDiagnostics;
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> normalizedResultDiagnostics =
            resultDiagnostics.IsDefault ? [] : resultDiagnostics;

        if (contextDiagnostics.IsDefaultOrEmpty)
        {
            return normalizedResultDiagnostics;
        }

        if (normalizedResultDiagnostics.IsDefaultOrEmpty)
        {
            return contextDiagnostics;
        }

        if (normalizedResultDiagnostics.Equals(contextDiagnostics))
        {
            return normalizedResultDiagnostics;
        }

        return normalizedResultDiagnostics.Concat(contextDiagnostics).ToImmutableArray();
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

        return DocumentCacheProjectionObservationBounds.AppendPhaseDiagnostic(
            phaseDiagnostics,
            new DocumentCacheAdministrativePhaseDiagnostic(
                commandContext.CurrentPhase,
                commandContext.LastCompletedPhase,
                retryable: status == DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                DocumentCacheAdministrativeDiagnosticCategory.TargetReplaced,
                affectedDocumentIds: [],
                "Pinned target generation became noncurrent while the administrative command was running."
            ),
            commandContext.PhaseDiagnosticCapacity
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

    private static DocumentCacheAdministrativeCommandResult CreateProviderConcurrencyRetryExhaustedResult(
        DocumentCacheAdministrativeCommandExecutionContext commandContext,
        int attemptCount
    )
    {
        bool mutated = commandContext.Mutated;
        return commandContext.Failed(
            mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.ProviderConcurrencyRetryExhausted,
            DocumentCacheAdministrativeDiagnosticCategory.ProviderConcurrencyRetryExhausted,
            $"Provider concurrency retry budget was exhausted after {attemptCount} transaction attempts.",
            retryable: true
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
            request.AcceptedOfflineWriterAdmissionConfirmation,
            elapsedCommandTime: null
        );

    private static DocumentCacheAdministrativeCommandResult CreateTargetNotConfiguredResult(
        DocumentCacheAdministrativeCommandRunnerRequest request
    ) =>
        DocumentCachePreflightClassifier.ClassifyTargetObservationFailure(
            request.Command,
            request.TargetKey,
            targetObservation: null
        ) ?? throw new InvalidOperationException("Target-not-configured classification is required.");

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
    ) =>
        DocumentCachePreflightClassifier.ClassifyTargetObservationFailure(
            request.Command,
            request.TargetKey,
            targetObservation
        ) ?? throw new InvalidOperationException("Ineligible target-observation classification is required.");

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

    private sealed record PinnedTargetResolution(
        DocumentCacheProjectionTargetRuntimeContext? TargetContext,
        IDisposable? TargetRetention,
        DocumentCacheAdministrativeCommandResult? Rejection
    )
    {
        public static PinnedTargetResolution Pinned(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            IDisposable targetRetention
        ) => new(targetContext, targetRetention, Rejection: null);

        public static PinnedTargetResolution Rejected(DocumentCacheAdministrativeCommandResult result) =>
            new(TargetContext: null, TargetRetention: null, result);
    }
}

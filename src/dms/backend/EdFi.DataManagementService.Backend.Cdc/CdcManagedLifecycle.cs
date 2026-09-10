// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public enum CdcManagedLifecycleOperation
{
    Stop,
    Start,
    Restart,
    Resume,
}

public enum CdcManagedLifecycleBoundary
{
    Unverified,
    VerifiedManagedStop,
    NativeRecovery,
}

/// <summary>
/// Historical result for this invocation and target only. A worker may be stopped only after every
/// managed connector on it has verified shutdown. Ready is post-admission observation, not writer
/// publication authorization or certification of an unobserved recovery interval.
/// </summary>
public sealed record CdcManagedLifecycleResult(
    CdcManagedLifecycleOperation Operation,
    bool Succeeded,
    bool TargetShutdownVerified,
    bool Ready,
    CdcManagedLifecycleBoundary Boundary,
    IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics
)
{
    public CdcControllerTargetStatus Observation { get; init; } = null!;

    public CdcRecoveryObservation Recovery { get; init; } = new(CdcRecoveryBoundary.Unobserved, false);
}

/// <summary>Retains one controller lock through intent, fresh validation, mutation and read-back.</summary>
public sealed class CdcManagedLifecycle
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly ICdcConnectTransport _connect;
    private readonly ICdcWorkerInspectionTransport _worker;
    private readonly CdcControllerStatus _status;
    private readonly TimeProvider _time;

    public CdcManagedLifecycle(
        string stateRoot,
        ICdcProviderSetupService provider,
        ICdcConnectorTemplateService templates,
        ICdcKafkaAdminAdapter kafka,
        ICdcConnectTransport connect,
        ICdcWorkerInspectionTransport worker,
        ICdcWorkerMetricsTransport metrics,
        IEnumerable<ICdcProviderSourcePositionAdapter> positions
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _store = new(stateRoot);
        _bindings = new CdcBindingLifecycleService(
            new LocalCdcBindingStateStore(stateRoot),
            TimeProvider.System
        );
        _connect = connect;
        _worker = worker;
        _time = TimeProvider.System;
        _status = new(stateRoot, provider, templates, kafka, connect, worker, metrics, positions);
    }

    internal CdcManagedLifecycle(
        LocalCdcWorkflowJournalStore store,
        ICdcBindingLifecycleService bindings,
        ICdcConnectTransport connect,
        ICdcWorkerInspectionTransport worker,
        CdcControllerStatus status,
        TimeProvider time
    )
    {
        _store = store;
        _bindings = bindings;
        _connect = connect;
        _worker = worker;
        _status = status;
        _time = time;
    }

    public async Task<CdcManagedLifecycleResult> ExecuteAsync(
        CdcControllerStatusTarget target,
        CdcManagedLifecycleOperation operation,
        CancellationToken cancellationToken = default,
        CancellationToken operationDeadline = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        var request = target.Request;
        var failure = new FailureBoundary();
        var boundary = CdcManagedLifecycleBoundary.Unverified;
        List<CdcDeploymentDiagnostic> diagnostics = [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operationDeadline
        );
        timeout.CancelAfter(request.Timing.WaitTimeout);
        var token = timeout.Token;
        try
        {
            Require(Enum.IsDefined(operation));
            await using var session = await _store.AcquireAsync(
                request.Timing.CallTimeout,
                request.Timing.PollInterval < request.Timing.CallTimeout
                    ? request.Timing.PollInterval
                    : request.Timing.CallTimeout,
                token
            );
            if (operation == CdcManagedLifecycleOperation.Stop)
            {
                var journal = await session.ReadAsync(request.TargetIdentity, token);
                var exact = await CallAsync(
                    request,
                    ct => _bindings.ExactMatchBindingAsync(request.Binding, ct),
                    token
                );
                Require(exact.Status == CdcControlPlaneOperationStatus.Succeeded);
                Require(
                    journal
                        .Operations.SelectMany(o => o.Completions)
                        .Any(c =>
                            c.Evidence is CdcWorkflowCompletion.Source source
                            && source.PhysicalSourceFingerprint == request.Binding.PhysicalSourceFingerprint
                        )
                );
                var id = Guid.NewGuid();
                await session.RecordIntentAsync(
                    request.TargetIdentity,
                    journal.WorkflowId,
                    id,
                    CdcWorkflowEffect.StopConnector,
                    [],
                    token
                );
                failure.Component = CdcDeploymentComponent.Worker;
                var worker = await InspectWorkerAsync(request, token);
                failure.Component = CdcDeploymentComponent.Connect;
                await MutateAsync(request, ct => _connect.StopAsync(request, ct), diagnostics, token);
                await WaitStoppedAsync(request, worker, token);
                failure.Component = CdcDeploymentComponent.WorkflowState;
                await session.ReconcileCompletionAsync(
                    request.TargetIdentity,
                    journal.WorkflowId,
                    id,
                    async (_, ct) =>
                    {
                        // Completion always independently reconciles, including after an uncertain REST result.
                        await RequireStoppedAsync(request, worker, ct);
                        return new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                            new CdcWorkflowCompletion.Shutdown()
                        );
                    },
                    token
                );
                // A write failure or interruption never returns worker-stop permission, even if rename survived.
                token.ThrowIfCancellationRequested();
                failure.Component = CdcDeploymentComponent.Connect;
                await RequireStoppedAsync(request, worker, token);
                return new(
                    operation,
                    true,
                    true,
                    false,
                    CdcManagedLifecycleBoundary.VerifiedManagedStop,
                    diagnostics
                )
                {
                    Recovery = new(CdcRecoveryBoundary.VerifiedManagedStop, false),
                };
            }

            // Persist resume intent before the final validation, so journal I/O cannot age the evidence
            // between authorization and REST. An interrupted intent consumes any preceding stop receipt.
            CdcEstablishedValidationObservation eligibility = null!;
            var preflight = await _status.ObserveTargetAsync(
                target,
                cancellationToken,
                session,
                CdcEstablishedValidationMode.PreStart,
                value => eligibility = value,
                operationDeadline: token
            );
            if (
                operation is CdcManagedLifecycleOperation.Restart or CdcManagedLifecycleOperation.Resume
                && preflight.Recovery.RequiresFreshPass
                && eligibility is { PreStartInvalidatedByRecovery: true }
                && preflight.Diagnostics.Count == 0
            )
            {
                // One complete replacement pass in this session and original deadline. The
                // invalidated pass authorizes no intent and contributes no publication evidence.
                eligibility = null!;
                preflight = await _status.ObserveTargetAsync(
                    target,
                    cancellationToken,
                    session,
                    CdcEstablishedValidationMode.PreStart,
                    value => eligibility = value,
                    operationDeadline: token
                );
            }
            failure.Observation = preflight;
            diagnostics.AddRange(preflight.Diagnostics);
            if (preflight.Recovery.Boundary == CdcRecoveryBoundary.NativeRecovery)
            {
                boundary = CdcManagedLifecycleBoundary.NativeRecovery;
            }
            Require(eligibility is { PreStartEligible: true } && diagnostics.Count == 0);
            var before = await session.ReadAsync(request.TargetIdentity, token);
            var latest = before.Operations.LastOrDefault(o =>
                o.Effect is CdcWorkflowEffect.StopConnector or CdcWorkflowEffect.ResumeConnector
            );
            bool verifiedStop =
                latest is { Effect: CdcWorkflowEffect.StopConnector }
                && latest.Completions is [{ Evidence: CdcWorkflowCompletion.Shutdown }];
            boundary = verifiedStop
                ? CdcManagedLifecycleBoundary.VerifiedManagedStop
                : CdcManagedLifecycleBoundary.NativeRecovery;

            if (operation == CdcManagedLifecycleOperation.Start)
            {
                if (!verifiedStop || !eligibility.Connector.IsStopped)
                {
                    boundary = CdcManagedLifecycleBoundary.NativeRecovery;
                    Require(false);
                }
            }
            else if (!eligibility.Connector.IsStopped)
            {
                boundary = CdcManagedLifecycleBoundary.NativeRecovery;
            }

            if (operation == CdcManagedLifecycleOperation.Start)
            {
                // The managed HTTP host is offline. Start its invocation-owned projection executor
                // only after fresh eligibility and verified STOPPED evidence under this session.
                // Initialization for preflight observations does not start processing.
                failure.Component = CdcDeploymentComponent.Projection;
                await target.Runtime.StartProcessingAsync(token);
                token.ThrowIfCancellationRequested();
                failure.Component = CdcDeploymentComponent.WorkflowState;
            }

            var resumeId = Guid.NewGuid();
            await session.RecordIntentAsync(
                request.TargetIdentity,
                before.WorkflowId,
                resumeId,
                CdcWorkflowEffect.ResumeConnector,
                [],
                token
            );
            var authorizedWorker = eligibility.Worker;
            eligibility = null!;
            preflight = await _status.ObserveTargetAsync(
                target,
                cancellationToken,
                session,
                CdcEstablishedValidationMode.PreStart,
                value => eligibility = value,
                resumeId,
                operationDeadline: token
            );
            failure.Observation = preflight;
            diagnostics.AddRange(preflight.Diagnostics);
            if (preflight.Recovery.Boundary == CdcRecoveryBoundary.NativeRecovery)
            {
                boundary = CdcManagedLifecycleBoundary.NativeRecovery;
            }
            Require(eligibility is { PreStartEligible: true } && diagnostics.Count == 0);
            RequireUnchangedWorker(eligibility.Worker);
            if (operation == CdcManagedLifecycleOperation.Start && !eligibility.Connector.IsStopped)
            {
                boundary = CdcManagedLifecycleBoundary.NativeRecovery;
                Require(false);
            }
            token.ThrowIfCancellationRequested();
            failure.Component = CdcDeploymentComponent.Connect;
            bool acknowledged = await MutateAsync(
                request,
                ct =>
                    operation == CdcManagedLifecycleOperation.Restart && !eligibility.Connector.IsStopped
                        ? _connect.RestartAsync(request, ct)
                        : _connect.ResumeAsync(request, ct),
                diagnostics,
                token
            );

            void RequireUnchangedWorker(CdcWorkerInspection current)
            {
                if (
                    current.ProcessIdentity != authorizedWorker.ProcessIdentity
                    || current.ConnectWorkerId != authorizedWorker.ConnectWorkerId
                )
                {
                    boundary = CdcManagedLifecycleBoundary.NativeRecovery;
                }
                CdcConnectorRegistration.RequireSameWorker(request, authorizedWorker, current);
            }

            // Fresh passes after the effect: never reuse pre-start status or a disposed telemetry pass.
            while (true)
            {
                CdcEstablishedValidationObservation current = null!;
                var status = await _status.ObserveTargetAsync(
                    target,
                    cancellationToken,
                    session,
                    CdcEstablishedValidationMode.RunningPublication,
                    value => current = value,
                    resumeId,
                    operationDeadline: token
                );
                failure.Observation = status;
                if (status.Status.SourceHistory.Continuity == CdcSourceHistoryContinuity.Lost)
                {
                    diagnostics.AddRange(status.Diagnostics);
                    Require(false);
                }
                if (current is { Worker: not null })
                {
                    RequireUnchangedWorker(current.Worker);
                }
                if (current is { Connector.IsRunning: true, PreStartEligible: true })
                {
                    // Unchanged RUNNING state cannot reconcile whether an unacknowledged restart
                    // actually ran. A stopped/failed -> RUNNING transition can be independently observed.
                    Require(acknowledged || !eligibility.Connector.IsRunning);
                    failure.Component = CdcDeploymentComponent.WorkflowState;
                    await session.ReconcileCompletionAsync(
                        request.TargetIdentity,
                        before.WorkflowId,
                        resumeId,
                        async (_, ct) =>
                        {
                            // Completion is only running-state history. Readiness is collected after persistence.
                            var live = await ReadStatusAsync(request, ct);
                            Require(live.IsRunning);
                            RequireUnchangedWorker(await InspectWorkerAsync(request, ct));
                            Require(live.WorkerId == current.Worker!.ConnectWorkerId);
                            return new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                                new CdcWorkflowCompletion.Reconciled()
                            );
                        },
                        token
                    );
                    CdcEstablishedValidationObservation finalObservation = null!;
                    var final = await _status.ObserveTargetAsync(
                        target,
                        cancellationToken,
                        session,
                        CdcEstablishedValidationMode.RunningPublication,
                        value => finalObservation = value,
                        resumeId,
                        operationDeadline: token
                    );
                    failure.Observation = final;
                    diagnostics.AddRange(final.Diagnostics);
                    if (finalObservation is { Worker: not null })
                    {
                        RequireUnchangedWorker(finalObservation.Worker);
                    }
                    token.ThrowIfCancellationRequested();
                    bool ready = final.Status.Readiness == CdcReadiness.Ready;
                    return new(
                        operation,
                        ready,
                        false,
                        ready,
                        boundary,
                        diagnostics.DistinctBy(d => (d.Component, d.Failure)).ToArray()
                    )
                    {
                        Observation = final,
                        Recovery = final.Recovery,
                    };
                }
                diagnostics.AddRange(status.Diagnostics);
                // Unknown/provenance failures reject; only an otherwise eligible transitioning task is polled.
                Require(current is { PreStartEligible: true });
                await Task.Delay(request.Timing.PollInterval, _time, token);
            }
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(
                exception switch
                {
                    CdcWorkflowStateException state => new(
                        failure.Component,
                        state.Failure switch
                        {
                            CdcWorkflowStateFailure.Missing or CdcWorkflowStateFailure.Unavailable =>
                                CdcDeploymentFailure.Unavailable,
                            CdcWorkflowStateFailure.LockTimeout => CdcDeploymentFailure.Timeout,
                            _ => CdcDeploymentFailure.ValidationFailed,
                        }
                    ),
                    CdcEstablishedValidation.EvidenceException evidence => evidence.Diagnostic,
                    OperationCanceledException or TimeoutException => new(
                        failure.Component,
                        CdcDeploymentFailure.Timeout
                    ),
                    _ => CdcDeploymentDiagnostic.FromException(failure.Component, exception),
                }
            );
            return new(
                operation,
                false,
                false,
                false,
                boundary,
                diagnostics.DistinctBy(d => (d.Component, d.Failure)).ToArray()
            )
            {
                Observation = failure.Observation,
                Recovery = new(
                    boundary == CdcManagedLifecycleBoundary.NativeRecovery
                        ? CdcRecoveryBoundary.NativeRecovery
                        : CdcRecoveryBoundary.Unobserved,
                    true
                ),
            };
        }
    }

    private static async Task<bool> MutateAsync(
        CdcDeploymentRequest request,
        Func<CancellationToken, Task<CdcTransportResult<CdcTransportAcknowledgement>>> action,
        List<CdcDeploymentDiagnostic> diagnostics,
        CancellationToken token
    )
    {
        try
        {
            var response = await CallAsync(request, action, token);
            diagnostics.AddRange(response.Diagnostics);
            return response is CdcTransportResult<CdcTransportAcknowledgement>.Observed;
        }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            diagnostics.Add(CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.Connect, exception));
            return false;
        }
    }

    private async Task WaitStoppedAsync(
        CdcDeploymentRequest request,
        CdcWorkerInspection worker,
        CancellationToken token
    )
    {
        while (true)
        {
            var status = await ReadStatusAsync(request, token);
            if (status.IsStopped && status.Runtime.TaskCount == 0 && status.Tasks.Count == 0)
            {
                await RequireStoppedAsync(request, worker, token);
                return;
            }
            await Task.Delay(request.Timing.PollInterval, _time, token);
        }
    }

    private async Task RequireStoppedAsync(
        CdcDeploymentRequest request,
        CdcWorkerInspection worker,
        CancellationToken token
    )
    {
        CdcConnectorRegistration.RequireSameWorker(request, worker, await InspectWorkerAsync(request, token));
        var status = await ReadStatusAsync(request, token);
        Require(
            status.IsStopped
                && status.Runtime.TaskCount == 0
                && status.Runtime.RunningTaskCount == 0
                && status.Tasks.Count == 0
                && status.WorkerId == worker.ConnectWorkerId
        );
    }

    private async Task<CdcConnectStatus> ReadStatusAsync(
        CdcDeploymentRequest request,
        CancellationToken token
    )
    {
        var started = _time.GetUtcNow();
        var response = await CallAsync(request, ct => _connect.ReadStatusAsync(request, ct), token);
        if (response is CdcTransportResult<CdcConnectStatus>.Unavailable unavailable)
        {
            throw new CdcEstablishedValidation.EvidenceException(unavailable.Diagnostic);
        }
        Require(response is CdcTransportResult<CdcConnectStatus>.Observed);
        var status = ((CdcTransportResult<CdcConnectStatus>.Observed)response).Value;
        var now = _time.GetUtcNow();
        Require(
            status.Runtime.ObservedAt >= started
                && status.Runtime.ObservedAt <= now
                && now - status.Runtime.ObservedAt <= request.Timing.MaximumObservationAge
                && CdcConnectorRuntimeObservationValidator
                    .ValidateForLifecycle(
                        status.Runtime,
                        request.Binding,
                        new(
                            status.Runtime.OperationId,
                            request.TargetIdentity,
                            request.Binding.PhysicalSourceFingerprint,
                            now
                        )
                    )
                    .Succeeded
        );
        return status;
    }

    private async Task<CdcWorkerInspection> InspectWorkerAsync(
        CdcDeploymentRequest request,
        CancellationToken token
    )
    {
        var response = await CallAsync(request, ct => _worker.InspectAsync(request, ct), token);
        if (response is CdcTransportResult<CdcWorkerInspection>.Unavailable unavailable)
        {
            throw new CdcEstablishedValidation.EvidenceException(unavailable.Diagnostic);
        }
        Require(response is CdcTransportResult<CdcWorkerInspection>.Observed);
        var worker = ((CdcTransportResult<CdcWorkerInspection>.Observed)response).Value;
        CdcConnectorRegistration.RequireWorker(request, worker);
        return worker;
    }

    private sealed class FailureBoundary
    {
        public CdcControllerTargetStatus Observation { get; set; } = null!;
        public CdcDeploymentComponent Component { get; set; } = CdcDeploymentComponent.WorkflowState;
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value)
    {
        if (!value)
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory);
        }
    }

    private static async Task<T> CallAsync<T>(
        CdcDeploymentRequest request,
        Func<CancellationToken, Task<T>> action,
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Timing.CallTimeout);
        var result = await action(timeout.Token).WaitAsync(timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        return result;
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Persists classified terminal loss and verifies shutdown under the caller's controller session.</summary>
internal sealed class CdcSourceHistoryContainment(
    ICdcBindingLifecycleService bindings,
    ICdcConnectTransport connect,
    TimeProvider time
)
{
    internal async Task<CdcIncidentPersistenceState> PersistAsync(
        CdcDeploymentRequest request,
        CdcSourceHistoryClassificationResult classification,
        CdcBindingStateContract bindingState,
        List<CdcDeploymentDiagnostic> diagnostics,
        Action<CdcBindingStateContract> capture,
        CancellationToken token
    )
    {
        if (bindingState is { State: CdcBindingState.IncidentLatched, Incident: not null })
        {
            return CdcIncidentPersistenceState.Persisted;
        }
        try
        {
            var candidate = classification.IncidentCandidate;
            if (candidate is null)
            {
                throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory);
            }
            var result = await CallAsync(
                request,
                ct => bindings.LatchSourceHistoryLossAsync(candidate.ToIncident(), ct),
                token
            );
            if (result.Status != CdcControlPlaneOperationStatus.Succeeded)
            {
                diagnostics.Add(
                    new(
                        CdcDeploymentComponent.WorkflowState,
                        result.Status == CdcControlPlaneOperationStatus.StateStoreUnavailable
                            ? CdcDeploymentFailure.Unavailable
                            : CdcDeploymentFailure.ValidationFailed
                    )
                );
                return CdcIncidentPersistenceState.Failed;
            }
            // Independent durable read-back is required even after acknowledgement or an idempotent retry.
            var readBack = await CallAsync(
                request,
                ct => bindings.ExactMatchBindingAsync(request.Binding, ct),
                token
            );
            if (
                readBack.Status != CdcControlPlaneOperationStatus.Succeeded
                || readBack.State is not { State: CdcBindingState.IncidentLatched, Incident: not null } state
                || !CdcIncidentValidator
                    .ValidateForBinding(state.Incident, request.Binding, time.GetUtcNow())
                    .Succeeded
            )
            {
                diagnostics.Add(
                    new(CdcDeploymentComponent.WorkflowState, CdcDeploymentFailure.ValidationFailed)
                );
                return CdcIncidentPersistenceState.Failed;
            }
            capture(state);
            return CdcIncidentPersistenceState.Persisted;
        }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            diagnostics.Add(Diagnostic(CdcDeploymentComponent.WorkflowState, exception));
            return CdcIncidentPersistenceState.Failed;
        }
    }

    internal async Task<CdcConnectorContainmentState> StopAsync(
        CdcDeploymentRequest request,
        List<CdcDeploymentDiagnostic> diagnostics,
        Action<CdcConnectorRuntimeObservation> capture,
        CancellationToken token
    )
    {
        // A lost stop response is ambiguous. Always try fresh read-back; never equate HTTP success with stop.
        try
        {
            var acknowledgement = await CallAsync(request, ct => connect.StopAsync(request, ct), token);
            diagnostics.AddRange(acknowledgement.Diagnostics);
        }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            diagnostics.Add(Diagnostic(CdcDeploymentComponent.Connect, exception));
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(request.Timing.WaitTimeout);
        try
        {
            while (true)
            {
                var readStarted = time.GetUtcNow();
                var response = await CallAsync(
                    request,
                    ct => connect.ReadStatusAsync(request, ct),
                    wait.Token
                );
                if (response is CdcTransportResult<CdcConnectStatus>.Observed observed)
                {
                    var status = observed.Value;
                    var now = time.GetUtcNow();
                    if (
                        status.IsStopped
                        && status.Runtime.TaskCount == 0
                        && status.Runtime.RunningTaskCount == 0
                        && status.Runtime.ObservedAt >= readStarted
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
                    )
                    {
                        capture(status.Runtime);
                        return CdcConnectorContainmentState.Stopped;
                    }
                }
                else
                {
                    diagnostics.AddRange(response.Diagnostics);
                    diagnostics.Add(new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable));
                    return CdcConnectorContainmentState.Failed;
                }
                await Task.Delay(request.Timing.PollInterval, time, wait.Token);
            }
        }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            diagnostics.Add(Diagnostic(CdcDeploymentComponent.Connect, exception));
            return CdcConnectorContainmentState.Failed;
        }
    }

    private static CdcDeploymentDiagnostic Diagnostic(
        CdcDeploymentComponent component,
        Exception exception
    ) =>
        exception switch
        {
            CdcEstablishedValidation.EvidenceException evidence => evidence.Diagnostic,
            OperationCanceledException or TimeoutException => new(component, CdcDeploymentFailure.Timeout),
            CdcWorkflowStateException state => new(
                component,
                state.Failure switch
                {
                    CdcWorkflowStateFailure.LockTimeout => CdcDeploymentFailure.Timeout,
                    CdcWorkflowStateFailure.Missing or CdcWorkflowStateFailure.Unavailable =>
                        CdcDeploymentFailure.Unavailable,
                    _ => CdcDeploymentFailure.ValidationFailed,
                }
            ),
            _ => CdcDeploymentDiagnostic.FromException(component, exception),
        };

    private static async Task<T> CallAsync<T>(
        CdcDeploymentRequest request,
        Func<CancellationToken, Task<T>> action,
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Timing.CallTimeout);
        try
        {
            var result = await action(timeout.Token).WaitAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
            when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }
}

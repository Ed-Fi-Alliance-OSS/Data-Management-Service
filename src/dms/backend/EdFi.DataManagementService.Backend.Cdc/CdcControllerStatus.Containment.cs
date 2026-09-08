// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed partial class CdcControllerStatus
{
    private async Task<CdcIncidentPersistenceState> PersistAsync(
        CdcDeploymentRequest request,
        CdcEstablishedStatusProgress progress,
        List<CdcDeploymentDiagnostic> diagnostics,
        CancellationToken token
    )
    {
        if (progress.Input.BindingState is { State: CdcBindingState.IncidentLatched, Incident: not null })
        {
            return CdcIncidentPersistenceState.Persisted;
        }
        try
        {
            var candidate = progress.SourceHistory.IncidentCandidate;
            if (candidate is null)
            {
                throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory);
            }
            var result = await CallAsync(
                request,
                ct => _bindings.LatchSourceHistoryLossAsync(candidate.ToIncident(), ct),
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
                ct => _bindings.ExactMatchBindingAsync(request.Binding, ct),
                token
            );
            if (
                readBack.Status != CdcControlPlaneOperationStatus.Succeeded
                || readBack.State is not { State: CdcBindingState.IncidentLatched, Incident: not null } state
                || !CdcIncidentValidator
                    .ValidateForBinding(state.Incident, request.Binding, _time.GetUtcNow())
                    .Succeeded
            )
            {
                diagnostics.Add(
                    new(CdcDeploymentComponent.WorkflowState, CdcDeploymentFailure.ValidationFailed)
                );
                return CdcIncidentPersistenceState.Failed;
            }
            progress.Capture(
                progress.Input with
                {
                    BindingState = state,
                    ObservedAt = _time.GetUtcNow(),
                },
                progress.HasPendingRecordSizeIncrease
            );
            return CdcIncidentPersistenceState.Persisted;
        }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            diagnostics.Add(Diagnostic(CdcDeploymentComponent.WorkflowState, exception));
            return CdcIncidentPersistenceState.Failed;
        }
    }

    private async Task<CdcConnectorContainmentState> ContainAsync(
        CdcDeploymentRequest request,
        CdcEstablishedStatusProgress progress,
        List<CdcDeploymentDiagnostic> diagnostics,
        CancellationToken token
    )
    {
        // A lost stop response is ambiguous. Always try fresh read-back; never equate HTTP success with stop.
        try
        {
            var acknowledgement = await CallAsync(request, ct => _connect.StopAsync(request, ct), token);
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
                var readStarted = _time.GetUtcNow();
                var response = await CallAsync(
                    request,
                    ct => _connect.ReadStatusAsync(request, ct),
                    wait.Token
                );
                if (response is CdcTransportResult<CdcConnectStatus>.Observed observed)
                {
                    var status = observed.Value;
                    var now = _time.GetUtcNow();
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
                        progress.Capture(
                            progress.Input with
                            {
                                ObservedAt = now,
                                ConnectorRuntime = status.Runtime with
                                {
                                    OperationId = progress.Input.OperationId,
                                },
                                Lag = null,
                            },
                            progress.HasPendingRecordSizeIncrease
                        );
                        return CdcConnectorContainmentState.Stopped;
                    }
                }
                else
                {
                    diagnostics.AddRange(response.Diagnostics);
                    diagnostics.Add(new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable));
                    return CdcConnectorContainmentState.Failed;
                }
                await Task.Delay(request.Timing.PollInterval, _time, wait.Token);
            }
        }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            diagnostics.Add(Diagnostic(CdcDeploymentComponent.Connect, exception));
            return CdcConnectorContainmentState.Failed;
        }
    }
}

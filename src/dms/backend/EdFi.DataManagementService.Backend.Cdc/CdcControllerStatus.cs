// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using CoreProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Deployment-owned observational status and containment. Each target pass holds the controller lock
/// through inspection, incident persistence and verified connector stop. Watch yields and waits outside
/// that lock. No HTTP traffic gate, projection lifecycle mutation, artifact repair or readiness receipt.
/// </summary>
public sealed partial class CdcControllerStatus
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly ICdcConnectTransport _connect;
    private readonly Func<CoreProvider, CdcEstablishedValidation> _validation;
    private readonly TimeProvider _time;

    public CdcControllerStatus(
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
        _time = TimeProvider.System;
        var adapters = positions.ToDictionary(p => p.Provider);
        _validation = p =>
            new(_store, _bindings, provider, templates, kafka, connect, worker, metrics, adapters[p], _time);
    }

    internal CdcControllerStatus(
        LocalCdcWorkflowJournalStore store,
        ICdcBindingLifecycleService bindings,
        ICdcConnectTransport connect,
        Func<CoreProvider, CdcEstablishedValidation> validation,
        TimeProvider time
    )
    {
        _store = store;
        _bindings = bindings;
        _connect = connect;
        _validation = validation;
        _time = time;
    }

    public async Task<CdcControllerStatusResult> StatusAsync(
        IReadOnlyList<CdcControllerStatusTarget> targets,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targets);
        var selected = targets.ToArray();
        if (
            Array.Exists(selected, t => t is null)
            || selected.Select(t => t.Request.TargetIdentity).Distinct().Count() != selected.Length
        )
        {
            throw new ArgumentException("Status requires distinct non-null targets.", nameof(targets));
        }
        List<CdcControllerTargetStatus> results = [];
        foreach (var target in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ObserveTargetAsync(target, cancellationToken));
        }
        PropagateSharedOffsetStoreIssues(selected, results);
        var aggregate = CdcAggregateStatusEvaluator.Evaluate(
            new(_time.GetUtcNow(), results.Select(r => r.Status).ToArray())
        );
        return new(
            aggregate,
            aggregate
                .Targets.Select(t => results.Single(r => r.Status.TargetIdentity == t.TargetIdentity))
                .ToArray()
        );
    }

    /// <summary>Bounded number of fresh passes. The caller consumes results outside the controller lock.</summary>
    public IAsyncEnumerable<CdcControllerStatusResult> WatchAsync(
        IReadOnlyList<CdcControllerStatusTarget> targets,
        int maximumPasses,
        TimeSpan interval,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (
            maximumPasses is < 1 or > 10000
            || interval <= TimeSpan.Zero
            || interval > TimeSpan.FromMinutes(5)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPasses),
                "Watch requires 1-10000 passes and a positive interval of at most five minutes."
            );
        }
        return WatchPassesAsync(targets.ToArray(), maximumPasses, interval, cancellationToken);
    }

    private async IAsyncEnumerable<CdcControllerStatusResult> WatchPassesAsync(
        CdcControllerStatusTarget[] selected,
        int maximumPasses,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        for (int index = 0; index < maximumPasses; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > 0)
            {
                await Task.Delay(interval, _time, cancellationToken);
            }
            yield return await StatusAsync(selected, cancellationToken);
        }
    }

    private async Task<CdcControllerTargetStatus> ObserveTargetAsync(
        CdcControllerStatusTarget target,
        CancellationToken token
    )
    {
        var request = target.Request;
        var progress = new CdcEstablishedStatusProgress(
            new(
                Guid.NewGuid().ToString("D"),
                _time.GetUtcNow(),
                request.TargetIdentity,
                request.Binding.PhysicalSourceFingerprint
            )
        );
        List<CdcDeploymentDiagnostic> diagnostics = [];
        var persistence = CdcIncidentPersistenceState.NotRequired;
        var containment = CdcConnectorContainmentState.NotRequired;
        var component = CdcDeploymentComponent.WorkflowState;
        using var observationTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        observationTimeout.CancelAfter(request.Timing.WaitTimeout);
        try
        {
            await using var session = await _store.AcquireAsync(
                request.Timing.CallTimeout,
                request.Timing.PollInterval < request.Timing.CallTimeout
                    ? request.Timing.PollInterval
                    : request.Timing.CallTimeout,
                observationTimeout.Token
            );
            bool containmentAttempted = false;
            progress.ContainTerminal = async () =>
            {
                if (
                    !containmentAttempted
                    && progress.SourceHistory?.Observation.Continuity == CdcSourceHistoryContinuity.Lost
                )
                {
                    containmentAttempted = true;
                    persistence = await PersistAsync(request, progress, diagnostics, token);
                    containment = await ContainAsync(request, progress, diagnostics, token);
                }
            };
            try
            {
                // Retained terminal evidence still requires containment when another provenance or live read fails.
                var binding = await CallAsync(
                    request,
                    ct => _bindings.ExactMatchBindingAsync(request.Binding, ct),
                    observationTimeout.Token
                );
                if (
                    binding.Status == CdcControlPlaneOperationStatus.Succeeded
                    && binding.State is { Incident: not null } state
                )
                {
                    var retained = CdcSourceHistoryContinuityClassifier.Evaluate(
                        new(progress.Input.OperationId, _time.GetUtcNow(), _time.GetUtcNow(), request.Binding)
                        {
                            LatchedIncident = state.Incident,
                        }
                    );
                    progress.Capture(
                        progress.Input with
                        {
                            ObservedAt = _time.GetUtcNow(),
                            BindingState = state,
                            SourceHistory = retained.Observation,
                        },
                        false,
                        retained
                    );
                }
                await progress.ContainTerminal();
                var observation = await _validation(request.Binding.Provider)
                    .ObserveInSessionAsync(
                        request,
                        target.Runtime,
                        CdcEstablishedValidationMode.RunningPublication,
                        target.LagThresholdMilliseconds,
                        target.Integrity,
                        session,
                        c => component = c,
                        observationTimeout.Token,
                        progress
                    );
                diagnostics.AddRange(observation.Diagnostics);
            }
            catch (Exception exception)
            {
                token.ThrowIfCancellationRequested();
                diagnostics.Add(Diagnostic(component, exception));
            }
            // Separate bounded budgets ensure an exhausted observation or failed latch cannot suppress stop.
            await progress.ContainTerminal();
            if (
                containment == CdcConnectorContainmentState.Stopped
                && progress.Input.ConnectorRuntime is { } current
                && (current.ConnectorState != CdcConnectorRuntimeState.Stopped || current.TaskCount != 0)
            )
            {
                // Native recovery is not fenced by a prior STOPPED read. Keep the terminal latch and
                // report the newly observed containment failure; a later watch pass attempts stop again.
                containment = CdcConnectorContainmentState.Failed;
                diagnostics.Add(new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Conflict));
            }
        }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            diagnostics.Add(Diagnostic(component, exception));
        }
        var status =
            progress.CompletedStatus
            ?? CdcTargetStatusEvaluator.EvaluatePostAdmission(progress.Input with { Lag = null });
        if (progress.HasPendingRecordSizeIncrease || diagnostics.Count > 0)
        {
            status = Block(status, CdcBlockingCategory.StatusObservationUnavailable);
        }
        var input = progress.Input;
        var details = new CdcControllerStatusDetails(
            input.Projection?.QueuePresence ?? DocumentCacheStatusQueuePresence.Unknown,
            input.Lag?.CurrentLagMilliseconds,
            input.Lag?.ThresholdMilliseconds,
            input.Lag?.P50LagMilliseconds,
            input.Lag?.P95LagMilliseconds,
            input.Lag?.P99LagMilliseconds,
            input.SourceHistory?.ProviderArtifactState ?? CdcProviderArtifactContinuityState.Unknown,
            input.SourceHistory?.RetainedRangeState ?? CdcProviderRetainedRangeState.Unknown,
            input.SourceHistory?.SchemaHistoryState ?? CdcSqlServerSchemaHistoryState.Unknown,
            input.SourceHistory?.IncidentFailureCategory,
            Positions(request, input)
        );
        return new(
            input.ObservedAt,
            status,
            details,
            progress.HasPendingRecordSizeIncrease,
            input.ConnectOffsetStore is not null
                && status.ConnectOffsetStore.State != CdcComponentState.Satisfied,
            persistence,
            containment,
            diagnostics.DistinctBy(d => (d.Component, d.Failure)).ToArray()
        );
    }

    private static CdcControllerStatusPositions Positions(
        CdcDeploymentRequest request,
        CdcTargetStatusEvaluationInput input
    )
    {
        if (
            input.SourceHistory is { PositionEvidence: not null } history
            && CdcSourceHistoryObservationValidator
                .ValidateForBinding(
                    history,
                    request.Binding,
                    new(
                        input.OperationId,
                        input.TargetIdentity,
                        input.PhysicalSourceFingerprint,
                        input.ObservedAt
                    )
                )
                .Succeeded
        )
        {
            var evidence = history.PositionEvidence;
            return new(
                evidence.LsnProc ?? "",
                evidence.CommitLsn ?? "",
                evidence.ChangeLsn ?? "",
                evidence.EventSerialNo,
                evidence.RetainedRangeStart ?? "",
                evidence.RetainedRangeEnd ?? "",
                evidence.UnavailableFacts
            );
        }
        return new("", "", "", null, "", "", []);
    }

    private static CdcTargetStatus Block(CdcTargetStatus status, CdcBlockingCategory fallback) =>
        status with
        {
            Readiness = CdcReadiness.NotReady,
            PrimaryBlockingCategory =
                status.Readiness == CdcReadiness.NotReady ? status.PrimaryBlockingCategory : fallback,
        };

    private static void PropagateSharedOffsetStoreIssues(
        CdcControllerStatusTarget[] selected,
        List<CdcControllerTargetStatus> results
    )
    {
        // The configured deployment and worker group identify the shared store; endpoint aliases must not
        // isolate bindings of that same worker. Names alone in a different deployment do not correlate.
        var affected = selected
            .Where((_, i) => results[i].HasSharedOffsetStoreIssue)
            .Select(t => (t.Request.TargetIdentity.DeploymentKey, t.Request.WorkerPolicy.WorkerKey.Value))
            .ToHashSet();
        for (int i = 0; i < selected.Length; i++)
        {
            var request = selected[i].Request;
            if (
                !affected.Contains(
                    (request.TargetIdentity.DeploymentKey, request.WorkerPolicy.WorkerKey.Value)
                )
            )
            {
                continue;
            }
            var result = results[i];
            results[i] = result with
            {
                HasSharedOffsetStoreIssue = true,
                Status = Block(
                    result.Status with
                    {
                        ConnectOffsetStore = CdcComponent.NotSatisfied(
                            CdcBlockingCategory.ConnectOffsetStoreInvalid,
                            result.ObservedAt,
                            "Shared worker offset-store evidence is not satisfied in this pass."
                        ),
                    },
                    CdcBlockingCategory.ConnectOffsetStoreInvalid
                ),
                Diagnostics = result
                    .Diagnostics.Concat([
                        new CdcDeploymentDiagnostic(
                            CdcDeploymentComponent.Kafka,
                            CdcDeploymentFailure.ValidationFailed
                        ),
                    ])
                    .DistinctBy(d => (d.Component, d.Failure))
                    .ToArray(),
            };
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

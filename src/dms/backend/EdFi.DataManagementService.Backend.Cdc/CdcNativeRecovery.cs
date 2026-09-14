// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public enum CdcRecoveryBoundary
{
    Unobserved,
    VerifiedManagedStop,
    VerifiedManagedRestart,
    NativeRecovery,
}

/// <summary>Historical diagnostics only. A healthy later pass never certifies an unsampled interval.</summary>
public sealed record CdcRecoveryObservation(CdcRecoveryBoundary Boundary, bool RequiresFreshPass)
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S2325",
        Justification = "Serialized instance diagnostic must explicitly deny interval certification."
    )]
    public bool UnobservedIntervalCertified => false;
    public string Explanation =>
        Boundary switch
        {
            CdcRecoveryBoundary.NativeRecovery =>
                "Native recovery or unverified shutdown observed. Later healthy evidence does not certify continuity or absence of publication during the unobserved interval.",
            CdcRecoveryBoundary.VerifiedManagedStop =>
                "Managed shutdown receipt and retained STOPPED/no-task state were observed. This does not certify current state or an unobserved interval.",
            CdcRecoveryBoundary.VerifiedManagedRestart =>
                "Managed restart completion observed. Current health is observational and does not certify an unobserved interval.",
            _ =>
                "No recovery boundary established. Current health is observational; unobserved intervals are not certified.",
        };
}

/// <summary>
/// Retains identity comparisons only, never readiness, offsets, telemetry or barriers. Access is under
/// the controller session. REST exposes task id/assignment/state, not a task incarnation: a restart
/// entirely between samples on the same worker cannot be detected or certified by this observer.
/// </summary>
internal sealed class CdcNativeRecoveryObserver
{
    private readonly Dictionary<
        (CdcTargetIdentity Target, string Source, string Connector),
        Sample
    > _samples = [];

    internal CdcRecoveryObservation Last(CdcDeploymentRequest request) =>
        new(
            _samples.TryGetValue(
                (
                    request.TargetIdentity,
                    request.Binding.PhysicalSourceFingerprint,
                    request.Binding.ConnectorName
                ),
                out var sample
            )
                ? sample.Boundary
                : CdcRecoveryBoundary.Unobserved,
            false
        );

    internal CdcRecoveryObservation Observe(
        CdcDeploymentRequest request,
        CdcWorkflowJournal journal,
        CdcWorkerInspection worker,
        CdcConnectStatus status,
        CdcTelemetryObservationPass pass,
        Guid managedResumeId = default
    )
    {
        var key = (
            request.TargetIdentity,
            request.Binding.PhysicalSourceFingerprint,
            request.Binding.ConnectorName
        );
        _samples.TryGetValue(key, out var previous);
        var lifecycle = journal.Operations.LastOrDefault(o =>
            o.Effect is CdcWorkflowEffect.StopConnector or CdcWorkflowEffect.ResumeConnector
        );
        bool verifiedStop =
            lifecycle is { Effect: CdcWorkflowEffect.StopConnector }
            && lifecycle.Completions is [{ Evidence: CdcWorkflowCompletion.Shutdown }]
            && status.IsStopped
            && status.Tasks.Count == 0;
        bool ownedResume =
            managedResumeId != Guid.Empty
            && lifecycle is { Effect: CdcWorkflowEffect.ResumeConnector }
            && lifecycle.OperationId == managedResumeId;
        bool completedResume =
            lifecycle is { Effect: CdcWorkflowEffect.ResumeConnector }
            && lifecycle.Completions is [{ Evidence: CdcWorkflowCompletion.Reconciled }]
            && status.IsRunning;
        bool newCompletion =
            completedResume
            && (
                previous is null
                || previous.LifecycleId != lifecycle!.OperationId
                || !previous.LifecycleCompleted
            );
        var tasks = status.Tasks.Select(t => new TaskIdentity(t.Id, t.WorkerId, t.State)).ToArray();
        bool workerChanged =
            previous is not null
            && (previous.Process != worker.ProcessIdentity || previous.Worker != worker.ConnectWorkerId);
        bool taskChanged =
            previous is not null
            && (
                previous.ConnectorState != status.Runtime.ConnectorState
                || previous.ConnectorWorker != status.WorkerId
                || !previous.Tasks.SequenceEqual(tasks)
            );
        bool unverifiedShutdown =
            (lifecycle is not null || status.IsStopped) && !verifiedStop && !completedResume && !ownedResume;
        bool newUnverifiedShutdown =
            unverifiedShutdown
            && lifecycle is not null
            && (
                previous is null
                || previous.LifecycleId != (lifecycle?.OperationId ?? Guid.Empty)
                || !previous.UnverifiedShutdown
            );
        bool native =
            !verifiedStop
            && (workerChanged || taskChanged && !ownedResume && !newCompletion || newUnverifiedShutdown);
        var boundary = previous?.Boundary ?? CdcRecoveryBoundary.Unobserved;
        if (native || unverifiedShutdown)
        {
            boundary = CdcRecoveryBoundary.NativeRecovery;
        }
        else if (verifiedStop)
        {
            boundary = CdcRecoveryBoundary.VerifiedManagedStop;
        }
        else if (newCompletion)
        {
            boundary = CdcRecoveryBoundary.VerifiedManagedRestart;
        }
        if (native)
        {
            // Provider and offset reads earlier in this pass may precede the detected recovery.
            // Dispose/invalidate all evidence from this pass; the next invocation re-reads everything.
            pass.Invalidate();
        }
        _samples[key] = new(
            worker.ProcessIdentity,
            worker.ConnectWorkerId,
            status.WorkerId,
            status.Runtime.ConnectorState,
            tasks,
            lifecycle?.OperationId ?? Guid.Empty,
            lifecycle is not null && !lifecycle.Completions.IsEmpty,
            unverifiedShutdown,
            boundary
        );
        return new(boundary, !pass.IsValid);
    }

    private sealed record TaskIdentity(int Id, string Worker, CdcConnectorRuntimeState State);

    private sealed record Sample(
        string Process,
        string Worker,
        string ConnectorWorker,
        CdcConnectorRuntimeState ConnectorState,
        TaskIdentity[] Tasks,
        Guid LifecycleId,
        bool LifecycleCompleted,
        bool UnverifiedShutdown,
        CdcRecoveryBoundary Boundary
    );
}

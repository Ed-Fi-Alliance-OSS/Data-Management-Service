// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed partial class CdcEstablishedValidation
{
    private static CdcEstablishedValidationObservation Evaluate(
        CdcEstablishedValidationMode mode,
        CdcWorkflowJournal journal,
        CdcTargetStatusEvaluationInput input,
        CdcSourceHistoryClassificationResult continuity,
        CdcConnectStatus connector,
        CdcWorkerInspection worker,
        List<CdcDeploymentDiagnostic> diagnostics,
        bool acknowledgedIncrease = false
    )
    {
        // This observational check does not certify another initial baseline.
        var status = CdcTargetStatusEvaluator.Evaluate(input);
        (CdcComponent Evidence, CdcDeploymentComponent Component)[] prerequisites =
        [
            (status.Binding, CdcDeploymentComponent.WorkflowState),
            (status.ProviderSetup, CdcDeploymentComponent.ProviderSetup),
            (status.ConnectorConfig, CdcDeploymentComponent.Connect),
            (status.KafkaPolicy, CdcDeploymentComponent.Kafka),
            (status.ConnectOffsetStore, CdcDeploymentComponent.Kafka),
        ];
        foreach (var item in prerequisites)
        {
            AddFailure(item.Evidence.State, item.Component);
        }
        AddFailure(status.SourceHistory.State, CdcDeploymentComponent.ProviderSetup);
        if (journal.HasPendingRecordSizeIncrease && !acknowledgedIncrease)
        {
            diagnostics.Add(new(CdcDeploymentComponent.WorkflowState, CdcDeploymentFailure.ValidationFailed));
        }
        bool common =
            Array.TrueForAll(prerequisites, p => p.Evidence.State == CdcComponentState.Satisfied)
            && status.SourceHistory.State == CdcComponentState.Satisfied
            && continuity.Observation.Continuity == CdcSourceHistoryContinuity.Healthy
            && (!journal.HasPendingRecordSizeIncrease || acknowledgedIncrease);
        bool startable =
            connector.IsStopped
            || connector.IsRunning
            || connector.Tasks.Count == 1
                && connector.Tasks[0].State == CdcConnectorRuntimeState.Failed
                && connector.Runtime.ConnectorState
                    is CdcConnectorRuntimeState.Running
                        or CdcConnectorRuntimeState.Failed;
        bool preStart = common && startable;
        if (!startable)
        {
            diagnostics.Add(new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.ValidationFailed));
        }
        bool ready = false;
        if (mode == CdcEstablishedValidationMode.RunningPublication)
        {
            AddFailure(status.Projection.State, CdcDeploymentComponent.Projection);
            AddFailure(status.ConnectorRuntime.State, CdcDeploymentComponent.Connect);
            AddFailure(status.Lag.State, CdcDeploymentComponent.Metrics);
            ready =
                common
                && connector.IsRunning
                && status.Projection.State == CdcComponentState.Satisfied
                && status.ConnectorRuntime.State == CdcComponentState.Satisfied
                && status.Lag.State == CdcComponentState.Satisfied;
        }
        return new(
            input.ObservedAt,
            preStart,
            ready,
            continuity.Observation.Continuity,
            journal.HasPendingRecordSizeIncrease,
            diagnostics.DistinctBy(d => (d.Component, d.Failure)).ToArray()
        )
        {
            Evidence = input,
            SourceHistory = continuity,
            Connector = connector,
            Worker = worker,
        };

        void AddFailure(CdcComponentState state, CdcDeploymentComponent component)
        {
            if (state != CdcComponentState.Satisfied)
            {
                diagnostics.Add(
                    new(
                        component,
                        state == CdcComponentState.Unknown
                            ? CdcDeploymentFailure.Unavailable
                            : CdcDeploymentFailure.ValidationFailed
                    )
                );
            }
        }
    }

    private void RequireStatus(
        CdcDeploymentRequest request,
        CdcWorkerInspection worker,
        CdcConnectStatus status
    )
    {
        Require(
            Fresh(request, status.Runtime.ObservedAt)
                && CdcConnectorRuntimeObservationValidator
                    .ValidateForLifecycle(
                        status.Runtime,
                        request.Binding,
                        new(
                            status.Runtime.OperationId,
                            request.TargetIdentity,
                            request.Binding.PhysicalSourceFingerprint,
                            _time.GetUtcNow()
                        )
                    )
                    .Succeeded
                && status.WorkerId == worker.ConnectWorkerId
                && status.Tasks.Count == status.Runtime.TaskCount
                && status.Tasks.Count <= 1
                && status.Tasks.All(t =>
                    t.Id == 0
                    && t.WorkerId == worker.ConnectWorkerId
                    && t.State == status.Runtime.SoleTaskState
                )
        );
    }

    private async Task<CdcProjectionCorrelationObservation> ProjectionAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        string operation,
        CancellationToken token
    )
    {
        var started = _time.GetUtcNow();
        var response = await CallAsync(request, ct => runtime.ObserveAsync(ct), token);
        return CdcControllerObservations.Projection(request, response, operation, started, _time.GetUtcNow());
    }

    private CdcConnectorOffsetObservation Offset(
        CdcDeploymentRequest request,
        string operation,
        CdcConnectOffsetEvidence offset,
        string expectedSourcePartitionHash
    )
    {
        bool postgres = request.Binding.Provider == CdcProvider.Postgresql;
        // The transport already parses provider positions. Preserve null/snapshot/malformed and
        // authoritative match outcomes so Core, rather than the controller, classifies history loss.
        return new(
            CdcJsonContract.CurrentContractVersion,
            operation,
            _time.GetUtcNow(),
            request.TargetIdentity,
            request.Binding.Provider,
            request.Binding.PhysicalSourceFingerprint,
            request.Binding.ConnectorName,
            request.Binding.ConnectorName,
            postgres
                ? offset.Postgresql.SourcePartitionMatchResult
                : offset.SqlServer.SourcePartitionMatchResult,
            string.IsNullOrEmpty(offset.SourcePartitionHash)
            && offset.State != CdcConnectOffsetState.Streaming
                ? expectedSourcePartitionHash
                : offset.SourcePartitionHash,
            offset.State == CdcConnectOffsetState.Snapshot,
            offset.State == CdcConnectOffsetState.Null,
            postgres ? offset.Postgresql.LsnProc : null,
            postgres ? null : offset.SqlServer.CommitLsn,
            postgres ? null : offset.SqlServer.ChangeLsn,
            postgres ? null : offset.SqlServer.EventSerialNo,
            []
        );
    }
}

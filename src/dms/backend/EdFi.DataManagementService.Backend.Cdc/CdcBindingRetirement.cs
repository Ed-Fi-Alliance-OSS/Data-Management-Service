// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed record CdcBindingRetirementResult(
    bool Succeeded,
    Guid OperationId,
    IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics
);

/// <summary>
/// Destructive generation retirement, retaining infrastructure, the journal and source-lifetime history.
/// One controller session spans intent, stopped-connector offset removal, independently reconciled
/// cleanup and Core state-last deletion. Checkpoints never authorize enablement or ordinary restart.
/// </summary>
public sealed class CdcBindingRetirement
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly ICdcConnectTransport _connect;
    private readonly ICdcArtifactCleanupAdapter _connectCleanup;
    private readonly ICdcKafkaArtifactCleanupAdapter _kafka;
    private readonly ICdcProviderArtifactCleanupAdapter _provider;
    private readonly TimeProvider _time;

    public CdcBindingRetirement(
        string stateRoot,
        ICdcConnectTransport connect,
        ICdcKafkaArtifactCleanupAdapter kafka,
        ICdcProviderArtifactCleanupAdapter provider
    )
        : this(
            new(stateRoot),
            new CdcBindingLifecycleService(new LocalCdcBindingStateStore(stateRoot), TimeProvider.System),
            connect,
            kafka,
            provider,
            TimeProvider.System
        )
    {
        ArgumentNullException.ThrowIfNull(connect);
    }

    internal CdcBindingRetirement(
        LocalCdcWorkflowJournalStore store,
        ICdcBindingLifecycleService bindings,
        ICdcConnectTransport connect,
        ICdcKafkaArtifactCleanupAdapter kafka,
        ICdcProviderArtifactCleanupAdapter provider,
        TimeProvider time
    )
    {
        _store = store;
        _bindings = bindings;
        _connect = connect;
        _connectCleanup = new CdcConnectArtifactCleanupAdapter(connect);
        _kafka = kafka;
        _provider = provider;
        _time = time;
    }

    public async Task<CdcBindingRetirementResult> RetireAsync(
        CdcDeploymentRequest request,
        long generation,
        bool destructiveCleanup,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var component = CdcDeploymentComponent.Request;
        Guid operationId = Guid.Empty;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.WaitTimeout);
        var token = timeout.Token;
        try
        {
            Require(destructiveCleanup && generation > 0 && generation == request.Binding.Generation);
            component = CdcDeploymentComponent.WorkflowState;
            await using var session = await _store.AcquireAsync(
                request.Timing.CallTimeout,
                request.Timing.PollInterval < request.Timing.CallTimeout
                    ? request.Timing.PollInterval
                    : request.Timing.CallTimeout,
                token
            );
            var journal = await session.ReadAsync(request.TargetIdentity, token);
            var history = await session.ReadSourcePublicationHistoryAsync(
                request.TargetIdentity,
                request.Binding.PhysicalSourceFingerprint,
                token
            );
            Require(
                history.WorkflowId == journal.WorkflowId
                    && history.CreationTarget == request.TargetIdentity
                    && history.CreationReceipt.Outcome == CdcDatabaseCreationOutcome.Created
                    && history.Transitions[^1].Status
                        is DocumentCacheDownstreamPublicationStatus.Possible
                            or DocumentCacheDownstreamPublicationStatus.Active
                            or DocumentCacheDownstreamPublicationStatus.Historical
            );
            Require(journal.Operations.Any(o => o.Effect == CdcWorkflowEffect.ReserveBinding));
            var retirements = journal.Operations.Where(o => o.Effect == CdcWorkflowEffect.Retire).ToArray();
            Require(retirements.Length <= 1);
            if (retirements.Length == 1)
            {
                var existing = retirements.Single();
                Require(existing.Retirement is [{ Binding: var binding }] && binding == request.Binding);
                operationId = existing.OperationId;
            }
            var exact = await CallAsync(ct => _bindings.ExactMatchBindingAsync(request.Binding, ct), token);
            bool deletingState =
                retirements.Length == 1
                && retirements
                    .Single()
                    .Retirement.Single()
                    .Steps.Any(s => s.Kind == CdcRetirementStepKind.DeleteState);
            Require(
                exact.Status == CdcControlPlaneOperationStatus.Succeeded
                    || exact.Status == CdcControlPlaneOperationStatus.BindingMissing && deletingState
            );
            if (retirements.Length == 0)
            {
                operationId = Guid.NewGuid();
                journal = await session.RecordRetirementIntentAsync(
                    request.Binding,
                    journal.WorkflowId,
                    operationId,
                    token
                );
            }
            var names = CdcArtifactNameGenerator.RecoverFromBinding(request.Binding);
            Require(names.Succeeded && names.Inventory is not null);
            var scope = new CdcArtifactCleanupScope(
                request,
                request.Binding.ToCompleteBindingIdentity(),
                names.Inventory.GovernedArtifacts
            );
            Dictionary<CdcGovernedArtifactKind, CdcGovernedArtifact> evidence = [];
            foreach (var step in RetirementSteps(request.Binding.Provider))
            {
                if (step == CdcRetirementStepKind.DeleteState)
                {
                    break;
                }
                component = CdcDeploymentComponent.WorkflowState;
                journal = await session.RecordRetirementStepAsync(
                    request.TargetIdentity,
                    journal.WorkflowId,
                    operationId,
                    step,
                    false,
                    token
                );
                await ReconcileAsync(step, token);
                component = CdcDeploymentComponent.WorkflowState;
                journal = await session.RecordRetirementStepAsync(
                    request.TargetIdentity,
                    journal.WorkflowId,
                    operationId,
                    step,
                    true,
                    token
                );
            }
            component = CdcDeploymentComponent.WorkflowState;
            journal = await session.RecordRetirementStepAsync(
                request.TargetIdentity,
                journal.WorkflowId,
                operationId,
                CdcRetirementStepKind.DeleteState,
                false,
                token
            );
            // Persistence can fail or stall. Collect fresh evidence again beside the irreversible state removal.
            foreach (
                var step in RetirementSteps(request.Binding.Provider)
                    .Where(s => s != CdcRetirementStepKind.DeleteState)
            )
            {
                await ReconcileAsync(step, token);
            }
            component = CdcDeploymentComponent.WorkflowState;
            var proof = new CdcCleanupProof(
                CdcJsonContract.CurrentContractVersion,
                operationId.ToString("D"),
                _time.GetUtcNow(),
                request.Binding.ToCompleteBindingIdentity(),
                CdcCleanupMode.RetireBindingGeneration,
                evidence.Values.ToArray()
            );
            Require(evidence.Count == scope.Inventory.Count);
            await DeleteStateAsync(token);
            journal = await session.RecordRetirementStepAsync(
                request.TargetIdentity,
                journal.WorkflowId,
                operationId,
                CdcRetirementStepKind.DeleteState,
                true,
                token
            );
            await session.ReconcileCompletionAsync(
                request.TargetIdentity,
                journal.WorkflowId,
                operationId,
                async (_, ct) =>
                {
                    // Core's idempotent deletion also handles the orphan-incident crash boundary. Missing binding
                    // alone is insufficient: Core must verify or remove the incident using the complete proof.
                    await DeleteStateAsync(ct);
                    return new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                        new CdcWorkflowCompletion.Reconciled()
                    );
                },
                token
            );
            token.ThrowIfCancellationRequested();
            return new(true, operationId, []);

            async Task DeleteStateAsync(CancellationToken ct)
            {
                var result = await CallAsync(
                    t =>
                        _bindings.DeleteStateAfterVerifiedCleanupAsync(
                            proof with
                            {
                                VerifiedAt = _time.GetUtcNow(),
                            },
                            t
                        ),
                    ct
                );
                Require(
                    result.Status
                        is CdcControlPlaneOperationStatus.Succeeded
                            or CdcControlPlaneOperationStatus.BindingMissing
                );
            }

            async Task ReconcileAsync(CdcRetirementStepKind step, CancellationToken ct)
            {
                if (step == CdcRetirementStepKind.SqlServerJobs)
                {
                    component = CdcDeploymentComponent.ProviderSetup;
                    RequireObserved(
                        await _provider.DeleteOwnedSqlServerJobsAsync(scope, session, ct).WaitAsync(ct)
                    );
                    return;
                }
                var kind = Enum.Parse<CdcGovernedArtifactKind>(step.ToString());
                component = kind switch
                {
                    CdcGovernedArtifactKind.ConnectSourceOffsets
                    or CdcGovernedArtifactKind.KafkaConnectConnector => CdcDeploymentComponent.Connect,
                    CdcGovernedArtifactKind.PostgresqlLogicalSlot
                    or CdcGovernedArtifactKind.PostgresqlPublication
                    or CdcGovernedArtifactKind.SqlServerCdcGatingRole
                    or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                    or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                    or CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat =>
                        CdcDeploymentComponent.ProviderSetup,
                    _ => CdcDeploymentComponent.Kafka,
                };
                CdcGovernedArtifact artifact;
                if (kind == CdcGovernedArtifactKind.ConnectSourceOffsets)
                {
                    var config = await CallAsync(t => _connect.ReadConfigurationAsync(request, t), ct);
                    if (config is CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent)
                    {
                        var steps = journal
                            .Operations.Single(o => o.OperationId == operationId)
                            .Retirement.Single()
                            .Steps;
                        bool deletedByThisRetirement =
                            steps.Any(s =>
                                s.Kind == CdcRetirementStepKind.ConnectSourceOffsets
                                && s.VerifiedAt.Length == 1
                            ) && steps.Any(s => s.Kind == CdcRetirementStepKind.KafkaConnectConnector);
                        if (!deletedByThisRetirement)
                        {
                            // Failed-attempt cleanup needs independent empty offset evidence. A missing
                            // connector, missing workflow or HTTP 404 must never be relabeled offset absence.
                            var offset = await CallAsync(
                                t => _connect.ReadOffsetEvidenceAsync(request, t),
                                ct
                            );
                            if (offset is CdcTransportResult<CdcConnectOffsetEvidence>.Observed observed)
                            {
                                Require(observed.Value.State == CdcConnectOffsetState.Missing);
                            }
                            else
                            {
                                component = CdcDeploymentComponent.Kafka;
                                var retained = RequireObserved(
                                    await CallAsync(t => _kafka.InspectRetirementOffsetsAsync(scope, t), ct)
                                );
                                Require(retained == CdcRetirementOffsetState.Absent);
                            }
                        }
                        artifact = new(
                            kind,
                            scope.Artifact(kind).Name,
                            CdcCleanupState.NotFound,
                            "Verified offset absence and current connector absence."
                        );
                    }
                    else
                    {
                        RequireObserved(config);
                        artifact = RequireObserved(
                            await _connectCleanup.DeleteAsync(scope, kind, ct).WaitAsync(ct)
                        );
                    }
                }
                else
                {
                    var adapter = component switch
                    {
                        CdcDeploymentComponent.Connect => _connectCleanup,
                        CdcDeploymentComponent.ProviderSetup => _provider,
                        _ => _kafka,
                    };
                    artifact = RequireObserved(await adapter.DeleteAsync(scope, kind, ct).WaitAsync(ct));
                }
                Require(
                    artifact.ArtifactKind == kind
                        && artifact.ArtifactName == scope.Artifact(kind).Name
                        && Enum.IsDefined(artifact.CleanupState)
                );
                // Rebuild safe proof text; transport diagnostic strings never enter the journal/result.
                evidence[kind] = new(
                    kind,
                    artifact.ArtifactName,
                    artifact.CleanupState,
                    "Authoritative cleanup reconciliation verified absence."
                );
            }
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = exception switch
            {
                CleanupEvidenceException evidence => evidence.Diagnostic,
                OperationCanceledException or TimeoutException => new(
                    component,
                    CdcDeploymentFailure.Timeout
                ),
                CdcWorkflowStateException => new(component, CdcDeploymentFailure.ValidationFailed),
                _ => CdcDeploymentDiagnostic.FromException(component, exception),
            };
            return new(false, operationId, [diagnostic]);
        }

        async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct) =>
            await call(ct).WaitAsync(request.Timing.CallTimeout, ct);
    }

    private static T RequireObserved<T>(CdcTransportResult<T> result)
        where T : notnull =>
        result switch
        {
            CdcTransportResult<T>.Observed observed => observed.Value,
            CdcTransportResult<T>.Unavailable unavailable => throw new CleanupEvidenceException(
                unavailable.Diagnostic
            ),
            _ => throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory),
        };

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S3871",
        Justification = "Private control flow caught inside the controller."
    )]
    private sealed class CleanupEvidenceException(CdcDeploymentDiagnostic diagnostic) : Exception
    {
        internal CdcDeploymentDiagnostic Diagnostic { get; } = diagnostic;
    }
}

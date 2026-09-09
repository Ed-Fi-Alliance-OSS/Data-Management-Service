// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Fresh handoff to provider setup; neither publication readiness nor persisted authority.</summary>
public sealed record CdcInitialEnablementEvidence(
    InitialCdcProvisioningProof ProvisioningProof,
    InitialCdcEligibilityObservation Eligibility,
    CdcRetry Retry
);

/// <summary>
/// Initial pre-capture stage only. Hosts supply an initialized, configured-target runtime.
/// No caller ownership/writesClosed assertion is accepted.
/// Subsequent stages own provider/connector reconciliation once those effects have begun.
/// </summary>
public sealed class CdcInitialEnablement
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly TimeProvider _timeProvider;

    public CdcInitialEnablement(string stateRoot)
        : this(
            new LocalCdcWorkflowJournalStore(stateRoot),
            new CdcBindingLifecycleService(new LocalCdcBindingStateStore(stateRoot), TimeProvider.System),
            TimeProvider.System
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
    }

    internal CdcInitialEnablement(
        LocalCdcWorkflowJournalStore store,
        ICdcBindingLifecycleService bindings,
        TimeProvider timeProvider
    )
    {
        _store = store;
        _bindings = bindings;
        _timeProvider = timeProvider;
    }

    public async Task<CdcTransportResult<CdcInitialEnablementEvidence>> ActivateAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        var boundary = new OperationBoundary();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.CallTimeout);
        CancellationToken token = timeout.Token;
        try
        {
            await using var session = await _store.AcquireAsync(
                request.Timing.CallTimeout,
                request.Timing.PollInterval < request.Timing.CallTimeout
                    ? request.Timing.PollInterval
                    : request.Timing.CallTimeout,
                token
            );
            CdcBinding binding = request.Binding;
            CdcTargetIdentity target = binding.ToTargetIdentity();
            var journal = await session.ReadAsync(target, token);
            Require(
                journal.Operations.All(o =>
                    o.Effect
                        is CdcWorkflowEffect.CreateDatabase
                            or CdcWorkflowEffect.AssociateSource
                            or CdcWorkflowEffect.ReserveBinding
                            or CdcWorkflowEffect.ActivateProjection
                )
            );
            Require(
                journal.Operations.Count(o => o.Effect == CdcWorkflowEffect.ReserveBinding) <= 1
                    && journal.Operations.Count(o => o.Effect == CdcWorkflowEffect.ActivateProjection) <= 1
            );
            var history = await session.ReadSourcePublicationHistoryAsync(
                target,
                binding.PhysicalSourceFingerprint,
                token
            );
            Require(
                history.WorkflowId == journal.WorkflowId
                    && history.CreationTarget == target
                    && history.CreationReceipt.Outcome == CdcDatabaseCreationOutcome.Created
            );
            bool reserved = journal.Operations.Any(o => o.Effect == CdcWorkflowEffect.ReserveBinding);
            Require(
                history.Transitions[^1].Status
                    == (
                        reserved
                            ? DocumentCacheDownstreamPublicationStatus.Possible
                            : DocumentCacheDownstreamPublicationStatus.InternalOnly
                    )
            );

            string operationId = Guid.NewGuid().ToString("D");
            InitialCdcProvisioningProof proof = new(
                CdcJsonContract.CurrentContractVersion,
                Guid.NewGuid().ToString("D"),
                operationId,
                target,
                target.Provider,
                journal.WorkflowId.ToString("D"),
                CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
                CdcWriteAdmissionState.ClosedNeverOpened,
                _timeProvider.GetUtcNow()
            );
            boundary.Component = CdcDeploymentComponent.Projection;
            var (eligibility, runtimeTarget) = await ObserveAsync(
                runtime,
                binding,
                proof,
                request.Timing.MaximumObservationAge,
                token
            );
            boundary.Component = CdcDeploymentComponent.WorkflowState;
            await ValidateSourceInventoryAsync(binding, token);
            var exact = await _bindings.ExactMatchBindingAsync(binding, token);
            bool missing = exact.Status == CdcControlPlaneOperationStatus.BindingMissing;
            Require(missing || exact.Status == CdcControlPlaneOperationStatus.Succeeded);
            if (missing)
            {
                // An unfinished reservation may have crashed before CREATE. A completed one cannot
                // explain a missing binding, nor can any later operation authorize reconstruction.
                Require(journal.Operations.All(o => o.Effect != CdcWorkflowEffect.ActivateProjection));
                Require(
                    journal
                        .Operations.Where(o => o.Effect == CdcWorkflowEffect.ReserveBinding)
                        .All(o => o.Completions.IsEmpty)
                );
                var prebinding = CdcInitialEnableRetryClassifier.EvaluatePreBindingEligibility(
                    new(
                        operationId,
                        eligibility.ObservedAt,
                        _timeProvider.GetUtcNow(),
                        target,
                        binding.PhysicalSourceFingerprint,
                        proof,
                        eligibility
                    )
                );
                Require(prebinding.CanCreateBinding);
            }
            else
            {
                Require(reserved);
                var existingRetry = Classify(binding, proof, eligibility, exact);
                Require(CanProceed(existingRetry));
                Require(
                    existingRetry.RetryClassification
                        != CdcRetryClassification.ResumeProviderTopicConnectorSetup
                        || journal.Operations.Any(o => o.Effect == CdcWorkflowEffect.ActivateProjection)
                );
            }

            Require(
                _timeProvider.GetUtcNow() - eligibility.ObservedAt <= request.Timing.MaximumObservationAge
            );
            if (!reserved)
            {
                // RecordIntent persists irreversible Possible exposure BEFORE reservation intent.
                journal = await session.RecordIntentAsync(
                    target,
                    journal.WorkflowId,
                    Guid.NewGuid(),
                    CdcWorkflowEffect.ReserveBinding,
                    [],
                    token
                );
            }
            var reservation = journal.Operations.Single(o => o.Effect == CdcWorkflowEffect.ReserveBinding);
            if (missing)
            {
                exact = await _bindings.CreateBindingIfAbsentAsync(binding, token);
                Require(exact.Status == CdcControlPlaneOperationStatus.Succeeded);
            }
            journal = await session.ReconcileCompletionAsync(
                target,
                journal.WorkflowId,
                reservation.OperationId,
                async (_, ct) =>
                {
                    var read = await _bindings.ExactMatchBindingAsync(binding, ct);
                    Require(
                        read.Status == CdcControlPlaneOperationStatus.Succeeded
                            && read.State?.State == CdcBindingState.BindingPresent
                    );
                    return new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                        new CdcWorkflowCompletion.Reconciled()
                    );
                },
                token
            );

            var retry = Classify(binding, proof, eligibility, exact);
            Require(CanProceed(retry));
            bool activationIntended = journal.Operations.Any(o =>
                o.Effect == CdcWorkflowEffect.ActivateProjection
            );
            if (retry.RetryClassification == CdcRetryClassification.ResumeProviderTopicConnectorSetup)
            {
                // Tracking without our original activation intent is not a committed controller activation.
                Require(activationIntended);
            }
            else
            {
                Require(
                    !journal.Operations.Any(o =>
                        o.Effect == CdcWorkflowEffect.ActivateProjection && !o.Completions.IsEmpty
                    )
                );
                if (!activationIntended)
                {
                    journal = await session.RecordIntentAsync(
                        target,
                        journal.WorkflowId,
                        Guid.NewGuid(),
                        CdcWorkflowEffect.ActivateProjection,
                        [],
                        token
                    );
                }
                boundary.Component = CdcDeploymentComponent.Projection;
                var result = await runtime.ActivateAsync(
                    new(
                        DocumentCacheAdministrativeTargetKey.FromTargetKey(runtimeTarget),
                        new(binding.PhysicalSourceFingerprint),
                        DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation
                    ),
                    token
                );
                Require(
                    result.Status == DocumentCacheAdministrativeCommandStatus.Completed
                        && result.Classification == DocumentCacheAdministrativeCommandClassification.Succeeded
                );
            }
            boundary.Component = CdcDeploymentComponent.Projection;
            // A successful response or old completion is never sufficient: re-read the actual commit.
            var afterActivation = await ObserveAsync(
                runtime,
                binding,
                proof,
                request.Timing.MaximumObservationAge,
                token
            );
            Require(afterActivation.RuntimeTarget.Equals(runtimeTarget));
            eligibility = afterActivation.Eligibility;
            exact = await _bindings.ExactMatchBindingAsync(binding, token);
            retry = Classify(binding, proof, eligibility, exact);
            Require(retry.RetryClassification == CdcRetryClassification.ResumeProviderTopicConnectorSetup);
            boundary.Component = CdcDeploymentComponent.WorkflowState;
            var activation = journal.Operations.Single(o => o.Effect == CdcWorkflowEffect.ActivateProjection);
            await session.ReconcileCompletionAsync(
                target,
                journal.WorkflowId,
                activation.OperationId,
                (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                        new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                            new CdcWorkflowCompletion.Reconciled()
                        )
                    );
                },
                token
            );
            return new CdcTransportResult<CdcInitialEnablementEvidence>.Observed(
                new(proof, eligibility, retry)
            );
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Failure(boundary.Component, CdcDeploymentFailure.Timeout);
        }
        catch (CdcWorkflowStateException exception)
        {
            return Failure(
                boundary.Component,
                exception.Failure switch
                {
                    CdcWorkflowStateFailure.LockTimeout => CdcDeploymentFailure.Timeout,
                    CdcWorkflowStateFailure.Unavailable or CdcWorkflowStateFailure.Missing =>
                        CdcDeploymentFailure.Unavailable,
                    _ => CdcDeploymentFailure.ValidationFailed,
                }
            );
        }
        catch (Exception exception)
        {
            return new CdcTransportResult<CdcInitialEnablementEvidence>.Unavailable(
                CdcDeploymentDiagnostic.FromException(boundary.Component, exception)
            );
        }
    }

    private async Task ValidateSourceInventoryAsync(CdcBinding binding, CancellationToken token)
    {
        // The existing lifecycle service supplies the complete validated deployment inventory.
        // Per-identity create/exact-match alone cannot detect another configured alias of this source.
        var inventory = await _bindings.ListBindingsAsync(binding.DeploymentKey, token);
        Require(inventory.Status == CdcControlPlaneOperationStatus.Succeeded);
        foreach (var existing in inventory.States.Select(state => state.Binding))
        {
            Require(existing is not null && CdcBindingValidator.Validate(existing).Succeeded);
            Require(
                existing!.Provider != binding.Provider
                    || existing.PhysicalSourceFingerprint != binding.PhysicalSourceFingerprint
                    || existing.ToTargetIdentity() == binding.ToTargetIdentity()
            );
        }
    }

    internal async Task<(
        InitialCdcEligibilityObservation Eligibility,
        DocumentCacheTargetKey RuntimeTarget
    )> ObserveAsync(
        ICdcProjectionRuntime runtime,
        CdcBinding binding,
        InitialCdcProvisioningProof proof,
        TimeSpan maximumAge,
        CancellationToken token
    )
    {
        var observation = await ObserveCurrentDatabaseAsync(
            runtime,
            binding,
            proof.IssuedAt,
            maximumAge,
            token
        );
        InitialCdcEligibilityObservation eligibility = new(
            CdcJsonContract.CurrentContractVersion,
            proof.OperationId,
            observation.ObservedAt,
            observation.ObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            observation.PhysicalSourceFingerprint,
            proof.SetupControllerRunId,
            proof.ProofId,
            CdcConsistencyScope.SingleProviderTransaction,
            observation.Lifecycle.State switch
            {
                DocumentCacheLifecycleState.Disabled => CdcLifecycleState.Disabled,
                DocumentCacheLifecycleState.Tracking => CdcLifecycleState.Tracking,
                DocumentCacheLifecycleState.Resetting => CdcLifecycleState.Resetting,
                DocumentCacheLifecycleState.Rebuilding => CdcLifecycleState.Rebuilding,
                _ => CdcLifecycleState.Unknown,
            },
            observation.Lifecycle.CacheAheadRecoveryRequired
                ? CdcCacheAheadState.RecoveryRequired
                : CdcCacheAheadState.Clear,
            !observation.Tables.CanonicalDocumentsEmpty,
            !observation.Tables.DocumentCacheEmpty,
            !observation.Tables.DocumentProjectionWorkEmpty,
            observation.TransactionObservationId,
            []
        );
        return (eligibility, observation.TargetKey);
    }

    internal async Task<CdcInitialDatabaseObservation> ObserveCurrentDatabaseAsync(
        ICdcProjectionRuntime runtime,
        CdcBinding binding,
        DateTimeOffset notBefore,
        TimeSpan maximumAge,
        CancellationToken token,
        bool established = false
    )
    {
        var observation = established
            ? await runtime.ObserveEstablishedDatabaseAsync(token)
            : await runtime.ObserveInitialDatabaseAsync(token);
        token.ThrowIfCancellationRequested();
        Require(
            CdcTargetValidator.MapE18TenantKeyToBindingTenantKey(
                observation.TargetKey.TenantKey.ToLowerInvariant()
            ) == binding.TenantKey
                && observation.TargetKey.DataStoreId.ToString(CultureInfo.InvariantCulture)
                    == binding.DataStoreId
                && CdcProviderToken.TryToRelationalProviderToken(binding.Provider, out var provider)
                && provider == observation.Provider
                && observation.PhysicalSourceFingerprint == binding.PhysicalSourceFingerprint
                && observation.ObservedAt >= notBefore
                && observation.ObservedAt <= _timeProvider.GetUtcNow()
                && _timeProvider.GetUtcNow() - observation.ObservedAt <= maximumAge
        );
        return observation;
    }

    internal CdcRetry Classify(
        CdcBinding binding,
        InitialCdcProvisioningProof proof,
        InitialCdcEligibilityObservation eligibility,
        CdcBindingLifecycleResult exact
    )
    {
        Require(exact.Status == CdcControlPlaneOperationStatus.Succeeded);
        return CdcInitialEnableRetryClassifier.EvaluateRetry(
            new(
                proof.OperationId,
                _timeProvider.GetUtcNow(),
                _timeProvider.GetUtcNow(),
                binding.ToTargetIdentity(),
                binding.PhysicalSourceFingerprint,
                proof,
                eligibility,
                exact.State
            )
        );
    }

    private sealed class OperationBoundary
    {
        public CdcDeploymentComponent Component { get; set; } = CdcDeploymentComponent.WorkflowState;
    }

    private static bool CanProceed(CdcRetry retry) =>
        retry.RetryClassification
            is CdcRetryClassification.RetryGuardedActivation
                or CdcRetryClassification.ResumeProviderTopicConnectorSetup;

    private static void Require(bool condition) =>
        CdcWorkflowJournalValidation.Require(condition, CdcWorkflowStateFailure.Contradictory);

    private static CdcTransportResult<CdcInitialEnablementEvidence> Failure(
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure
    ) => new CdcTransportResult<CdcInitialEnablementEvidence>.Unavailable(new(component, failure));
}

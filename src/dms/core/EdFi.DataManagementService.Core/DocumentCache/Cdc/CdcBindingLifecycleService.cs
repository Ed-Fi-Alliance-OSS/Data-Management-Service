// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

internal sealed class CdcBindingLifecycleService(ICdcBindingStateStore stateStore, TimeProvider timeProvider)
    : ICdcBindingLifecycleService
{
    private readonly ICdcBindingStateStore _stateStore =
        stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<CdcBindingLifecycleResult> CreateBindingIfAbsentAsync(
        CdcBinding binding,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(binding);

        CdcCreateBindingStateStoreResult result = await _stateStore
            .CreateBindingIfAbsentAsync(binding, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            CdcCreateBindingStateStoreResult.Created created => Succeeded(created.State),
            CdcCreateBindingStateStoreResult.ExistingExactMatch existing => Succeeded(existing.State),
            CdcCreateBindingStateStoreResult.BindingMismatch mismatch => BindingMismatch(mismatch.Mismatch),
            CdcCreateBindingStateStoreResult.StateStoreFailure failure => Failure(failure.Failure),
            _ => StateStoreUnavailable("CDC binding create returned an unsupported result."),
        };
    }

    public async Task<CdcBindingLifecycleResult> ReadBindingAsync(
        CdcBindingIdentity identity,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(identity);

        CdcReadBindingStateStoreResult result = await _stateStore
            .ReadBindingAsync(identity, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            CdcReadBindingStateStoreResult.Found found => Succeeded(found.State),
            CdcReadBindingStateStoreResult.Missing => BindingMissing(),
            CdcReadBindingStateStoreResult.StateStoreFailure failure => Failure(failure.Failure),
            _ => StateStoreUnavailable("CDC binding read returned an unsupported result."),
        };
    }

    public async Task<CdcBindingLifecycleResult> ExactMatchBindingAsync(
        CdcBinding binding,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(binding);

        CdcExactMatchBindingStateStoreResult result = await _stateStore
            .ExactMatchBindingAsync(binding, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            CdcExactMatchBindingStateStoreResult.ExactMatch exact => Succeeded(exact.State),
            CdcExactMatchBindingStateStoreResult.BindingMissing => BindingMissing(),
            CdcExactMatchBindingStateStoreResult.BindingMismatch mismatch => BindingMismatch(
                mismatch.Mismatch
            ),
            CdcExactMatchBindingStateStoreResult.StateStoreFailure failure => Failure(failure.Failure),
            _ => StateStoreUnavailable("CDC binding exact-match returned an unsupported result."),
        };
    }

    public async Task<CdcBindingLifecycleListResult> ListBindingsAsync(
        string deploymentKey,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentKey);

        CdcListBindingsStateStoreResult result = await _stateStore
            .ListBindingsAsync(deploymentKey, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            CdcListBindingsStateStoreResult.Listed listed => new(
                CdcJsonContract.CurrentContractVersion,
                ObservedAt(),
                CdcControlPlaneOperationStatus.Succeeded,
                listed.States.Select(ToContract).ToArray(),
                []
            ),
            CdcListBindingsStateStoreResult.StateStoreFailure failure => ListFailure(failure.Failure),
            _ => ListStateStoreUnavailable("CDC binding list returned an unsupported result."),
        };
    }

    public async Task<CdcRetirementListResult> ListRetirementsAsync(
        string deploymentKey,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentKey);

        CdcListRetirementsStateStoreResult result = await _stateStore
            .ListRetirementsAsync(deploymentKey, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset observedAt = ObservedAt();
        return result switch
        {
            CdcListRetirementsStateStoreResult.Listed listed => new(
                CdcJsonContract.CurrentContractVersion,
                observedAt,
                CdcControlPlaneOperationStatus.Succeeded,
                listed.Retirements,
                []
            ),
            CdcListRetirementsStateStoreResult.StateStoreFailure failure => new(
                CdcJsonContract.CurrentContractVersion,
                observedAt,
                failure.Failure.Kind == CdcStateStoreFailureKind.InvalidOperation
                    ? CdcControlPlaneOperationStatus.InvalidOperation
                    : CdcControlPlaneOperationStatus.StateStoreUnavailable,
                [],
                failure.Failure.Diagnostics
            ),
            _ => new(
                CdcJsonContract.CurrentContractVersion,
                observedAt,
                CdcControlPlaneOperationStatus.StateStoreUnavailable,
                [],
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        observedAt,
                        "$",
                        "CDC retirement list returned an unsupported result."
                    ),
                ]
            ),
        };
    }

    public async Task<CdcBindingLifecycleResult> LatchSourceHistoryLossAsync(
        CdcIncident incident,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(incident);

        CdcLatchIncidentStateStoreResult result = await _stateStore
            .LatchSourceHistoryLossAsync(incident, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            CdcLatchIncidentStateStoreResult.Latched latched => Succeeded(latched.State),
            CdcLatchIncidentStateStoreResult.AlreadyLatched alreadyLatched => Succeeded(alreadyLatched.State),
            CdcLatchIncidentStateStoreResult.BindingMissing => BindingMissing(),
            CdcLatchIncidentStateStoreResult.BindingMismatch mismatch => BindingMismatch(mismatch.Mismatch),
            CdcLatchIncidentStateStoreResult.StateStoreFailure failure => Failure(failure.Failure),
            _ => StateStoreUnavailable("CDC incident latch returned an unsupported result."),
        };
    }

    public async Task<CdcBindingLifecycleResult> ImportVerifiedBindingAsync(
        CdcAdoptionProof verifiedAdoptionProof,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(verifiedAdoptionProof);

        CdcImportBindingStateStoreResult result = await _stateStore
            .ImportVerifiedBindingAsync(verifiedAdoptionProof, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            CdcImportBindingStateStoreResult.Imported imported => Succeeded(imported.State),
            CdcImportBindingStateStoreResult.ExistingExactMatch existing => Succeeded(existing.State),
            CdcImportBindingStateStoreResult.BindingMismatch mismatch => BindingMismatch(mismatch.Mismatch),
            CdcImportBindingStateStoreResult.StateStoreFailure failure => Failure(failure.Failure),
            _ => StateStoreUnavailable("CDC binding import returned an unsupported result."),
        };
    }

    public async Task<CdcBindingLifecycleResult> DeleteStateAfterVerifiedCleanupAsync(
        CdcCleanupProof verifiedCleanupProof,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(verifiedCleanupProof);

        CdcDeleteBindingStateStoreResult result = await _stateStore
            .DeleteStateAfterVerifiedCleanupAsync(verifiedCleanupProof, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            CdcDeleteBindingStateStoreResult.Deleted => new(
                CdcJsonContract.CurrentContractVersion,
                ObservedAt(),
                CdcControlPlaneOperationStatus.Succeeded,
                new(
                    CdcJsonContract.CurrentContractVersion,
                    ObservedAt(),
                    CdcBindingState.BindingMissing,
                    null,
                    null
                ),
                []
            ),
            CdcDeleteBindingStateStoreResult.BindingMissing => BindingMissing(),
            CdcDeleteBindingStateStoreResult.StateStoreFailure failure => Failure(failure.Failure),
            _ => StateStoreUnavailable("CDC binding delete returned an unsupported result."),
        };
    }

    private CdcBindingLifecycleResult Succeeded(CdcStoredBindingState state) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ObservedAt(),
            CdcControlPlaneOperationStatus.Succeeded,
            ToContract(state),
            []
        );

    private CdcBindingLifecycleResult BindingMissing() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ObservedAt(),
            CdcControlPlaneOperationStatus.BindingMissing,
            new(
                CdcJsonContract.CurrentContractVersion,
                ObservedAt(),
                CdcBindingState.BindingMissing,
                null,
                null
            ),
            []
        );

    private CdcBindingLifecycleResult BindingMismatch(CdcBindingMismatch mismatch)
    {
        ArgumentNullException.ThrowIfNull(mismatch);

        DateTimeOffset observedAt = ObservedAt();
        return new(
            CdcJsonContract.CurrentContractVersion,
            observedAt,
            CdcControlPlaneOperationStatus.BindingMismatch,
            new(
                CdcJsonContract.CurrentContractVersion,
                observedAt,
                CdcBindingState.BindingMismatch,
                mismatch.PersistedBinding,
                null
            ),
            CreateMismatchDiagnostics(mismatch, observedAt)
        );
    }

    private CdcBindingLifecycleResult Failure(CdcStateStoreFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new(
            CdcJsonContract.CurrentContractVersion,
            ObservedAt(),
            failure.Kind == CdcStateStoreFailureKind.InvalidOperation
                ? CdcControlPlaneOperationStatus.InvalidOperation
                : CdcControlPlaneOperationStatus.StateStoreUnavailable,
            null,
            failure.Diagnostics
        );
    }

    private CdcBindingLifecycleListResult ListFailure(CdcStateStoreFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new(
            CdcJsonContract.CurrentContractVersion,
            ObservedAt(),
            failure.Kind == CdcStateStoreFailureKind.InvalidOperation
                ? CdcControlPlaneOperationStatus.InvalidOperation
                : CdcControlPlaneOperationStatus.StateStoreUnavailable,
            [],
            failure.Diagnostics
        );
    }

    private CdcBindingLifecycleResult StateStoreUnavailable(string message)
    {
        DateTimeOffset observedAt = ObservedAt();
        return new(
            CdcJsonContract.CurrentContractVersion,
            observedAt,
            CdcControlPlaneOperationStatus.StateStoreUnavailable,
            null,
            [new(CdcDiagnosticCategory.LocalStateUnavailable, observedAt, "$", message)]
        );
    }

    private CdcBindingLifecycleListResult ListStateStoreUnavailable(string message)
    {
        DateTimeOffset observedAt = ObservedAt();
        return new(
            CdcJsonContract.CurrentContractVersion,
            observedAt,
            CdcControlPlaneOperationStatus.StateStoreUnavailable,
            [],
            [new(CdcDiagnosticCategory.LocalStateUnavailable, observedAt, "$", message)]
        );
    }

    private CdcBindingStateContract ToContract(CdcStoredBindingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new(
            CdcJsonContract.CurrentContractVersion,
            ObservedAt(),
            state.Incident is null ? CdcBindingState.BindingPresent : CdcBindingState.IncidentLatched,
            state.Binding,
            state.Incident
        );
    }

    private static IReadOnlyList<CdcDiagnostic> CreateMismatchDiagnostics(
        CdcBindingMismatch mismatch,
        DateTimeOffset observedAt
    ) =>
        mismatch
            .Differences.Select(difference => new CdcDiagnostic(
                CdcDiagnosticCategory.BindingMismatch,
                observedAt,
                $"$.{difference.FieldName}",
                "CDC persisted binding state does not exactly match the requested binding."
            ))
            .Take(CdcDiagnostic.MaximumDiagnostics)
            .ToArray();

    private DateTimeOffset ObservedAt() => _timeProvider.GetUtcNow().ToUniversalTime();
}

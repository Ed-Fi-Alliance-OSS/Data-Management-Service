// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Live provider facts, never ownership or writer-admission authority.</summary>
public sealed record CdcInitialDatabaseObservation(
    DocumentCacheTargetKey TargetKey,
    RelationalProviderToken Provider,
    string PhysicalSourceFingerprint,
    DateTimeOffset ObservedAt,
    DocumentCacheLifecycleObservation Lifecycle,
    DocumentCacheGuardedNewEmptyActivationState Tables,
    string TransactionObservationId
);

internal static class CdcInitialDatabaseInspector
{
    internal static async Task<CdcInitialDatabaseObservation> ObserveAsync(
        IServiceProvider provider,
        DocumentCacheTargetKey targetKey,
        CancellationToken cancellationToken,
        bool established = false
    )
    {
        var registry = provider.GetRequiredService<IDocumentCacheTargetRegistry>();
        var snapshot = await DocumentCacheRuntime.DocumentCacheRuntimeTargetResolver.ResolveAsync(
            registry,
            targetKey,
            cancellationToken
        );
        if (
            !DocumentCacheRuntime.DocumentCacheRuntimeTargetResolver.ContainsOnlyTarget(snapshot, targetKey)
            || snapshot.Targets[0].ResolutionState != DocumentCacheTargetResolutionState.Resolved
            || snapshot.Targets[0].EligibilityState != DocumentCacheTargetEligibilityState.Eligible
            || registry.CurrentRuntimeSnapshot.GetExecutionContext(targetKey) is not { } context
        )
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory);
        }
        // Refresh the physical-source evidence even if the registry retained an unchanged context.
        var source = await provider
            .GetRequiredService<IDocumentCachePhysicalSourceFingerprintReader>()
            .ReadFingerprintAsync(context.ConnectionInput.Value, cancellationToken);
        if (!source.Succeeded || source.Fingerprint != context.PhysicalSourceFingerprint)
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory);
        }
        var primitives = provider.GetRequiredService<IDocumentCacheAdministrativePrimitives>();
        await using var mutex = await provider
            .GetRequiredService<IDocumentCacheAdministrativeMutex>()
            .AcquireAsync(context.ConnectionInput, cancellationToken);
        await using var transaction = await mutex.BeginTransactionAsync(
            established ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            cancellationToken
        );
        // Initial eligibility needs serializable table-absence evidence. Established validation uses
        // source/lifecycle/latch facts only: retain the shared lifecycle-row lock, but allow a current
        // row version after concurrent projection writes instead of aborting a serializable snapshot.
        // Established table counts must never be promoted into an initial empty-database proof.
        var lifecycle = await primitives.ReadLifecycleAsync(
            transaction,
            DocumentCacheAdministrativeStateLockMode.Shared,
            cancellationToken
        );
        var tables = await primitives.ReadGuardedNewEmptyActivationStateAsync(transaction, cancellationToken);
        var prerequisites = await primitives.ValidateActivationPrerequisitesAsync(
            transaction,
            cancellationToken
        );
        if (!lifecycle.Succeeded || prerequisites.FailureCategory is not null)
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory);
        }
        var observation = new CdcInitialDatabaseObservation(
            targetKey,
            context.ProviderToken,
            source.Fingerprint!.Value,
            DateTimeOffset.UtcNow,
            lifecycle.Lifecycle!,
            tables,
            Guid.NewGuid().ToString("D")
        );
        await transaction.RollbackAsync(cancellationToken);
        return observation;
    }
}

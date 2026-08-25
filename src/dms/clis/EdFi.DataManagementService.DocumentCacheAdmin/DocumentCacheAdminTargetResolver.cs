// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal enum DocumentCacheAdminTargetResolutionOutcome
{
    Completed,
    UnexpectedTargetMembership,
}

internal sealed record DocumentCacheAdminTargetResolutionResult(
    DocumentCacheAdminTargetResolutionOutcome Outcome,
    DocumentCacheTargetKey TargetKey,
    DocumentCacheTargetObservation? Observation,
    DocumentCacheTargetExecutionContext? ExecutionContext,
    DocumentCacheTargetRegistrySnapshot RegistrySnapshot,
    DocumentCacheTargetRuntimeSnapshot RuntimeSnapshot,
    string? FailureMessage
)
{
    public bool HasCompleteObservation =>
        Outcome == DocumentCacheAdminTargetResolutionOutcome.Completed && Observation is not null;

    public bool CanAttemptMutation =>
        Observation is not null
        && Observation.ResolutionState == DocumentCacheTargetResolutionState.Resolved
        && Observation.EligibilityState == DocumentCacheTargetEligibilityState.Eligible
        && ExecutionContext is not null;
}

internal interface IDocumentCacheAdminTargetResolver
{
    Task<DocumentCacheAdminTargetResolutionResult> ResolveAsync(
        DocumentCacheTargetKey targetKey,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheAdminTargetResolver(IDocumentCacheTargetRegistry targetRegistry)
    : IDocumentCacheAdminTargetResolver
{
    public async Task<DocumentCacheAdminTargetResolutionResult> ResolveAsync(
        DocumentCacheTargetKey targetKey,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        DocumentCacheTargetRegistrySnapshot registrySnapshot = await targetRegistry
            .RefreshAsync(DocumentCacheTargetRefreshReason.Startup, cancellationToken)
            .ConfigureAwait(false);
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = targetRegistry.CurrentRuntimeSnapshot;

        if (registrySnapshot.Targets.Length != 1 || !registrySnapshot.Targets[0].TargetKey.Equals(targetKey))
        {
            return new DocumentCacheAdminTargetResolutionResult(
                DocumentCacheAdminTargetResolutionOutcome.UnexpectedTargetMembership,
                targetKey,
                Observation: null,
                ExecutionContext: null,
                registrySnapshot,
                runtimeSnapshot,
                "DocumentCache target registry did not contain exactly the invocation target."
            );
        }

        DocumentCacheTargetObservation observation = registrySnapshot.Targets[0];
        DocumentCacheTargetExecutionContext? executionContext = observation.Generation is null
            ? runtimeSnapshot.GetExecutionContext(targetKey)
            : runtimeSnapshot.GetExecutionContext(targetKey, observation.Generation);

        return new DocumentCacheAdminTargetResolutionResult(
            DocumentCacheAdminTargetResolutionOutcome.Completed,
            targetKey,
            observation,
            executionContext,
            registrySnapshot,
            runtimeSnapshot,
            FailureMessage: null
        );
    }
}

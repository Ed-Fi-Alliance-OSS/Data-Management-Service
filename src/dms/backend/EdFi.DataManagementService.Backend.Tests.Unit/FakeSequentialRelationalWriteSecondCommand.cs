// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Runs the second command as the sequential commands the executor issued before it existed: the namespace
/// check through the session's command executor, then the relationship check through the persister, then —
/// in DML mode — persistence through the persister.
/// </summary>
/// <remarks>
/// Executor orchestration tests assert precedence and result shape, not emitted SQL, and they arrange
/// denials through the session's authorization command executor and the persister seam. Substituting the
/// sequential shape keeps those arrangements meaningful and keeps each executor test focused on the
/// ordering it exists to pin. The composite command's own statement order, ordered-segment fallback, and
/// failure mapping are covered by <c>Given_The_Composite_Relational_Write_Second_Command</c>.
/// </remarks>
internal sealed class FakeSequentialRelationalWriteSecondCommand(
    IRelationalWritePersister persister,
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null
) : IRelationalWriteSecondCommandPhase
{
    private readonly IRelationalWritePersister _persister = persister;

    private readonly ProposedNamespaceAuthorizationOrchestrator _namespaceOrchestrator = new(
        providerFailureExtractor
    );

    private readonly ProposedRelationshipAuthorizationOrchestrator _relationshipOrchestrator = new(persister);

    public int ResolveCallCount { get; private set; }

    public async Task<RelationalWriteSecondCommandResolution> ResolveAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        RelationalWriteSecondCommandMode mode,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ResolveCallCount++;

        var namespaceBoundary = await _namespaceOrchestrator
            .ResolveAsync(request, mergeResult, writeSession, cancellationToken)
            .ConfigureAwait(false);

        if (namespaceBoundary.ImmediateResult is not null)
        {
            return new RelationalWriteSecondCommandResolution(
                mergeResult,
                null,
                namespaceBoundary.ImmediateResult
            );
        }

        // Authorization-only mode forces the standalone relationship check because it issues no
        // dms.Document insert for the POST create path to prefix the check onto.
        var relationshipBoundary = await _relationshipOrchestrator
            .ResolveAsync(
                request,
                mergeResult,
                writeSession,
                cancellationToken,
                forceStandaloneAuthorization: mode is RelationalWriteSecondCommandMode.AuthorizationOnly
            )
            .ConfigureAwait(false);

        if (
            relationshipBoundary.ImmediateResult is not null
            || mode is RelationalWriteSecondCommandMode.AuthorizationOnly
        )
        {
            return new RelationalWriteSecondCommandResolution(
                relationshipBoundary.MergeResult,
                null,
                relationshipBoundary.ImmediateResult
            );
        }

        return new RelationalWriteSecondCommandResolution(
            relationshipBoundary.MergeResult,
            await _persister
                .PersistAsync(request, relationshipBoundary.MergeResult, writeSession, cancellationToken)
                .ConfigureAwait(false),
            null
        );
    }
}

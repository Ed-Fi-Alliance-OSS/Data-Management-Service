// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Runs proposed-value authorization as the two sequential commands the executor issued before the
/// authorization-only second command existed: the namespace check through the session's command executor,
/// then the relationship check through the persister.
/// </summary>
/// <remarks>
/// Executor orchestration tests assert precedence and result shape, not emitted SQL, and they arrange
/// denials through the session's authorization command executor and the persister seam. Substituting the
/// sequential shape keeps those arrangements meaningful and keeps each executor test focused on the
/// ordering it exists to pin. The composite command's own statement order, ordered-segment fallback, and
/// failure mapping are covered by <c>Given_The_Composite_Relational_Write_Proposed_Authorization</c>.
/// <para>
/// The relationship check is forced standalone because this phase only ever runs where no
/// <c>dms.Document</c> insert follows, so there is nothing for the POST create path to prefix it onto.
/// </para>
/// </remarks>
internal sealed class FakeSequentialRelationalWriteProposedAuthorization(
    IRelationalWritePersister persister,
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null
) : IRelationalWriteProposedAuthorizationPhase
{
    private readonly ProposedNamespaceAuthorizationOrchestrator _namespaceOrchestrator = new(
        providerFailureExtractor
    );

    private readonly ProposedRelationshipAuthorizationOrchestrator _relationshipOrchestrator = new(persister);

    public int ResolveCallCount { get; private set; }

    public async Task<RelationalWriteProposedAuthorizationResolution> ResolveAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
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
            return new RelationalWriteProposedAuthorizationResolution(
                mergeResult,
                namespaceBoundary.ImmediateResult
            );
        }

        var relationshipBoundary = await _relationshipOrchestrator
            .ResolveAsync(
                request,
                mergeResult,
                writeSession,
                cancellationToken,
                forceStandaloneAuthorization: true
            )
            .ConfigureAwait(false);

        return new RelationalWriteProposedAuthorizationResolution(
            relationshipBoundary.MergeResult,
            relationshipBoundary.ImmediateResult
        );
    }
}

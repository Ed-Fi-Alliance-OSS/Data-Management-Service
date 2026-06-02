// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal sealed class ProposedRelationshipAuthorizationOrchestrator(IRelationalWritePersister persister)
{
    private readonly IRelationalWritePersister _persister =
        persister ?? throw new ArgumentNullException(nameof(persister));

    public async Task<ProposedRelationshipAuthorizationBoundary> ResolveAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergeResult);
        ArgumentNullException.ThrowIfNull(writeSession);

        switch (request.ProposedRelationshipAuthorization)
        {
            case null:
            case RelationshipAuthorizationResult.NoAuthorizationRequired:
            case RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                break;

            // NoClaims is deferred from POST or PUT preflight so the proposed namespace check can run
            // first (namespace AND-composes before the relationship OR-group per auth.md). The
            // namespace orchestrator runs ahead of this one in the executor, so reaching this
            // branch means namespace authorized — surface the deferred denial now.
            case RelationshipAuthorizationResult.NoClaims noClaims:
                return new ProposedRelationshipAuthorizationBoundary(
                    mergeResult,
                    RelationalWriteExecutorResults.BuildNoClaimsRelationshipAuthorizationResult(
                        request.OperationKind,
                        noClaims
                    )
                );

            case RelationshipAuthorizationResult.Authorized authorized:
                var finalizedRootRow = RelationalWriteFinalizedRootRow.Build(request, mergeResult);
                var extractionResult = RelationshipAuthorizationProposedValueExtractor.Extract(
                    authorized,
                    finalizedRootRow,
                    RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(
                        request.OperationKind
                    ),
                    request.TargetContext
                );

                switch (extractionResult)
                {
                    case ProposedRelationshipAuthorizationExtractionResult.Ready ready:
                        mergeResult = mergeResult with
                        {
                            ProposedRelationshipAuthorizationRuntimeCheck = ready.RuntimeCheck,
                        };
                        break;

                    case ProposedRelationshipAuthorizationExtractionResult.InvalidAuthorizationPlan invalid:
                        return new ProposedRelationshipAuthorizationBoundary(
                            mergeResult,
                            RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                                request.OperationKind,
                                [invalid.FailureMessage]
                            )
                        );

                    default:
                        throw new InvalidOperationException(
                            $"Unsupported proposed relationship authorization extraction result '{extractionResult.GetType().Name}'."
                        );
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported proposed relationship authorization result '{request.ProposedRelationshipAuthorization.GetType().Name}'."
                );
        }

        await AuthorizeAsync(request, mergeResult, writeSession, cancellationToken).ConfigureAwait(false);

        return new ProposedRelationshipAuthorizationBoundary(mergeResult, null);
    }

    private async Task AuthorizeAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (mergeResult.ProposedRelationshipAuthorizationRuntimeCheck is null)
        {
            return;
        }

        if (IsHandledByPostInlineAuth1(request))
        {
            return;
        }

        await _persister
            .AuthorizeProposedRelationshipAsync(request, mergeResult, writeSession, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsHandledByPostInlineAuth1(RelationalWriteExecutorRequest request) =>
        request.OperationKind is RelationalWriteOperationKind.Post
        && request.TargetContext is RelationalWriteTargetContext.CreateNew
        && request.WritePrecondition is not WritePrecondition.IfMatch;
}

internal sealed record ProposedRelationshipAuthorizationBoundary(
    RelationalWriteMergeResult MergeResult,
    RelationalWriteExecutorResult? ImmediateResult
);

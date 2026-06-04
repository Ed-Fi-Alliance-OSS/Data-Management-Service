// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Runs proposed-value namespace authorization as a separate statement inside the write session,
/// after merge has produced the finalized root row and before persist/commit. It is the namespace
/// sibling of <see cref="ProposedRelationshipAuthorizationOrchestrator"/>: it reads the namespace
/// value from the finalized merged root row (never the raw request body) and binds it to the same
/// single-record namespace SQL used by reads, so the <c>LIKE</c> semantics and AUTH1 failure mapping
/// stay identical across read and write paths.
/// </summary>
/// <remarks>
/// This orchestrator authorizes the proposed namespace value for both new-document and existing-target
/// writes. The stored namespace of an existing target is authorized separately in the locked-target
/// boundary, so this stage only ever evaluates the finalized proposed value.
/// </remarks>
internal sealed class ProposedNamespaceAuthorizationOrchestrator(
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null
)
{
    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        providerFailureExtractor ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    public async Task<ProposedNamespaceAuthorizationBoundary> ResolveAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergeResult);
        ArgumentNullException.ThrowIfNull(writeSession);

        var namespaceAuthorization = request.ProposedNamespaceAuthorization;

        if (namespaceAuthorization is null)
        {
            return new ProposedNamespaceAuthorizationBoundary(null);
        }

        var extraction = ProposedNamespaceValueExtractor.Extract(
            namespaceAuthorization.Checks,
            RelationalWriteFinalizedRootRow.Build(request, mergeResult)
        );

        if (extraction is ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan invalid)
        {
            return new ProposedNamespaceAuthorizationBoundary(
                RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                    request.OperationKind,
                    [invalid.FailureMessage],
                    AuthorizationSecurityConfigurationDiagnostics.ForNamespaceProposedValueExtraction(
                        namespaceAuthorization.Checks
                    )
                )
            );
        }

        var ready = (ProposedNamespaceValueExtractionResult.Ready)extraction;

        var namespaceExecutor = new NamespaceAuthorizationExecutor(
            writeSession.CreateCommandExecutor(),
            _providerFailureExtractor
        );

        var executionResult = await namespaceExecutor
            .ExecuteAsync(
                new NamespaceAuthorizationExecutionRequest(
                    request.MappingSet,
                    // Proposed-only checks evaluate the bound proposed value; no stored DocumentId is bound.
                    DocumentId: 0L,
                    ready.ProposedNamespace,
                    namespaceAuthorization.Checks,
                    namespaceAuthorization.NamespacePrefixParameterization
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return executionResult switch
        {
            NamespaceAuthorizationExecutionResult.Authorized => new ProposedNamespaceAuthorizationBoundary(
                null
            ),
            NamespaceAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                new ProposedNamespaceAuthorizationBoundary(
                    RelationalWriteExecutorResults.BuildNamespaceAuthorizationFailureResult(
                        request.OperationKind,
                        notAuthorized.Failure
                    )
                ),
            NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                new ProposedNamespaceAuthorizationBoundary(
                    RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                        request.OperationKind,
                        [invalidFailure.FailureMessage],
                        invalidFailure.Diagnostics
                    )
                ),
            // Proposed-value checks bind no stored DocumentId, so they never raise the stale stored-target
            // kind; map it defensively to the same write-conflict/not-exists shape the stored path uses.
            NamespaceAuthorizationExecutionResult.StaleTarget => new ProposedNamespaceAuthorizationBoundary(
                RelationalWriteExecutorResults.BuildStaleTargetResult(request.OperationKind)
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported namespace authorization execution result '{executionResult.GetType().Name}'."
            ),
        };
    }
}

internal sealed record ProposedNamespaceAuthorizationBoundary(RelationalWriteExecutorResult? ImmediateResult);

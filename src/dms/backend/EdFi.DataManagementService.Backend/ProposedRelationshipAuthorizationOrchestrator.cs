// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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

        if (request.ProposedRelationshipAuthorization is not null)
        {
            var finalizedRootRow = BuildFinalizedRootRowBuffer(request, mergeResult);
            var extractionResult = RelationshipAuthorizationProposedValueExtractor.Extract(
                request.ProposedRelationshipAuthorization,
                finalizedRootRow,
                RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(request.OperationKind),
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

    private static RootWriteRowBuffer BuildFinalizedRootRowBuffer(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult
    )
    {
        var rootTable = GetRootTableWritePlan(request.WritePlan);
        var rootTableState = mergeResult.TablesInDependencyOrder.SingleOrDefault(tableState =>
            tableState.TableWritePlan.TableModel.Table.Equals(rootTable.TableModel.Table)
        );

        if (rootTableState is null)
        {
            throw new InvalidOperationException(
                $"Relational write merge result did not include the root table '{rootTable.TableModel.Table}'."
            );
        }

        if (rootTableState.MergedRows.Length != 1)
        {
            throw new InvalidOperationException(
                $"Relational write merge result for root table '{rootTable.TableModel.Table}' "
                    + $"included {rootTableState.MergedRows.Length} merged rows; expected exactly one."
            );
        }

        return new RootWriteRowBuffer(rootTableState.TableWritePlan, rootTableState.MergedRows[0].Values);
    }

    private static TableWritePlan GetRootTableWritePlan(ResourceWritePlan writePlan)
    {
        var rootPlans = writePlan
            .TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind is DbTableKind.Root
            )
            .Take(2)
            .ToArray();

        return rootPlans.Length switch
        {
            1 => rootPlans[0],
            0 => throw new InvalidOperationException(
                $"Write plan for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}' does not contain a root table plan."
            ),
            _ => throw new InvalidOperationException(
                $"Write plan for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}' contains multiple root table plans."
            ),
        };
    }
}

internal sealed record ProposedRelationshipAuthorizationBoundary(
    RelationalWriteMergeResult MergeResult,
    RelationalWriteExecutorResult? ImmediateResult
);

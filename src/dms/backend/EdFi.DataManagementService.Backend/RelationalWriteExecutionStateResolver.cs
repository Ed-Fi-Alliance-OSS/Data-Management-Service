// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Owns the etag precondition policy for the write executor: whether a precondition applies, when it
/// is evaluated relative to proposed authorization, and how the deferred evaluation resolves against
/// the current state the first phase hydrated. Target resolution, locking, and current-state loading
/// themselves live in the composite first phase, which observes them in one command.
/// </summary>
internal sealed class RelationalWriteExecutionStateResolver(
    ILogger<RelationalWriteExecutionStateResolver> logger
)
{
    private readonly ILogger<RelationalWriteExecutionStateResolver> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// True when the request carries an HTTP conditional write precondition (If-Match) whose current
    /// existence/etag the write flow must resolve. The proceed-vs-412 outcome is centralized in
    /// <see cref="EtagPreconditionEvaluator"/>.
    /// </summary>
    internal static bool HasEtagPrecondition(WritePrecondition precondition) =>
        precondition switch
        {
            WritePrecondition.None => false,
            WritePrecondition.IfMatch => true,
            _ => throw new ArgumentOutOfRangeException(
                nameof(precondition),
                precondition,
                "Unsupported write precondition type."
            ),
        };

    /// <remarks>
    /// A create whose ownership verdict denies it owes that 403 or token-cap 500 ahead of its If-Match 412,
    /// exactly like a proposed authorization failure, so the precondition is deferred behind the second
    /// command that returns the verdict. Gated on the create target: a shared-policy POST carries the verdict
    /// on either branch, and an update never owes it.
    /// </remarks>
    public static EtagPreconditionEvaluation GetEtagPreconditionEvaluation(
        RelationalWriteExecutorRequest request
    ) =>
        GetEtagPreconditionEvaluation(
            request.WritePrecondition,
            request.ProposedRelationshipAuthorization,
            request.StoredNamespaceAuthorization,
            request.ProposedNamespaceAuthorization,
            hasPendingCreateOwnershipFailure: request.TargetContext is RelationalWriteTargetContext.CreateNew
                && request.DeferredCreateOwnershipFailureResult is not null
        );

    /// <summary>
    /// The same evaluation-mode decision computed from the unresolved input, so composite first-phase
    /// emission — which happens before the target is observed — cannot drift from the resolved
    /// request's decision.
    /// </summary>
    /// <summary>
    /// The evaluation before the target is known. A POST whose branches differ defers when either branch
    /// would, so work planned ahead of the target (the hydrated descriptor projection) covers both; the
    /// executor's own decision is made from the selected branch once the target is resolved.
    /// </summary>
    public static EtagPreconditionEvaluation GetEtagPreconditionEvaluation(RelationalWriteExecutorInput input)
    {
        var evaluation = GetUnresolvedEtagPreconditionEvaluation(input);

        return
            evaluation is EtagPreconditionEvaluation.BeforeProposedAuthorization
            && input.PostTargetAuthorizationBundles?.CreateNew
                is PostBranchAuthorization.Authorized createNewBranch
            ? GetUnresolvedEtagPreconditionEvaluation(input.WithPostBranchInputs(createNewBranch.Inputs))
            : evaluation;
    }

    // The create ownership verdict is left out deliberately. This decision only shapes the current-state
    // hydration planned ahead of the target, which runs only for an existing target, and an existing target
    // never owes the create verdict; counting it here would change first-phase SQL for no outcome.
    private static EtagPreconditionEvaluation GetUnresolvedEtagPreconditionEvaluation(
        RelationalWriteExecutorInput input
    ) =>
        GetEtagPreconditionEvaluation(
            input.WritePrecondition,
            GetUnresolvedProposedRelationshipAuthorization(input),
            input.StoredNamespaceAuthorization,
            input.ProposedNamespaceAuthorization,
            hasPendingCreateOwnershipFailure: false
        );

    private static RelationshipAuthorizationResult? GetUnresolvedProposedRelationshipAuthorization(
        RelationalWriteExecutorInput input
    )
    {
        if (input.ProposedRelationshipAuthorization is not null)
        {
            return input.ProposedRelationshipAuthorization;
        }

        if (input.PostRelationshipAuthorizationPlans is not { } plans)
        {
            return null;
        }

        if (plans.CreateNewProposedRelationshipAuthorization is not null)
        {
            return plans.CreateNewProposedRelationshipAuthorization;
        }

        return
            plans.ExistingResourcePlan.ProposedValues is RelationshipAuthorizationResult.Authorized authorized
            ? authorized
            : null;
    }

    private static EtagPreconditionEvaluation GetEtagPreconditionEvaluation(
        WritePrecondition writePrecondition,
        RelationshipAuthorizationResult? proposedRelationshipAuthorization,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization,
        bool hasPendingCreateOwnershipFailure
    ) =>
        HasEtagPrecondition(writePrecondition)
        && (
            proposedRelationshipAuthorization is not null
            || storedNamespaceAuthorization is not null
            || proposedNamespaceAuthorization is not null
            || hasPendingCreateOwnershipFailure
        )
            ? EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
            : EtagPreconditionEvaluation.BeforeProposedAuthorization;

    public RelationalWriteExecutorResult? TryBuildDeferredPreconditionFailureResult(
        RelationalWriteExecutorRequest request,
        RelationalWriteCurrentState? currentState
    )
    {
        if (!HasEtagPrecondition(request.WritePrecondition))
        {
            return null;
        }

        if (request.TargetContext is RelationalWriteTargetContext.CreateNew)
        {
            // If-Match on an insert fails (no current representation to match).
            return RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                request.OperationKind,
                ETagPreconditionFailureReason.TargetDoesNotExist
            );
        }

        if (request.TargetContext is not RelationalWriteTargetContext.ExistingDocument)
        {
            throw new InvalidOperationException(
                $"Deferred etag precondition does not support target context '{request.TargetContext.GetType().Name}'."
            );
        }

        if (request.ExistingDocumentReadPlan is null)
        {
            return RelationalWriteExecutorResults.BuildMissingExistingDocumentReadPlanResult(request);
        }

        if (currentState is null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var missingTarget = (RelationalWriteTargetContext.ExistingDocument)request.TargetContext;
                _logger.LogDebug(
                    "Deferred etag precondition for document {DocumentId}: no current representation "
                        + "(operation={OperationKind}); resolving missing-target outcome",
                    missingTarget.DocumentId,
                    request.OperationKind
                );
            }
            return request.OperationKind switch
            {
                RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureWriteConflict()
                ),
                // RFC 9110 §13.1.1 If-Match: * requires the target to exist; a wildcard against a missing PUT
                // target yields the precondition-failed (412) result rather than not-exists (404).
                RelationalWriteOperationKind.Put => request.WritePrecondition
                    is WritePrecondition.IfMatch { IsWildcard: true }
                    ? RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                        request.OperationKind,
                        ETagPreconditionFailureReason.TargetDoesNotExist
                    )
                    : new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists()),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
            };
        }

        // Write preconditions compare ContentVersion and schemaEpoch only. Evaluate that state projection
        // directly; representation-specific format, profile, and link inputs are intentionally absent.
        var isSatisfied = EtagPreconditionEvaluator.IsSatisfiedByCurrentState(
            request.WritePrecondition,
            currentState.DocumentMetadata.ContentVersion,
            request.MappingSet.Key.EffectiveSchemaHash
        );

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var existing = (RelationalWriteTargetContext.ExistingDocument)request.TargetContext;
            _logger.LogDebug(
                "Deferred etag precondition for document {DocumentId}: "
                    + "contentVersion={ContentVersion}, satisfied={IsSatisfied}",
                existing.DocumentId,
                currentState.DocumentMetadata.ContentVersion,
                isSatisfied
            );
        }

        return isSatisfied
            ? null
            : RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                request.OperationKind,
                EtagPreconditionEvaluator.GetFailureReason(request.WritePrecondition)
            );
    }
}

internal enum EtagPreconditionEvaluation
{
    BeforeProposedAuthorization,
    DeferredUntilAfterProposedAuthorization,
}

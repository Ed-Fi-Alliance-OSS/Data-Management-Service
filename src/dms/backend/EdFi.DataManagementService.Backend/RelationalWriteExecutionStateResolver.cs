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
    /// True when the request carries an HTTP conditional write precondition (If-Match or
    /// If-None-Match) whose current existence/etag the write flow must resolve. If-None-Match is a
    /// sibling of If-Match, so every structural "is a precondition present?" gate must admit both;
    /// only the proceed-vs-412 outcome differs, centralized in <see cref="EtagPreconditionEvaluator"/>.
    /// </summary>
    internal static bool HasEtagPrecondition(WritePrecondition precondition) =>
        precondition switch
        {
            WritePrecondition.None => false,
            WritePrecondition.IfMatch => true,
            WritePrecondition.IfNoneMatch => true,
            _ => throw new ArgumentOutOfRangeException(
                nameof(precondition),
                precondition,
                "Unsupported write precondition type."
            ),
        };

    public static EtagPreconditionEvaluation GetEtagPreconditionEvaluation(
        RelationalWriteExecutorRequest request
    ) =>
        GetEtagPreconditionEvaluation(
            request.WritePrecondition,
            request.ProposedRelationshipAuthorization,
            request.StoredNamespaceAuthorization,
            request.ProposedNamespaceAuthorization
        );

    /// <summary>
    /// The same evaluation-mode decision computed from the unresolved input, so composite first-phase
    /// emission — which happens before the target is observed — cannot drift from the resolved
    /// request's decision.
    /// </summary>
    public static EtagPreconditionEvaluation GetEtagPreconditionEvaluation(
        RelationalWriteExecutorInput input
    ) =>
        GetEtagPreconditionEvaluation(
            input.WritePrecondition,
            GetUnresolvedProposedRelationshipAuthorization(input),
            input.StoredNamespaceAuthorization,
            input.ProposedNamespaceAuthorization
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
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization
    ) =>
        HasEtagPrecondition(writePrecondition)
        && (
            proposedRelationshipAuthorization is not null
            || storedNamespaceAuthorization is not null
            || proposedNamespaceAuthorization is not null
        )
            ? EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
            : EtagPreconditionEvaluation.BeforeProposedAuthorization;

    public RelationalWriteExecutorResult? TryBuildDeferredPreconditionFailureResult(
        RelationalWriteExecutorRequest request,
        RelationalWriteCurrentState? currentState
    )
    {
        // FAIL-OPEN HAZARD: this early return must admit BOTH If-Match and If-None-Match. If it kept
        // keying on If-Match only, an If-None-Match write against an existing, authorization-bounded
        // target would take the deferred path, return null here, and proceed WITHOUT the required 412.
        if (!HasEtagPrecondition(request.WritePrecondition))
        {
            return null;
        }

        if (request.TargetContext is RelationalWriteTargetContext.CreateNew)
        {
            // If-Match on an insert fails (no current representation to match). If-None-Match on an
            // insert is the create-only success case, so it proceeds.
            return request.WritePrecondition is WritePrecondition.IfMatch
                ? RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                    request.OperationKind,
                    ETagPreconditionFailureReason.TargetDoesNotExist
                )
                : null;
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
                // target yields the precondition-failed (412) result rather than not-exists (404). An
                // If-None-Match against a now-missing target is the success case, so it falls through to
                // the normal not-exists (404) result.
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

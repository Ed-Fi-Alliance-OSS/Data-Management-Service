// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalWriteExecutorResults
{
    public static RelationalWriteExecutorResult BuildUnknownFailureResult(
        RelationalWriteOperationKind operationKind,
        string failureMessage
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UnknownFailure(failureMessage)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UnknownFailure(failureMessage)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult BuildRelationshipAuthorizationFailureResult(
        RelationalWriteOperationKind operationKind,
        RelationshipAuthorizationFailure relationshipFailure
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureRelationshipNotAuthorized(relationshipFailure)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureRelationshipNotAuthorized(relationshipFailure)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult BuildSecurityConfigurationFailureResult(
        RelationalWriteOperationKind operationKind,
        string[] errors,
        SecurityConfigurationFailureDiagnostic[]? diagnostics = null
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureSecurityConfiguration(errors, diagnostics)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureSecurityConfiguration(errors, diagnostics)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult BuildNamespaceAuthorizationFailureResult(
        RelationalWriteOperationKind operationKind,
        NamespaceAuthorizationFailure namespaceFailure
    )
    {
        ArgumentNullException.ThrowIfNull(namespaceFailure);

        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureNamespaceNotAuthorized(namespaceFailure)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureNamespaceNotAuthorized(namespaceFailure)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    /// <summary>
    /// Maps a stale-target namespace authorization result to a write conflict (POST) or not-exists (PUT)
    /// outcome, mirroring the single-record relationship authorization stale-target mapping. Locked write
    /// paths row-lock the target before the namespace check, so this is a defensive mapping for a row
    /// that vanished despite the lock rather than an expected outcome.
    /// </summary>
    public static RelationalWriteExecutorResult BuildStaleTargetResult(
        RelationalWriteOperationKind operationKind
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureWriteConflict()
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureNotExists()
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult BuildNoClaimsRelationshipAuthorizationResult(
        RelationalWriteOperationKind operationKind,
        RelationshipAuthorizationResult.NoClaims noClaims
    )
    {
        if (
            !RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
                noClaims.CheckSpecs,
                noClaims.Failures,
                [],
                GetRelationshipAuthorizationAuth1Index(operationKind),
                out var noClaimsFailure
            ) || noClaimsFailure is null
        )
        {
            return BuildUnknownFailureResult(
                operationKind,
                "Relationship authorization required caller EducationOrganizationIds, but denial metadata could not be built."
            );
        }

        return BuildRelationshipAuthorizationFailureResult(operationKind, noClaimsFailure);
    }

    public static RelationalWriteExecutorResult BuildGuardedNoOpSuccessResult(
        RelationalWriteOperationKind operationKind,
        DocumentUuid documentUuid,
        string etag
    )
    {
        RequireEtag(etag);

        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpdateSuccess(documentUuid, etag),
                RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateSuccess(documentUuid, etag),
                RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult BuildAppliedWriteSuccessResult(
        RelationalWriteOperationKind operationKind,
        RelationalWriteTargetContext targetContext,
        RelationalWritePersistResult persistedTarget,
        string etag
    )
    {
        RequireEtag(etag);
        var documentUuid = persistedTarget.DocumentUuid;

        return (operationKind, targetContext) switch
        {
            (RelationalWriteOperationKind.Post, RelationalWriteTargetContext.CreateNew) =>
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.InsertSuccess(documentUuid, etag),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                ),
            (RelationalWriteOperationKind.Post, RelationalWriteTargetContext.ExistingDocument) =>
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpdateSuccess(documentUuid, etag),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                ),
            (RelationalWriteOperationKind.Put, RelationalWriteTargetContext.ExistingDocument) =>
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(documentUuid, etag),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                ),
            _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
        };
    }

    public static RelationalWriteExecutorResult BuildStaleNoOpCompareResult(
        RelationalWriteOperationKind operationKind,
        WritePrecondition writePrecondition
    )
    {
        ArgumentNullException.ThrowIfNull(writePrecondition);
        // A wildcard If-Match (*) is an existence-only precondition, not a concurrency check. A stale
        // guarded no-op against a still-existing row must NOT 412 for a wildcard; excluding it here
        // routes the wildcard through the write-conflict branch, matching the no-precondition path.
        var hasIfMatchPrecondition = writePrecondition is WritePrecondition.IfMatch { IsWildcard: false };

        return operationKind switch
        {
            RelationalWriteOperationKind.Post when hasIfMatchPrecondition =>
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureETagMisMatch(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                ),
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureWriteConflict(),
                RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
            ),
            RelationalWriteOperationKind.Put when hasIfMatchPrecondition =>
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureETagMisMatch(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureWriteConflict(),
                RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult BuildReferenceFailureResult(
        RelationalWriteOperationKind operationKind,
        ResolvedReferenceSet resolvedReferences
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureReference(
                    [.. resolvedReferences.InvalidDocumentReferences],
                    [.. resolvedReferences.InvalidDescriptorReferences]
                )
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureReference(
                    [.. resolvedReferences.InvalidDocumentReferences],
                    [.. resolvedReferences.InvalidDescriptorReferences]
                )
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult BuildValidationFailureResult(
        RelationalWriteOperationKind operationKind,
        WriteValidationFailure[] validationFailures
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureValidation(validationFailures)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureValidation(validationFailures)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult BuildProfileCreatabilityRejectionResult(
        RelationalWriteOperationKind operationKind,
        string profileName
    ) =>
        operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureProfileDataPolicy(profileName)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureProfileDataPolicy(profileName)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };

    public static RelationalWriteExecutorResult BuildProfileContractMismatchResult(
        RelationalWriteOperationKind operationKind,
        ProfileFailure[] failures
    )
    {
        var message = $"Profile write contract mismatch: {failures[0].Message}";
        return BuildUnknownFailureResult(operationKind, message);
    }

    /// <summary>
    /// Shapes a planner-emitted <see cref="ProfilePlannerContractMismatchException"/> as a
    /// profile contract-mismatch result. The leading <c>"Profile write contract mismatch:"</c>
    /// prefix matches <see cref="BuildProfileContractMismatchResult"/> so callers cannot tell
    /// upfront-validator failures from planner-driven failures by message shape.
    /// </summary>
    public static RelationalWriteExecutorResult BuildPlannerContractMismatchResult(
        RelationalWriteOperationKind operationKind,
        ProfilePlannerContractMismatchException exception
    )
    {
        var message = $"Profile write contract mismatch: {exception.Message}";
        return BuildUnknownFailureResult(operationKind, message);
    }

    public static RelationalWriteExecutorResult BuildPreconditionFailureResult(
        RelationalWriteOperationKind operationKind,
        ETagPreconditionFailureReason reason = ETagPreconditionFailureReason.Concurrency
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureETagMisMatch(reason)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureETagMisMatch(reason)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult? BuildMissingExistingDocumentReadPlanResult(
        RelationalWriteExecutorRequest request
    )
    {
        if (request.ExistingDocumentReadPlan is not null)
        {
            return null;
        }

        var failureMessage = RelationalWriteSupport.BuildMissingExistingDocumentReadPlanMessage(
            request.WritePlan.Model.Resource
        );

        return BuildUnknownFailureResult(request.OperationKind, failureMessage);
    }

    public static int GetRelationshipAuthorizationAuth1Index(RelationalWriteOperationKind operationKind) =>
        operationKind switch
        {
            RelationalWriteOperationKind.Post =>
                RelationalDocumentStoreRepository.PostRelationshipAuthorizationAuth1Index,
            RelationalWriteOperationKind.Put =>
                RelationalDocumentStoreRepository.PutRelationshipAuthorizationAuth1Index,
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };

    private static void RequireEtag(string etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
        {
            throw new InvalidOperationException(
                "Committed relational write did not produce an external response _etag."
            );
        }
    }
}

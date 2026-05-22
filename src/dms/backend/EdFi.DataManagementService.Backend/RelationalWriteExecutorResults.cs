// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

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
                new UpsertResult.UpsertFailureRelationshipNotAuthorized(
                    RelationshipAuthorizationErrorMessageFormatter.Format(relationshipFailure),
                    relationshipFailure
                )
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureRelationshipNotAuthorized(
                    RelationshipAuthorizationErrorMessageFormatter.Format(relationshipFailure),
                    relationshipFailure
                )
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    public static RelationalWriteExecutorResult BuildNoClaimsStoredRelationshipAuthorizationResult(
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

    public static int GetRelationshipAuthorizationAuth1Index(RelationalWriteOperationKind operationKind) =>
        operationKind switch
        {
            RelationalWriteOperationKind.Post =>
                RelationalDocumentStoreRepository.PostRelationshipAuthorizationAuth1Index,
            RelationalWriteOperationKind.Put =>
                RelationalDocumentStoreRepository.PutRelationshipAuthorizationAuth1Index,
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
}

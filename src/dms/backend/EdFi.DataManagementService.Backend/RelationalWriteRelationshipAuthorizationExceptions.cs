// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

public sealed class RelationalWriteRelationshipAuthorizationNotAuthorizedException(
    RelationshipAuthorizationFailure relationshipFailure
) : Exception("Relational proposed relationship authorization failed.")
{
    public RelationshipAuthorizationFailure RelationshipFailure { get; } =
        relationshipFailure ?? throw new ArgumentNullException(nameof(relationshipFailure));
}

public sealed class RelationalWriteInvalidRelationshipAuthorizationFailureException(string failureMessage)
    : Exception(failureMessage)
{
    public string FailureMessage { get; } =
        string.IsNullOrWhiteSpace(failureMessage)
            ? throw new ArgumentException("Failure message is required.", nameof(failureMessage))
            : failureMessage;
}

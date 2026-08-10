// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Public-facing security-configuration messages for custom view-based authorization planning failures.
/// Shared so the regular-resource GET-many path and the descriptor GET-many path report the same wording for
/// the same failure kind rather than one of them falling back to the generic unknown-strategy message.
/// </summary>
internal static class CustomViewAuthorizationFailureMessages
{
    /// <summary>
    /// ODS names the specific basis-entity property missing from the subject entity, because it joins
    /// custom views on natural keys. DMS joins on DocumentId, so no single missing property exists to name:
    /// the failure is that no reference path reaches the basis resource at all. The wording therefore states
    /// the DMS failure rather than ODS's, while keeping ODS's closing question, which is the part an operator
    /// acts on. See the security-configuration table in auth.md.
    /// </summary>
    public static string NoJoinPath(RelationshipAuthorizationFailureMetadata failure, string operationLabel)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // The planner's hint already names both the subject and the basis resource, so it carries that
        // detail here rather than being appended after the closing question.
        var joinPathSentence = string.IsNullOrWhiteSpace(failure.Hint)
            ? "No DocumentId join path could be resolved from the subject resource to the custom view basis resource."
            : failure.Hint.Trim();

        return $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
            + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' uses custom auth view '{failure.Location?.AuthorizationObjectName ?? "<unknown>"}'. "
            + $"{joinPathSentence} "
            + "Should a different authorization strategy be used?";
    }
}

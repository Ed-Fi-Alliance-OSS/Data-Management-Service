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
    public static string NoJoinPath(RelationshipAuthorizationFailureMetadata failure, string operationLabel)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
            + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' uses custom auth view '{failure.Location?.AuthorizationObjectName ?? "<unknown>"}', "
            + "but no DocumentId join path could be resolved from the subject resource to the custom view basis resource."
            + FormatHintSentence(failure.Hint);
    }

    private static string FormatHintSentence(string? hint) =>
        string.IsNullOrWhiteSpace(hint) ? string.Empty : $" {hint}";
}

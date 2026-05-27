// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class RelationshipAuthorizationPlanningHelpers
{
    internal static RelationshipAuthorizationPersonKind MapPersonKind(SecurableElementKind kind) =>
        kind switch
        {
            SecurableElementKind.Student => RelationshipAuthorizationPersonKind.Student,
            SecurableElementKind.Contact => RelationshipAuthorizationPersonKind.Contact,
            SecurableElementKind.Staff => RelationshipAuthorizationPersonKind.Staff,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unsupported relationship authorization person securable element kind."
            ),
        };

    internal static DbColumnName GetRootDocumentIdColumn(DbTableModel rootTableModel, string planningContext)
    {
        ArgumentNullException.ThrowIfNull(rootTableModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(planningContext);

        var rootScopeLocatorColumns = rootTableModel.IdentityMetadata.RootScopeLocatorColumns;

        return rootScopeLocatorColumns.Count switch
        {
            1 => rootScopeLocatorColumns[0],
            0 => throw new InvalidOperationException(
                $"Root table '{rootTableModel.Table}' does not expose a root-scope locator column for {planningContext}."
            ),
            _ => throw new InvalidOperationException(
                $"Root table '{rootTableModel.Table}' exposes multiple root-scope locator columns, which is not supported for {planningContext}."
            ),
        };
    }

    internal static IEnumerable<RelationshipAuthorizationFailureMetadata> OrderFailures(
        IEnumerable<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(failures);

        return failures
            .OrderBy(static failure => failure.ConfiguredStrategy?.RawConfiguredIndex ?? int.MaxValue)
            .ThenBy(static failure => failure.RelationshipLocalOrder ?? int.MaxValue)
            .ThenBy(GetUnresolvedContributionOrder)
            .ThenBy(static failure => failure.Location?.JsonPath, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.ReadableName, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.Table?.ToString(), StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.Column?.Value, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.AuthorizationObjectName, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Hint, StringComparer.Ordinal);
    }

    private static int GetUnresolvedContributionOrder(RelationshipAuthorizationFailureMetadata failure) =>
        failure.FailureKind is RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
        && failure.Contributors.Count > 0
            ? failure.Contributors.Min(static contributor => contributor.ContributionOrder)
            : int.MaxValue;
}

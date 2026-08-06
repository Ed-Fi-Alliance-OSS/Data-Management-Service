// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

public sealed record CustomViewAuthorizationCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    DbTableName RootTable,
    DbColumnName RootDocumentIdColumn,
    DbTableName AuthView,
    DbColumnName AuthViewDocumentIdColumn,
    IReadOnlyList<ColumnPathStep> PathToBasisResource
);

public abstract record CustomViewAuthorizationPlanOutcome
{
    private CustomViewAuthorizationPlanOutcome() { }

    public sealed record Plan(IReadOnlyList<CustomViewAuthorizationCheckSpec> Checks)
        : CustomViewAuthorizationPlanOutcome;

    /// <summary>
    /// At least one configured custom view could not be planned. <paramref name="PlannedChecks"/> carries the
    /// custom views that <em>did</em> plan successfully, so a caller can still validate the ones configured
    /// ahead of the earliest failure before reporting it. Custom views are AND filters executing in
    /// CMS-configured order, so an earlier missing or non-conforming <c>auth.{StrategyName}</c> must surface
    /// its own error rather than being hidden by a later strategy's planning failure.
    /// </summary>
    public sealed record SecurityConfiguration(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures,
        IReadOnlyList<CustomViewAuthorizationCheckSpec> PlannedChecks
    ) : CustomViewAuthorizationPlanOutcome;
}

internal static class PageDocumentIdCustomViewAdapter
{
    public static IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> AdaptFromChecks(
        IReadOnlyList<CustomViewAuthorizationCheckSpec> checks
    )
    {
        if (checks is null || checks.Count == 0)
        {
            return [];
        }

        return checks
            .Select(check => new PageDocumentIdAuthorizationCustomViewCheck(
                check.ConfiguredStrategy.StrategyName,
                check.ConfiguredStrategy.RawConfiguredIndex,
                check.AuthView,
                check.AuthViewDocumentIdColumn,
                check.PathToBasisResource,
                check.RootTable,
                check.RootDocumentIdColumn
            ))
            .ToArray();
    }
}

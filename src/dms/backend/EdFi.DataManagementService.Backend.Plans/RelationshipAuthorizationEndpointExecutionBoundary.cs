// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class RelationshipAuthorizationEndpointExecutionBoundary
{
    public static bool IsPeopleRelationshipStrategy(string strategyName) =>
        RelationshipAuthorizationStrategyClassifier.IsPeopleRelationshipStrategy(strategyName);

    public static void ThrowIfUnsupportedForPageDocumentId(RelationshipAuthorizationCheckSpec checkSpec)
    {
        ArgumentNullException.ThrowIfNull(checkSpec);

        if (IsPeopleRelationshipStrategy(checkSpec.ConfiguredStrategy.StrategyName))
        {
            throw new InvalidOperationException(
                $"PageDocumentId authorization does not support People relationship strategy '{checkSpec.ConfiguredStrategy.StrategyName}' before DMS-1095 integration."
            );
        }

        var errorMessage = GetNonEdOrgHierarchyErrorMessage(
            checkSpec,
            "PageDocumentId authorization supports only EdOrg hierarchy relationship checks."
        );

        if (errorMessage is not null)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    public static void ThrowIfUnsupportedForSingleRecordSql(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    )
    {
        ArgumentNullException.ThrowIfNull(checkSpecs);

        var peopleStrategy = checkSpecs.FirstOrDefault(checkSpec =>
            IsPeopleRelationshipStrategy(checkSpec.ConfiguredStrategy.StrategyName)
        );

        if (peopleStrategy is not null)
        {
            throw new ArgumentException(
                $"Single-record relationship authorization SQL does not support People relationship strategy '{peopleStrategy.ConfiguredStrategy.StrategyName}' before DMS-1158 integration.",
                nameof(checkSpecs)
            );
        }

        foreach (var checkSpec in checkSpecs)
        {
            var errorMessage = GetNonEdOrgHierarchyErrorMessage(
                checkSpec,
                "Single-record relationship authorization SQL only supports the EdOrg hierarchy auth object."
            );

            if (errorMessage is not null)
            {
                throw new ArgumentException(errorMessage, nameof(checkSpecs));
            }
        }
    }

    private static string? GetNonEdOrgHierarchyErrorMessage(
        RelationshipAuthorizationCheckSpec checkSpec,
        string messagePrefix
    )
    {
        if (!UsesEdOrgHierarchyAuthObject(checkSpec.AuthObject))
        {
            return $"{messagePrefix} Auth object '{checkSpec.AuthObject.Name}' is not supported.";
        }

        var nonEdOrgSubject = checkSpec.Subjects.FirstOrDefault(static subject =>
            subject.IsPersonSubject
            || subject.Contributors.Count == 0
            || subject.Contributors.Any(static contributor =>
                contributor.Kind is not SecurableElementKind.EducationOrganization
            )
        );

        if (nonEdOrgSubject is not null)
        {
            return $"{messagePrefix} Subject column '{nonEdOrgSubject.Column.Value}' is not an EducationOrganization subject.";
        }

        return null;
    }

    private static bool UsesEdOrgHierarchyAuthObject(RelationshipAuthorizationAuthObject authObject) =>
        authObject.Name.Equals(AuthNames.EdOrgIdToEdOrgId)
        && (
            (
                authObject.SubjectValueColumn.Equals(AuthNames.TargetEdOrgId)
                && authObject.ClaimEducationOrganizationIdColumn.Equals(AuthNames.SourceEdOrgId)
            )
            || (
                authObject.SubjectValueColumn.Equals(AuthNames.SourceEdOrgId)
                && authObject.ClaimEducationOrganizationIdColumn.Equals(AuthNames.TargetEdOrgId)
            )
        );
}

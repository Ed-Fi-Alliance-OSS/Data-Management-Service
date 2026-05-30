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

        var errorMessage = GetUnsupportedPageDocumentIdErrorMessage(
            checkSpec,
            "PageDocumentId authorization supports only EdOrg hierarchy or People relationship checks."
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

        foreach (var checkSpec in checkSpecs)
        {
            var errorMessage = GetUnsupportedPageDocumentIdErrorMessage(
                checkSpec,
                "Single-record relationship authorization SQL supports only EdOrg hierarchy or People relationship checks."
            );

            if (errorMessage is not null)
            {
                throw new ArgumentException(errorMessage, nameof(checkSpecs));
            }
        }
    }

    private static string? GetUnsupportedPageDocumentIdErrorMessage(
        RelationshipAuthorizationCheckSpec checkSpec,
        string messagePrefix
    )
    {
        foreach (var subject in checkSpec.Subjects)
        {
            if (subject.IsPersonSubject)
            {
                if (!UsesPeopleAuthObject(subject.AuthObject))
                {
                    return $"{messagePrefix} Auth object '{subject.AuthObject.Name}' is not supported.";
                }

                if (
                    subject.Contributors.Count == 0
                    || subject.Contributors.Any(static contributor => !IsPersonSubjectKind(contributor.Kind))
                )
                {
                    return $"{messagePrefix} Subject column '{subject.Column.Value}' is not a People subject.";
                }

                continue;
            }

            if (!UsesEdOrgHierarchyAuthObject(subject.AuthObject))
            {
                return $"{messagePrefix} Auth object '{subject.AuthObject.Name}' is not supported.";
            }

            if (
                subject.Contributors.Count == 0
                || subject.Contributors.Any(static contributor =>
                    contributor.Kind is not SecurableElementKind.EducationOrganization
                )
            )
            {
                return $"{messagePrefix} Subject column '{subject.Column.Value}' is not an EducationOrganization subject.";
            }
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

    private static bool UsesPeopleAuthObject(RelationshipAuthorizationAuthObject authObject) =>
        AuthObjectDefinitions.PeopleAuthViewDefinitions.Any(definition =>
            authObject.Name.Equals(definition.View)
            && authObject.SubjectValueColumn.Equals(definition.PersonDocumentIdOutputColumn)
            && authObject.ClaimEducationOrganizationIdColumn.Equals(
                definition.ClaimEducationOrganizationIdColumn
            )
        );

    private static bool IsPersonSubjectKind(SecurableElementKind kind) =>
        kind is SecurableElementKind.Student or SecurableElementKind.Contact or SecurableElementKind.Staff;
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal static class RelationshipAuthorizationErrorMessageFormatter
{
    public static string[] Format(RelationshipAuthorizationFailure relationshipFailure)
    {
        ArgumentNullException.ThrowIfNull(relationshipFailure);

        string edOrgIdsFromFilters = string.Join(
            ", ",
            relationshipFailure.ClaimEducationOrganizationIds.Select(static id => $"'{id.Value}'")
        );
        string[] notAuthorizedProperties =
        [
            .. relationshipFailure
                .FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
                .SelectMany(static subject => GetPropertyNames(subject))
                .Distinct(StringComparer.Ordinal),
        ];

        if (notAuthorizedProperties.Length == 0)
        {
            return
            [
                "No relationships have been established between the caller's education organization id claims "
                    + $"({edOrgIdsFromFilters}) and the requested resource.",
            ];
        }

        if (notAuthorizedProperties.Length == 1)
        {
            return
            [
                "No relationships have been established between the caller's education organization id claims "
                    + $"({edOrgIdsFromFilters}) and the resource item's {notAuthorizedProperties[0]} value.",
            ];
        }

        return
        [
            "No relationships have been established between the caller's education organization id claims "
                + $"({edOrgIdsFromFilters}) and one or more of the following properties of the resource item: "
                + $"{string.Join(", ", notAuthorizedProperties.Select(static property => $"'{property}'"))}.",
        ];
    }

    private static IEnumerable<string> GetPropertyNames(RelationshipAuthorizationFailedSubject subject)
    {
        if (subject.SecurableElements.Length == 0)
        {
            yield return subject.RootBinding.ColumnName;
            yield break;
        }

        foreach (var securableElement in subject.SecurableElements)
        {
            yield return securableElement.ReadableName;
        }
    }
}

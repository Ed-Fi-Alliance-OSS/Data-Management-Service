// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal sealed record RelationshipAuthorizationSelectedPeopleAuthView(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int RelationshipLocalOrder,
    SecurableElementKind SecurableElementKind,
    RelationshipAuthorizationPersonKind PersonKind,
    RelationshipAuthorizationPersonAuthViewKind AuthViewKind,
    RelationshipAuthorizationAuthObject AuthObject
);

internal static class RelationshipAuthorizationPeopleAuthViewSelector
{
    public static IReadOnlyList<RelationshipAuthorizationSelectedPeopleAuthView> Select(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(supportedStrategies);

        var resolvedPersonKinds = SelectResolvedPersonKinds(mappingSet, resource);

        if (resolvedPersonKinds.Count == 0)
        {
            return [];
        }

        List<RelationshipAuthorizationSelectedPeopleAuthView> selectedAuthViews = [];

        foreach (var supportedStrategy in supportedStrategies)
        {
            HashSet<(
                SecurableElementKind Kind,
                RelationshipAuthorizationPersonAuthViewKind AuthViewKind
            )> seen = [];

            foreach (var eligibleSubject in supportedStrategy.EligibleSubjects)
            {
                if (
                    eligibleSubject.PersonAuthViewKind is not { } authViewKind
                    || !resolvedPersonKinds.Contains(eligibleSubject.Kind)
                    || !seen.Add((eligibleSubject.Kind, authViewKind))
                )
                {
                    continue;
                }

                selectedAuthViews.Add(
                    new RelationshipAuthorizationSelectedPeopleAuthView(
                        supportedStrategy.ConfiguredStrategy,
                        supportedStrategy.RelationshipLocalOrder,
                        eligibleSubject.Kind,
                        MapPersonKind(eligibleSubject.Kind),
                        authViewKind,
                        RelationshipAuthorizationAuthObject.CreatePerson(authViewKind)
                    )
                );
            }
        }

        return selectedAuthViews;
    }

    private static HashSet<SecurableElementKind> SelectResolvedPersonKinds(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        if (!mappingSet.SecurableElementColumnPathsByResource.TryGetValue(resource, out var resolvedPaths))
        {
            return [];
        }

        return
        [
            .. resolvedPaths
                .Select(static path => path.Kind)
                .Where(static kind =>
                    kind
                        is SecurableElementKind.Student
                            or SecurableElementKind.Contact
                            or SecurableElementKind.Staff
                ),
        ];
    }

    private static RelationshipAuthorizationPersonKind MapPersonKind(SecurableElementKind kind) =>
        kind switch
        {
            SecurableElementKind.Student => RelationshipAuthorizationPersonKind.Student,
            SecurableElementKind.Contact => RelationshipAuthorizationPersonKind.Contact,
            SecurableElementKind.Staff => RelationshipAuthorizationPersonKind.Staff,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };
}

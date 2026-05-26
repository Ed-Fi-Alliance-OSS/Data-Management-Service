// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class PageDocumentIdAuthorizationSpecAdapter
{
    public static PageDocumentIdAuthorizationSpec Adapt(
        RelationshipAuthorizationResult.Authorized authorizationResult
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationResult);

        var claimEducationOrganizationIdParameterization =
            authorizationResult.ClaimEducationOrganizationIdParameterization
            ?? throw new InvalidOperationException(
                "PageDocumentId authorization requires claim EducationOrganization parameterization."
            );

        return new PageDocumentIdAuthorizationSpec(
            [.. authorizationResult.CheckSpecs.Select(AdaptStrategy)],
            claimEducationOrganizationIdParameterization
        );
    }

    private static PageDocumentIdAuthorizationStrategy AdaptStrategy(
        RelationshipAuthorizationCheckSpec checkSpec
    )
    {
        ArgumentNullException.ThrowIfNull(checkSpec);

        if (checkSpec.ValueSource is not RelationshipAuthorizationValueSource.Stored)
        {
            throw new InvalidOperationException(
                $"PageDocumentId authorization does not support relationship value source '{checkSpec.ValueSource}'."
            );
        }

        if (checkSpec.CheckTarget is not RelationshipAuthorizationCheckTarget.Stored storedTarget)
        {
            throw new InvalidOperationException(
                $"PageDocumentId authorization requires stored check targets, but received '{checkSpec.CheckTarget.GetType().Name}'."
            );
        }

        RelationshipAuthorizationEndpointExecutionBoundary.ThrowIfUnsupportedForPageDocumentId(checkSpec);
        var authObject = SelectPageDocumentIdAuthObject(checkSpec);

        return new PageDocumentIdAuthorizationStrategy(
            MapKind(checkSpec.Direction),
            [.. checkSpec.Subjects.Select(subject => AdaptSubject(storedTarget.RootTable, subject))],
            checkSpec.ConfiguredStrategy.RawConfiguredIndex,
            checkSpec.RelationshipLocalOrder,
            authObject.AllowsDirectClaimMatch
        );
    }

    private static RelationshipAuthorizationAuthObject SelectPageDocumentIdAuthObject(
        RelationshipAuthorizationCheckSpec checkSpec
    ) => checkSpec.Subjects[0].AuthObject;

    private static PageDocumentIdAuthorizationSubject AdaptSubject(
        DbTableName rootTable,
        RelationshipAuthorizationSubject subject
    )
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!subject.Table.Equals(rootTable))
        {
            throw new InvalidOperationException(
                $"Authorization subject table '{subject.Table}' does not match query root table '{rootTable}'. "
                    + "PageDocumentId authorization supports only concrete root-table subjects."
            );
        }

        return new PageDocumentIdAuthorizationSubject(subject.Table, subject.Column);
    }

    private static PageDocumentIdAuthorizationStrategyKind MapKind(
        RelationshipAuthorizationHierarchyDirection direction
    ) =>
        direction switch
        {
            RelationshipAuthorizationHierarchyDirection.Normal =>
                PageDocumentIdAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
            RelationshipAuthorizationHierarchyDirection.Inverted =>
                PageDocumentIdAuthorizationStrategyKind.RelationshipsWithEdOrgsOnlyInverted,
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unsupported page-query authorization direction."
            ),
        };
}

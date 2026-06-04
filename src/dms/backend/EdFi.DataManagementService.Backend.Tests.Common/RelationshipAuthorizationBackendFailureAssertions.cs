// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal static class RelationshipAuthorizationBackendFailureAssertions
{
    public static void AssertUnknownStrategySecurityConfiguration(
        string[] errors,
        SecurityConfigurationFailureDiagnostic[]? diagnostics,
        string expectedStrategyName,
        string expectedResourceFullName
    )
    {
        errors
            .Should()
            .Equal(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([expectedStrategyName])
            );

        diagnostics.Should().NotBeNull();
        var diagnostic = diagnostics!.Should().ContainSingle().Subject;
        diagnostic
            .ProviderOrPlannerFailureKind.Should()
            .Be("RelationshipAuthorization.InvalidAuthorizationStrategy");
        diagnostic.ResourceFullName.Should().Be(expectedResourceFullName);
        diagnostic.ConfiguredStrategyNames.Should().Equal(expectedStrategyName);
        diagnostic.ConfiguredStrategyIndexes.Should().Equal(0);
    }

    public static void AssertStoredRootSchoolNoRelationshipFailure(
        RelationshipAuthorizationFailure relationshipFailure,
        long expectedClaimEducationOrganizationId
    )
    {
        AssertStoredRelationshipFailure(
            relationshipFailure,
            expectedClaimEducationOrganizationId,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship,
            new RelationshipAuthorizationRootBinding(
                "Authz.AuthorizationRootChildResource",
                "authz.AuthorizationRootChildResource",
                "School_SchoolId"
            ),
            new RelationshipAuthorizationSecurableElement(
                "EducationOrganization",
                "$.schoolReference.schoolId",
                "SchoolId"
            ),
            "No matching relationship authorization row was found for the subject value and claim EducationOrganizationIds."
        );
    }

    public static void AssertStoredNullableSchoolNullFailure(
        RelationshipAuthorizationFailure relationshipFailure,
        long expectedClaimEducationOrganizationId
    )
    {
        AssertStoredRelationshipFailure(
            relationshipFailure,
            expectedClaimEducationOrganizationId,
            RelationshipAuthorizationSubjectFailureKind.StoredValueNull,
            new RelationshipAuthorizationRootBinding(
                "Authz.AuthorizationNullableResource",
                "authz.AuthorizationNullableResource",
                "NullableSchoolId"
            ),
            new RelationshipAuthorizationSecurableElement(
                "EducationOrganization",
                "$.nullableSchoolId",
                "NullableSchoolId"
            ),
            "Stored relationship authorization subject value is null."
        );
    }

    public static void AssertInvalidFailurePayloadSecurityConfiguration(string[] errors)
    {
        errors
            .Should()
            .Equal(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError
            );
    }

    private static void AssertStoredRelationshipFailure(
        RelationshipAuthorizationFailure relationshipFailure,
        long expectedClaimEducationOrganizationId,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind,
        RelationshipAuthorizationRootBinding expectedRootBinding,
        RelationshipAuthorizationSecurableElement expectedSecurableElement,
        string expectedSubjectHint
    )
    {
        relationshipFailure.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Stored);
        relationshipFailure.EmittedAuth1Index.Should().Be(0);
        relationshipFailure
            .ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(expectedClaimEducationOrganizationId);

        var failedStrategy = relationshipFailure.FailedStrategies.Should().ContainSingle().Subject;
        failedStrategy.ConfiguredStrategyIndex.Should().Be(0);
        failedStrategy.RelationshipLocalOrder.Should().Be(0);
        failedStrategy
            .StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
        failedStrategy
            .StrategyKind.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
        AssertEducationOrganizationAuthObject(failedStrategy.AuthObject);
        failedStrategy.Hint.Should().BeNull();

        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Subject;
        failedSubject.SubjectIndex.Should().Be(0);
        failedSubject.FailureKind.Should().Be(expectedFailureKind);
        failedSubject.RootBinding.Should().Be(expectedRootBinding);
        AssertEducationOrganizationAuthObject(failedSubject.AuthObject);
        failedSubject.SecurableElements.Should().Equal(expectedSecurableElement);
        failedSubject.Hint.Should().Be(expectedSubjectHint);
        failedSubject.PersonSubject.Should().BeNull();
    }

    private static void AssertEducationOrganizationAuthObject(
        RelationshipAuthorizationAuthObjectInfo? authObject
    )
    {
        authObject
            .Should()
            .Be(
                new RelationshipAuthorizationAuthObjectInfo(
                    "auth.EducationOrganizationIdToEducationOrganizationId",
                    "TargetEducationOrganizationId",
                    "SourceEducationOrganizationId"
                )
            );
    }
}

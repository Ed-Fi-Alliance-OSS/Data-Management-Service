// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Response;

public abstract class RelationshipAuthorizationProblemDetailsTestBase
{
    protected static string[] ResponseErrors(JsonNode response) =>
        [.. response["errors"]!.AsArray().Select(static error => error!.ToString())];

    protected static EducationOrganizationId[] ClaimIds(params long[] ids) =>
        [.. ids.Select(static id => new EducationOrganizationId(id))];

    protected static RelationshipAuthorizationFailure Failure(
        RelationshipAuthorizationFailureValueSource valueSource,
        EducationOrganizationId[] claimEducationOrganizationIds,
        params RelationshipAuthorizationFailedStrategy[] failedStrategies
    ) => new(valueSource, 0, failedStrategies, claimEducationOrganizationIds);

    protected static RelationshipAuthorizationFailedStrategy Strategy(
        int index,
        string strategyName,
        params RelationshipAuthorizationFailedSubject[] failedSubjects
    ) => new(index, index, strategyName, strategyName, AuthObject(), failedSubjects);

    protected static RelationshipAuthorizationFailedSubject Subject(
        int index,
        RelationshipAuthorizationSubjectFailureKind failureKind,
        params RelationshipAuthorizationSecurableElement[] securableElements
    ) =>
        new(
            index,
            failureKind,
            new RelationshipAuthorizationRootBinding(
                "Ed-Fi.StudentSchoolAssociation",
                "edfi.StudentSchoolAssociation",
                "SchoolId"
            ),
            AuthObject(),
            securableElements
        );

    protected static RelationshipAuthorizationSecurableElement SecurableElement(
        string readableName,
        string jsonPath
    ) => new("EducationOrganization", jsonPath, readableName);

    private static RelationshipAuthorizationAuthObjectInfo AuthObject() =>
        new(
            "auth.EducationOrganizationIdToEducationOrganizationId",
            "TargetEducationOrganizationId",
            "SourceEducationOrganizationId"
        );
}

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationProblemDetails_For_No_Relationship_Failures
    : RelationshipAuthorizationProblemDetailsTestBase
{
    private static readonly TraceId _traceId = new("relationship-trace");

    private JsonNode _response = null!;

    [SetUp]
    public void SetUp()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Stored,
            ClaimIds(255901, 255902),
            Strategy(
                0,
                "RelationshipsWithEdOrgsOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    SecurableElement("SchoolId", "$.schoolReference.schoolId")
                )
            ),
            Strategy(
                1,
                "RelationshipsWithStudentsOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    SecurableElement("SchoolId", "$.schoolReference.schoolId"),
                    SecurableElement("StudentUniqueId", "$.studentReference.studentUniqueId")
                )
            )
        );

        _response = FailureResponse.ForRelationshipAuthorization(_traceId, relationshipFailure);
    }

    [Test]
    public void It_renders_the_base_authorization_problem_details_shape()
    {
        _response["type"]!.ToString().Should().Be("urn:ed-fi:api:security:authorization");
        _response["title"]!.ToString().Should().Be("Authorization Denied");
        _response["status"]!.GetValue<int>().Should().Be(403);
        _response["detail"]!.ToString().Should().Be("Access to the requested data could not be authorized.");
        _response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        _response["validationErrors"]!.AsObject().Count.Should().Be(0);
    }

    [Test]
    public void It_aggregates_no_relationship_errors_across_failed_OR_strategies()
    {
        ResponseErrors(_response)
            .Should()
            .Equal(
                "No relationships have been established between the caller's education organization id claims ('255901', '255902') and one or more of the following properties of the resource item: 'SchoolId', 'StudentUniqueId'."
            );
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationProblemDetails_For_Empty_EdOrg_Claims
    : RelationshipAuthorizationProblemDetailsTestBase
{
    private static readonly TraceId _traceId = new("empty-claims-trace");

    private JsonNode _response = null!;

    [SetUp]
    public void SetUp()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Stored,
            [],
            Strategy(
                0,
                "RelationshipsWithEdOrgsOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    SecurableElement("SchoolId", "$.schoolReference.schoolId")
                )
            )
        );

        _response = FailureResponse.ForRelationshipAuthorization(_traceId, relationshipFailure);
    }

    [Test]
    public void It_renders_none_through_the_EdOrg_claims_wording()
    {
        ResponseErrors(_response)
            .Should()
            .Equal(
                "No relationships have been established between the caller's education organization id claims (none) and the resource item's 'SchoolId' value."
            );
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationProblemDetails_For_Stored_Value_Null
    : RelationshipAuthorizationProblemDetailsTestBase
{
    private static readonly TraceId _traceId = new("stored-null-trace");

    private JsonNode _response = null!;

    [SetUp]
    public void SetUp()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Stored,
            ClaimIds(255901),
            Strategy(
                0,
                "RelationshipsWithPeopleOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    SecurableElement("ContactUniqueId", "$.contactReference.contactUniqueId")
                )
            ),
            Strategy(
                1,
                "RelationshipsWithEdOrgsOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.StoredValueNull,
                    SecurableElement("SchoolId", "$.schoolReference.schoolId")
                ),
                Subject(
                    1,
                    RelationshipAuthorizationSubjectFailureKind.StoredValueNull,
                    SecurableElement(
                        "LocalEducationAgencyId",
                        "$.localEducationAgencyReference.localEducationAgencyId"
                    )
                )
            ),
            Strategy(
                2,
                "RelationshipsWithStudentsOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.StoredValueNull,
                    SecurableElement("StudentUniqueId", "$.studentReference.studentUniqueId")
                )
            )
        );

        _response = FailureResponse.ForRelationshipAuthorization(_traceId, relationshipFailure);
    }

    [Test]
    public void It_uses_the_existing_data_element_uninitialized_type_and_plural_detail()
    {
        _response["type"]!
            .ToString()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:relationships:invalid-data:element-uninitialized");
        _response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The existing values of one or more of the following properties are required for authorization purposes: 'SchoolId', 'LocalEducationAgencyId', 'StudentUniqueId'."
            );
    }

    [Test]
    public void It_emits_one_error_per_selected_configured_strategy()
    {
        ResponseErrors(_response)
            .Should()
            .Equal(
                "The existing resource item is inaccessible to clients using the 'RelationshipsWithEdOrgsOnly' authorization strategy.",
                "The existing resource item is inaccessible to clients using the 'RelationshipsWithStudentsOnly' authorization strategy."
            );
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationProblemDetails_For_Proposed_Value_Missing
    : RelationshipAuthorizationProblemDetailsTestBase
{
    private static readonly TraceId _traceId = new("proposed-missing-trace");

    private JsonNode _response = null!;

    [SetUp]
    public void SetUp()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Proposed,
            ClaimIds(255901),
            Strategy(
                0,
                "RelationshipsWithStudentsOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    SecurableElement("StudentUniqueId", "$.studentReference.studentUniqueId")
                )
            ),
            Strategy(
                1,
                "RelationshipsWithEdOrgsOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
                    SecurableElement("SchoolId", "$.schoolReference.schoolId")
                ),
                Subject(
                    1,
                    RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
                    SecurableElement(
                        "LocalEducationAgencyId",
                        "$.localEducationAgencyReference.localEducationAgencyId"
                    )
                )
            )
        );

        _response = FailureResponse.ForRelationshipAuthorization(_traceId, relationshipFailure);
    }

    [Test]
    public void It_uses_the_proposed_data_element_required_type_and_plural_detail()
    {
        _response["type"]!
            .ToString()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:relationships:access-denied:element-required");
        _response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The values of one or more of the following properties are required for authorization purposes: 'SchoolId', 'LocalEducationAgencyId'."
            );
    }

    [Test]
    public void It_emits_an_empty_errors_array()
    {
        _response["errors"]!.AsArray().Count.Should().Be(0);
    }
}

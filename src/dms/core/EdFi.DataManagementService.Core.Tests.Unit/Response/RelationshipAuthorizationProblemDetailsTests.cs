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

    protected static RelationshipAuthorizationFailedStrategy StrategyWithHint(
        int index,
        string strategyName,
        string hint,
        params RelationshipAuthorizationFailedSubject[] failedSubjects
    ) => new(index, index, strategyName, strategyName, AuthObject(), failedSubjects, hint);

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

    protected static RelationshipAuthorizationFailedSubject SubjectWithHint(
        int index,
        RelationshipAuthorizationSubjectFailureKind failureKind,
        string hint,
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
            securableElements,
            hint
        );

    protected static RelationshipAuthorizationFailedSubject SubjectWithPersonHint(
        int index,
        RelationshipAuthorizationSubjectFailureKind failureKind,
        string personHint,
        params RelationshipAuthorizationSecurableElement[] securableElements
    ) =>
        Subject(index, failureKind, securableElements) with
        {
            PersonSubject = new RelationshipAuthorizationPersonSubjectInfo(
                PersonKind: "Student",
                PathKind: "DirectRootColumn",
                DocumentIdPath:
                [
                    new RelationshipAuthorizationPersonDocumentIdPathStepInfo(
                        "edfi.StudentSchoolAssociation",
                        "Student_DocumentId",
                        TargetTableName: null,
                        TargetColumnName: null
                    ),
                ],
                StoredAnchor: new RelationshipAuthorizationPersonStoredAnchorInfo(
                    "edfi.StudentSchoolAssociation",
                    "DocumentId"
                ),
                ProposedAnchor: null,
                Hint: personHint
            ),
        };

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
public class Given_RelationshipAuthorizationProblemDetails_For_Readable_Names
    : RelationshipAuthorizationProblemDetailsTestBase
{
    private static readonly TraceId _traceId = new("readable-name-trace");

    private JsonNode _response = null!;

    [SetUp]
    public void SetUp()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Stored,
            ClaimIds(255901),
            Strategy(
                0,
                "RelationshipsWithEdOrgsAndPeople",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    SecurableElement("Campus Identifier", "$.schoolReference.schoolId")
                ),
                Subject(
                    1,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    SecurableElement("", "$.studentReference.studentUniqueId")
                ),
                Subject(
                    2,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    SecurableElement("Campus Identifier", "$.alternateSchoolReference.schoolId")
                )
            )
        );

        _response = FailureResponse.ForRelationshipAuthorization(_traceId, relationshipFailure);
    }

    [Test]
    public void It_prefers_readable_metadata_and_falls_back_to_the_json_path_name()
    {
        ResponseErrors(_response)
            .Should()
            .Equal(
                "No relationships have been established between the caller's education organization id claim ('255901') and one or more of the following properties of the resource item: 'Campus Identifier', 'StudentUniqueId'."
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
public class Given_RelationshipAuthorizationProblemDetails_For_EdOrg_Claim_Display
    : RelationshipAuthorizationProblemDetailsTestBase
{
    [Test]
    public void It_uses_claim_for_one_EdOrg_claim()
    {
        var response = FormatNoRelationshipWithClaimIds(255901);

        ResponseErrors(response)
            .Should()
            .Equal(
                "No relationships have been established between the caller's education organization id claim ('255901') and the resource item's 'SchoolId' value."
            );
    }

    [Test]
    public void It_displays_five_EdOrg_claims()
    {
        var response = FormatNoRelationshipWithClaimIds(5, 4, 3, 2, 1);

        ResponseErrors(response)
            .Should()
            .Equal(
                "No relationships have been established between the caller's education organization id claims ('1', '2', '3', '4', '5') and the resource item's 'SchoolId' value."
            );
    }

    [Test]
    public void It_truncates_more_than_five_EdOrg_claims()
    {
        var response = FormatNoRelationshipWithClaimIds(7, 6, 5, 4, 3, 2, 1);

        ResponseErrors(response)
            .Should()
            .Equal(
                "No relationships have been established between the caller's education organization id claims ('1', '2', '3', '4', '5', ...) and the resource item's 'SchoolId' value."
            );
    }

    private static JsonNode FormatNoRelationshipWithClaimIds(params long[] claimIds)
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Stored,
            ClaimIds(claimIds),
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

        return FailureResponse.ForRelationshipAuthorization(
            new TraceId("claim-display-trace"),
            relationshipFailure
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
public class Given_RelationshipAuthorizationProblemDetails_For_Singular_Stored_Value_Null
    : RelationshipAuthorizationProblemDetailsTestBase
{
    private static readonly TraceId _traceId = new("singular-stored-null-trace");

    private JsonNode _response = null!;

    [SetUp]
    public void SetUp()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Stored,
            ClaimIds(255901),
            Strategy(
                0,
                "RelationshipsWithEdOrgsOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.StoredValueNull,
                    SecurableElement("SchoolId", "$.schoolReference.schoolId")
                )
            )
        );

        _response = FailureResponse.ForRelationshipAuthorization(_traceId, relationshipFailure);
    }

    [Test]
    public void It_uses_the_singular_existing_data_detail()
    {
        _response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The existing 'SchoolId' value is required for authorization purposes."
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

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationProblemDetails_For_Singular_Proposed_Value_Missing
    : RelationshipAuthorizationProblemDetailsTestBase
{
    private static readonly TraceId _traceId = new("singular-proposed-missing-trace");

    private JsonNode _response = null!;

    [SetUp]
    public void SetUp()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Proposed,
            ClaimIds(255901),
            Strategy(
                0,
                "RelationshipsWithEdOrgsOnly",
                Subject(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
                    SecurableElement("SchoolId", "$.schoolReference.schoolId")
                )
            )
        );

        _response = FailureResponse.ForRelationshipAuthorization(_traceId, relationshipFailure);
    }

    [Test]
    public void It_uses_the_singular_proposed_data_detail()
    {
        _response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The 'SchoolId' value is required for authorization purposes."
            );
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationProblemDetails_For_Authorization_Hints
    : RelationshipAuthorizationProblemDetailsTestBase
{
    [Test]
    public void It_appends_distinct_hints_for_no_relationship_failures_in_configured_order()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Stored,
            ClaimIds(255901),
            StrategyWithHint(
                0,
                "RelationshipsWithPeopleOnly",
                "You may need a configured relationship.",
                SubjectWithHint(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    "You may need to create a corresponding 'StudentSchoolAssociation' item.",
                    SecurableElement("StudentUniqueId", "$.studentReference.studentUniqueId")
                ),
                SubjectWithPersonHint(
                    1,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    "You may need to create a corresponding 'StudentSchoolAssociation' item.",
                    SecurableElement("ContactUniqueId", "$.contactReference.contactUniqueId")
                )
            ),
            Strategy(
                1,
                "RelationshipsWithStaffOnly",
                SubjectWithPersonHint(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    "You may need to create corresponding 'StaffEducationOrganizationEmploymentAssociation' or 'StaffEducationOrganizationAssignmentAssociation' items.",
                    SecurableElement("StaffUniqueId", "$.staffReference.staffUniqueId")
                )
            )
        );

        var response = FailureResponse.ForRelationshipAuthorization(
            new TraceId("no-relationship-hint-trace"),
            relationshipFailure
        );

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. Hint: You may need a configured relationship. You may need to create a corresponding 'StudentSchoolAssociation' item. You may need to create corresponding 'StaffEducationOrganizationEmploymentAssociation' or 'StaffEducationOrganizationAssignmentAssociation' items."
            );
    }

    [Test]
    public void It_appends_hints_only_from_the_selected_invalid_data_failures()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Stored,
            ClaimIds(255901),
            Strategy(
                0,
                "RelationshipsWithPeopleOnly",
                SubjectWithHint(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                    "This lower precedence hint should not be emitted.",
                    SecurableElement("StudentUniqueId", "$.studentReference.studentUniqueId")
                )
            ),
            Strategy(
                1,
                "RelationshipsWithEdOrgsOnly",
                SubjectWithHint(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.StoredValueNull,
                    "You may need to repair the stored SchoolId.",
                    SecurableElement("SchoolId", "$.schoolReference.schoolId")
                ),
                SubjectWithHint(
                    1,
                    RelationshipAuthorizationSubjectFailureKind.StoredValueNull,
                    "Hint: You may need to repair the stored SchoolId.",
                    SecurableElement(
                        "LocalEducationAgencyId",
                        "$.localEducationAgencyReference.localEducationAgencyId"
                    )
                )
            )
        );

        var response = FailureResponse.ForRelationshipAuthorization(
            new TraceId("invalid-data-hint-trace"),
            relationshipFailure
        );

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The existing values of one or more of the following properties are required for authorization purposes: 'SchoolId', 'LocalEducationAgencyId'. Hint: You may need to repair the stored SchoolId."
            );
    }

    [Test]
    public void It_appends_hints_for_proposed_element_required_failures_and_keeps_errors_empty()
    {
        var relationshipFailure = Failure(
            RelationshipAuthorizationFailureValueSource.Proposed,
            ClaimIds(255901),
            Strategy(
                0,
                "RelationshipsWithEdOrgsOnly",
                SubjectWithHint(
                    0,
                    RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
                    "You may need to include the SchoolId.",
                    SecurableElement("SchoolId", "$.schoolReference.schoolId")
                )
            )
        );

        var response = FailureResponse.ForRelationshipAuthorization(
            new TraceId("proposed-hint-trace"),
            relationshipFailure
        );

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The 'SchoolId' value is required for authorization purposes. Hint: You may need to include the SchoolId."
            );
        response["errors"]!.AsArray().Count.Should().Be(0);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Response;

[TestFixture]
[Parallelizable]
public class Given_CustomViewAuthorizationFailureResponse
{
    private const string Hint = "You may need a Student with CTE Course Enrollments.";
    private const string StrategyName = "StudentWithCTECourseEnrollments";

    private static readonly TraceId TestTraceId = new("cv-trace-id");

    private static CustomViewAuthorizationFailure Failure(
        CustomViewAuthorizationFailureKind failureKind,
        CustomViewAuthorizationFailureValueSource valueSource,
        string[]? readableSecurableElements = null,
        string? hint = Hint
    ) =>
        new(
            failureKind,
            valueSource,
            EmittedAuth1Index: 0,
            StrategyName,
            readableSecurableElements ?? ["StudentUniqueId"],
            hint
        );

    private static string Text(JsonNode body, string property) => body[property]!.GetValue<string>();

    private static string[] Errors(JsonNode body) =>
        [.. body["errors"]!.AsArray().Select(error => error!.GetValue<string>())];

    [Test]
    public void It_should_format_a_stored_no_matching_row_failure_as_the_bare_authorization_type()
    {
        var body = CustomViewAuthorizationFailureResponse.ForFailure(
            Failure(
                CustomViewAuthorizationFailureKind.NoMatchingRow,
                CustomViewAuthorizationFailureValueSource.Stored
            ),
            TestTraceId
        );

        // auth.md §2.4 carries the bare authorization type, because a custom view need not involve education
        // organization claims at all.
        Text(body, "type").Should().Be("urn:ed-fi:api:security:authorization");
        Text(body, "title").Should().Be("Authorization Denied");
        body["status"]!.GetValue<int>().Should().Be(403);
        Text(body, "correlationId").Should().Be("cv-trace-id");
        Text(body, "detail")
            .Should()
            .Be(
                "Access to the requested data could not be authorized. "
                    + "Hint: You may need a Student with CTE Course Enrollments."
            );
        Errors(body)
            .Should()
            .Equal(
                "The caller is not authorized to perform the requested operation on the item based on the "
                    + "existing value of the 'StudentUniqueId' property of the item."
            );
    }

    [Test]
    public void It_should_never_mention_education_organization_claims_for_a_no_matching_row_failure()
    {
        // §2.3's relationship wording names EdOrg claims; §2.4 exists precisely because custom views may have
        // none, so that sentence must not leak into this response.
        var body = CustomViewAuthorizationFailureResponse.ForFailure(
            Failure(
                CustomViewAuthorizationFailureKind.NoMatchingRow,
                CustomViewAuthorizationFailureValueSource.Stored
            ),
            TestTraceId
        );

        body.ToJsonString().Should().NotContain("education organization");
        body.ToJsonString().Should().NotContain("No relationships have been established");
    }

    [Test]
    public void It_should_say_proposed_for_a_proposed_no_matching_row_failure()
    {
        var body = CustomViewAuthorizationFailureResponse.ForFailure(
            Failure(
                CustomViewAuthorizationFailureKind.NoMatchingRow,
                CustomViewAuthorizationFailureValueSource.Proposed
            ),
            TestTraceId
        );

        Errors(body)
            .Should()
            .Equal(
                "The caller is not authorized to perform the requested operation on the item based on the "
                    + "proposed value of the 'StudentUniqueId' property of the item."
            );
    }

    [Test]
    public void It_should_use_the_multiple_element_phrasing_for_a_composite_identity_basis()
    {
        var body = CustomViewAuthorizationFailureResponse.ForFailure(
            Failure(
                CustomViewAuthorizationFailureKind.NoMatchingRow,
                CustomViewAuthorizationFailureValueSource.Stored,
                ["CourseCode", "EducationOrganizationId"]
            ),
            TestTraceId
        );

        Errors(body)
            .Should()
            .Equal(
                "The caller is not authorized to perform the requested operation on the item based on the "
                    + "existing values of one or more of the following properties of the item: "
                    + "'CourseCode', 'EducationOrganizationId'."
            );
    }

    [Test]
    public void It_should_format_a_stored_value_uninitialized_failure_as_the_custom_view_invalid_data_type()
    {
        var body = CustomViewAuthorizationFailureResponse.ForFailure(
            Failure(
                CustomViewAuthorizationFailureKind.StoredValueUninitialized,
                CustomViewAuthorizationFailureValueSource.Stored
            ),
            TestTraceId
        );

        Text(body, "type")
            .Should()
            .Be("urn:ed-fi:api:security:authorization:custom-view:invalid-data:element-uninitialized");
        Text(body, "detail")
            .Should()
            .Be(
                "Access to the requested data could not be authorized. "
                    + "The existing 'StudentUniqueId' value is required for authorization purposes. "
                    + "Hint: You may need a Student with CTE Course Enrollments."
            );
        Errors(body)
            .Should()
            .Equal(
                "The existing resource item is inaccessible to clients using the "
                    + "'StudentWithCTECourseEnrollments' authorization strategy."
            );
    }

    [Test]
    public void It_should_format_a_proposed_value_missing_failure_as_the_custom_view_element_required_type()
    {
        var body = CustomViewAuthorizationFailureResponse.ForFailure(
            Failure(
                CustomViewAuthorizationFailureKind.ProposedValueMissing,
                CustomViewAuthorizationFailureValueSource.Proposed
            ),
            TestTraceId
        );

        Text(body, "type")
            .Should()
            .Be("urn:ed-fi:api:security:authorization:custom-view:access-denied:element-required");
        Text(body, "detail")
            .Should()
            .Be(
                "Access to the requested data could not be authorized. "
                    + "The 'StudentUniqueId' value is required for authorization purposes. "
                    + "Hint: You may need a Student with CTE Course Enrollments."
            );
        // auth.md §2.8 specifies an empty errors collection.
        Errors(body).Should().BeEmpty();
    }

    [Test]
    public void It_should_use_the_multiple_element_phrasing_for_an_uninitialized_composite_identity()
    {
        var body = CustomViewAuthorizationFailureResponse.ForFailure(
            Failure(
                CustomViewAuthorizationFailureKind.StoredValueUninitialized,
                CustomViewAuthorizationFailureValueSource.Stored,
                ["CourseCode", "EducationOrganizationId"],
                hint: null
            ),
            TestTraceId
        );

        Text(body, "detail")
            .Should()
            .Be(
                "Access to the requested data could not be authorized. "
                    + "The existing values of one or more of the following properties are required for "
                    + "authorization purposes: 'CourseCode', 'EducationOrganizationId'."
            );
    }

    [Test]
    public void It_should_use_the_multiple_element_phrasing_for_a_missing_composite_identity()
    {
        var body = CustomViewAuthorizationFailureResponse.ForFailure(
            Failure(
                CustomViewAuthorizationFailureKind.ProposedValueMissing,
                CustomViewAuthorizationFailureValueSource.Proposed,
                ["CourseCode", "EducationOrganizationId"],
                hint: null
            ),
            TestTraceId
        );

        Text(body, "detail")
            .Should()
            .Be(
                "Access to the requested data could not be authorized. "
                    + "The values of one or more of the following properties are required for "
                    + "authorization purposes: 'CourseCode', 'EducationOrganizationId'."
            );
    }

    [TestCase(CustomViewAuthorizationFailureKind.NoMatchingRow)]
    [TestCase(CustomViewAuthorizationFailureKind.StoredValueUninitialized)]
    public void It_should_omit_the_hint_sentence_when_no_hint_applies(
        CustomViewAuthorizationFailureKind failureKind
    )
    {
        var body = CustomViewAuthorizationFailureResponse.ForFailure(
            Failure(failureKind, CustomViewAuthorizationFailureValueSource.Stored, hint: null),
            TestTraceId
        );

        Text(body, "detail").Should().NotContain("Hint:");
    }

    [Test]
    public void It_should_append_the_hint_to_every_failure_kind()
    {
        // The hint is what tells a caller how to gain access, and the relationship formatter already appends
        // it to its uninitialized and missing-element cases. Diverging here would make two sibling formatters
        // disagree about the same failure family.
        foreach (
            var (failureKind, valueSource) in new[]
            {
                (
                    CustomViewAuthorizationFailureKind.NoMatchingRow,
                    CustomViewAuthorizationFailureValueSource.Stored
                ),
                (
                    CustomViewAuthorizationFailureKind.StoredValueUninitialized,
                    CustomViewAuthorizationFailureValueSource.Stored
                ),
                (
                    CustomViewAuthorizationFailureKind.ProposedValueMissing,
                    CustomViewAuthorizationFailureValueSource.Proposed
                ),
            }
        )
        {
            var body = CustomViewAuthorizationFailureResponse.ForFailure(
                Failure(failureKind, valueSource),
                TestTraceId
            );

            Text(body, "detail")
                .Should()
                .EndWith(
                    "Hint: You may need a Student with CTE Course Enrollments.",
                    because: $"{failureKind}"
                );
        }
    }

    [Test]
    public void It_should_reject_a_failure_that_names_no_securable_element()
    {
        // The planner always supplies at least one name across its three fallback tiers, so an empty list is
        // a violated upstream contract rather than a renderable response.
        var act = () =>
            CustomViewAuthorizationFailureResponse.ForFailure(
                Failure(
                    CustomViewAuthorizationFailureKind.NoMatchingRow,
                    CustomViewAuthorizationFailureValueSource.Stored,
                    []
                ),
                TestTraceId
            );

        act.Should().Throw<ArgumentException>().WithMessage("*at least one readable securable element*");
    }

    [Test]
    public void It_should_reject_an_unsupported_failure_kind()
    {
        var act = () =>
            CustomViewAuthorizationFailureResponse.ForFailure(
                Failure(
                    (CustomViewAuthorizationFailureKind)999,
                    CustomViewAuthorizationFailureValueSource.Stored
                ),
                TestTraceId
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

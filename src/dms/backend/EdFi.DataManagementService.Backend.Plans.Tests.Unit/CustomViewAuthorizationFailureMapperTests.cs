// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_CustomViewAuthorizationFailureMapper
{
    private static readonly DbTableName AuthView = new(
        new DbSchemaName("auth"),
        "StudentWithCTECourseEnrollments"
    );
    private static readonly DbTableName RootTable = new(new DbSchemaName("edfi"), "CourseTranscript");
    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");

    private static SingleRecordCustomViewAuthorizationCheckSpec Check(
        int index,
        CustomViewAuthorizationCheckValueSource valueSource,
        string strategyName = "StudentWithCTECourseEnrollments",
        IReadOnlyList<string>? readableSecurableElements = null
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, 0),
            index,
            valueSource,
            AuthView,
            DocumentIdColumn,
            [new ColumnPathStep(RootTable, new DbColumnName("Student_DocumentId"), null, null)],
            valueSource is CustomViewAuthorizationCheckValueSource.Stored
                ? new CustomViewAuthorizationCheckTarget.Stored(RootTable, DocumentIdColumn)
                : new CustomViewAuthorizationCheckTarget.Proposed(
                    RootTable,
                    new CustomViewAuthorizationProposedValueBinding(
                        RootTable,
                        new DbColumnName("Student_DocumentId"),
                        "logical",
                        "seed"
                    )
                ),
            new QualifiedResourceName("Ed-Fi", "Student"),
            readableSecurableElements ?? ["StudentUniqueId"],
            CustomViewAuthorizationHintFormatter.Format(strategyName)
        );

    [Test]
    public void It_should_map_a_stored_no_matching_row_payload_to_the_2_4_failure()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            [Check(0, CustomViewAuthorizationCheckValueSource.Stored)],
            out var failure
        );

        mapped.Should().BeTrue();
        failure.Should().NotBeNull();
        failure!.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.NoMatchingRow);
        failure.ValueSource.Should().Be(CustomViewAuthorizationFailureValueSource.Stored);
        failure.EmittedAuth1Index.Should().Be(0);
        failure.StrategyName.Should().Be("StudentWithCTECourseEnrollments");
        failure.ReadableSecurableElements.Should().Equal("StudentUniqueId");
        failure.Hint.Should().Be("You may need a Student with CTE Course Enrollments.");
    }

    [Test]
    public void It_should_map_a_stored_null_basis_payload_to_the_2_7_failure()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.StoredBasisValueNull
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            [Check(0, CustomViewAuthorizationCheckValueSource.Stored)],
            out var failure
        );

        mapped.Should().BeTrue();
        failure!.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.StoredValueUninitialized);
        failure.ValueSource.Should().Be(CustomViewAuthorizationFailureValueSource.Stored);
    }

    [Test]
    public void It_should_map_a_proposed_missing_basis_payload_to_the_2_8_failure()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            [Check(0, CustomViewAuthorizationCheckValueSource.Proposed)],
            out var failure
        );

        mapped.Should().BeTrue();
        failure!.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.ProposedValueMissing);
        failure.ValueSource.Should().Be(CustomViewAuthorizationFailureValueSource.Proposed);
    }

    [Test]
    public void It_should_select_the_check_the_payload_index_addresses_in_a_stored_then_proposed_pair()
    {
        // Update plans stored checks first, then proposed. The index is the only thing distinguishing them,
        // so mapping index 1 must report the proposed check's value source and strategy.
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> plannedChecks =
        [
            Check(0, CustomViewAuthorizationCheckValueSource.Stored, "StudentWithCTECourseEnrollments"),
            Check(1, CustomViewAuthorizationCheckValueSource.Proposed, "StudentWithCTECourseEnrollments"),
        ];
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            1,
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            plannedChecks,
            out var failure
        );

        mapped.Should().BeTrue();
        failure!.ValueSource.Should().Be(CustomViewAuthorizationFailureValueSource.Proposed);
        failure.EmittedAuth1Index.Should().Be(1);
    }

    [Test]
    public void It_should_report_every_readable_securable_element_of_a_composite_identity_basis()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    readableSecurableElements: ["CourseCode", "EducationOrganizationId"]
                ),
            ],
            out var failure
        );

        mapped.Should().BeTrue();
        failure!.ReadableSecurableElements.Should().Equal("CourseCode", "EducationOrganizationId");
    }

    [Test]
    public void It_should_not_map_a_payload_whose_index_is_out_of_range()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            5,
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            [Check(0, CustomViewAuthorizationCheckValueSource.Stored)],
            out var failure
        );

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    [Test]
    public void It_should_not_map_a_payload_against_an_empty_planned_check_list()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(payload, [], out var failure);

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    [Test]
    public void It_should_not_map_a_stored_only_kind_against_a_proposed_check()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.StoredBasisValueNull
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            [Check(0, CustomViewAuthorizationCheckValueSource.Proposed)],
            out var failure
        );

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    [Test]
    public void It_should_not_map_a_proposed_only_kind_against_a_stored_check()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            [Check(0, CustomViewAuthorizationCheckValueSource.Stored)],
            out var failure
        );

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    [Test]
    public void It_should_not_map_the_stale_target_kind_to_a_response_failure()
    {
        // Stale target is a retry signal that resolves to a 404, never a 403 body, so it must not produce a
        // cross-boundary failure at all.
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing
        );

        var mapped = CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            [Check(0, CustomViewAuthorizationCheckValueSource.Stored)],
            out var failure
        );

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    [Test]
    public void It_should_recognize_a_stale_stored_target_payload_against_a_stored_check()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing
        );

        CustomViewAuthorizationFailureMapper
            .IsStaleStoredTargetFailure(payload, [Check(0, CustomViewAuthorizationCheckValueSource.Stored)])
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_should_not_recognize_a_stale_payload_paired_with_a_proposed_check()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing
        );

        CustomViewAuthorizationFailureMapper
            .IsStaleStoredTargetFailure(payload, [Check(0, CustomViewAuthorizationCheckValueSource.Proposed)])
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_should_not_recognize_a_stale_payload_whose_index_is_out_of_range()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            3,
            CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing
        );

        CustomViewAuthorizationFailureMapper
            .IsStaleStoredTargetFailure(payload, [Check(0, CustomViewAuthorizationCheckValueSource.Stored)])
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_should_not_recognize_a_non_stale_kind_as_a_stale_target()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
        );

        CustomViewAuthorizationFailureMapper
            .IsStaleStoredTargetFailure(payload, [Check(0, CustomViewAuthorizationCheckValueSource.Stored)])
            .Should()
            .BeFalse();
    }
}

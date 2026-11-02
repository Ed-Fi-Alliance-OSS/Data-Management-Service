// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Missing-reference POST-create regressions shared by both database providers.
/// Execution and database-specific side-effect checks stay in each provider fixture.
/// </summary>
internal sealed class MissingReferencePostAuthorizationScenarios(
    MissingReferenceRootChildUpsert upsertRootChild,
    Func<AuthorizationRootChildSeed, Task> assertNoCreateSideEffects,
    Func<AuthorizationStudentAcademicRecordSeed, Task<UpsertResult>> postStudentAcademicRecord,
    Func<AuthorizationStudentAcademicRecordSeed, Task> assertNoPeopleCreateSideEffects,
    Func<AuthorizationStudentSchoolSeed, IReadOnlyList<string>, Task<UpsertResult>> postStudentSchool,
    Func<AuthorizationStudentSchoolSeed, Task> assertNoStudentSchoolCreateSideEffects,
    Action<
        UpsertResult,
        RelationshipAuthorizationSubjectFailureKind,
        IReadOnlyList<long>
    > assertPeopleRelationshipDenied,
    Action<UpsertResult, string> assertDirectStudentRelationshipDenied,
    StudentAcademicRecordSeed authorizedStudentAcademicRecordSeed,
    StudentAcademicRecordSeed unauthorizedStudentAcademicRecordSeed,
    IReadOnlyList<string> customViewStrategy,
    string rootChildUuidPrefix
)
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    public async Task It_returns_no_relationship_before_deferred_missing_reference_when_the_supplied_school_is_unresolved()
    {
        // DMS-1659: a supplied School that does not exist is denied exactly like an existing School the
        // caller has no relationship with, so the response cannot reveal which one it was.
        var missingSeed = CreateRootChildSeed(
            $"{rootChildUuidPrefix}-0000-0000-0000-000000000003",
            103,
            "missing-reference",
            999,
            []
        );
        var unrelatedSeed = CreateRootChildSeed(
            $"{rootChildUuidPrefix}-0000-0000-0000-000000000013",
            113,
            "unrelated-reference",
            300,
            []
        );

        var missingResult = await PostRootChildAsync(missingSeed);
        var unrelatedResult = await PostRootChildAsync(unrelatedSeed);

        var missingFailure = AssertSingleNoRelationshipSubject(missingResult);
        var unrelatedFailure = AssertSingleNoRelationshipSubject(unrelatedResult);
        missingFailure.Should().BeEquivalentTo(unrelatedFailure, options => options.WithStrictOrdering());
        await assertNoCreateSideEffects(missingSeed);
        await assertNoCreateSideEffects(unrelatedSeed);
    }

    public async Task It_denies_an_unresolved_school_equal_to_a_caller_claim_as_no_relationship()
    {
        // The unresolved reference is bound as null, so a supplied id that happens to equal a claim does not
        // authorize through direct claim equality; it is still the no-relationship denial.
        var seed = CreateRootChildSeed(
            $"{rootChildUuidPrefix}-0000-0000-0000-000000000014",
            114,
            "missing-reference-equal-to-claim",
            999,
            []
        );

        var result = await upsertRootChild(
            seed,
            [ClaimEducationOrganizationId, 999],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Equal(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        await assertNoCreateSideEffects(seed);
    }

    public async Task It_denies_an_unresolved_school_under_inverted_filtering_as_no_relationship()
    {
        var seed = CreateRootChildSeed(
            $"{rootChildUuidPrefix}-0000-0000-0000-000000000015",
            115,
            "inverted-missing-reference",
            999,
            []
        );

        var result = await PostRootChildAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.InvertedEdOrgOnlyStrategyNames
        );

        AssertSingleNoRelationshipSubject(result);
        await assertNoCreateSideEffects(seed);
    }

    public async Task Given_A_Custom_View_Strategy_It_Denies_A_Create_Whose_Proposed_School_Is_Unresolved_Like_An_Excluded_School()
    {
        var missingSeed = CreateRootChildSeed(
            $"{rootChildUuidPrefix}-0000-0000-0000-000000000603",
            603,
            "custom-view-missing-create",
            999,
            []
        );
        var excludedSeed = CreateRootChildSeed(
            $"{rootChildUuidPrefix}-0000-0000-0000-000000000604",
            604,
            "custom-view-excluded-create",
            200,
            []
        );

        var missingResult = await PostRootChildAsync(missingSeed, customViewStrategy);
        var excludedResult = await PostRootChildAsync(excludedSeed, customViewStrategy);

        var missingFailure = missingResult
            .Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure;
        var excludedFailure = excludedResult
            .Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure;
        missingFailure.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.NoMatchingRow);
        missingFailure.Should().BeEquivalentTo(excludedFailure, options => options.WithStrictOrdering());
        await assertNoCreateSideEffects(missingSeed);
        await assertNoCreateSideEffects(excludedSeed);
    }

    public async Task It_returns_no_relationship_before_deferred_people_missing_reference_when_the_supplied_first_hop_is_unresolved()
    {
        // The transitive People subject's first-hop anchor is the supplied StudentAcademicRecord reference.
        // An unresolved first hop answers like an existing record whose student the caller cannot reach.
        var missingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-0000-0000-0000-000000000205",
            205,
            "people-missing-reference",
            authorizedStudentAcademicRecordSeed
        ) with
        {
            StudentUniqueId = "missing-student",
        };
        var unrelatedSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-0000-0000-0000-000000000215",
            215,
            "people-unrelated-reference",
            unauthorizedStudentAcademicRecordSeed
        );

        var missingResult = await postStudentAcademicRecord(missingSeed);
        var unrelatedResult = await postStudentAcademicRecord(unrelatedSeed);

        assertPeopleRelationshipDenied(
            missingResult,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship,
            [ClaimEducationOrganizationId]
        );
        assertPeopleRelationshipDenied(
            unrelatedResult,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship,
            [ClaimEducationOrganizationId]
        );
        await assertNoPeopleCreateSideEffects(missingSeed);
        await assertNoPeopleCreateSideEffects(unrelatedSeed);
    }

    public async Task It_denies_an_unresolved_direct_person_reference_as_no_relationship()
    {
        var seed = new AuthorizationStudentSchoolSeed(
            new DocumentUuid(Guid.Parse("cccccccc-0000-0000-0000-000000000208")),
            208,
            "people-only-direct-missing",
            100,
            "missing-direct-student"
        );

        var result = await postStudentSchool(
            seed,
            RelationshipAuthorizationCrudTestSupport.PeopleOnlyStrategyNames
        );

        assertDirectStudentRelationshipDenied(
            result,
            RelationshipAuthorizationCrudTestSupport.RelationshipsWithPeopleOnly
        );
        await assertNoStudentSchoolCreateSideEffects(seed);
    }

    public async Task It_denies_an_unresolved_direct_person_with_an_authorized_school_in_a_mixed_and_strategy()
    {
        // EdOrgsAndPeople AND-composes the School and the Student; the School authorizes, so the only
        // failed subject is the unresolved Student.
        var seed = new AuthorizationStudentSchoolSeed(
            new DocumentUuid(Guid.Parse("cccccccc-0000-0000-0000-000000000209")),
            209,
            "mixed-direct-missing",
            100,
            "missing-mixed-student"
        );

        var result = await postStudentSchool(
            seed,
            RelationshipAuthorizationCrudTestSupport.EdOrgAndPeopleStrategyNames
        );

        assertDirectStudentRelationshipDenied(
            result,
            RelationshipAuthorizationCrudTestSupport.RelationshipsWithEdOrgsAndPeople
        );
        await assertNoStudentSchoolCreateSideEffects(seed);
    }

    public async Task It_reports_every_or_strategy_for_an_unresolved_direct_person()
    {
        var seed = new AuthorizationStudentSchoolSeed(
            new DocumentUuid(Guid.Parse("cccccccc-0000-0000-0000-000000000210")),
            210,
            "or-direct-missing",
            100,
            "missing-or-student"
        );

        var result = await postStudentSchool(
            seed,
            RelationshipAuthorizationCrudTestSupport.EdOrgAndPeoplePlusPeopleOnlyStrategyNames
        );

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.FailedStrategies.Select(static strategy => strategy.StrategyName)
            .Should()
            .Equal(
                RelationshipAuthorizationCrudTestSupport.RelationshipsWithEdOrgsAndPeople,
                RelationshipAuthorizationCrudTestSupport.RelationshipsWithPeopleOnly
            );
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => (subject.RootBinding.ColumnName, subject.FailureKind))
            .Should()
            .Equal(
                ("Student_DocumentId", RelationshipAuthorizationSubjectFailureKind.NoRelationship),
                ("Student_DocumentId", RelationshipAuthorizationSubjectFailureKind.NoRelationship)
            );
        await assertNoStudentSchoolCreateSideEffects(seed);
    }

    private static RelationshipAuthorizationFailure AssertSingleNoRelationshipSubject(UpsertResult result)
    {
        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        var failedSubject = failure
            .RelationshipFailure.FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.RootBinding.ColumnName.Should().Be("School_SchoolId");

        return failure.RelationshipFailure;
    }

    private Task<UpsertResult> PostRootChildAsync(
        AuthorizationRootChildSeed seed,
        IReadOnlyList<string>? strategyNames = null
    ) =>
        upsertRootChild(
            seed,
            [ClaimEducationOrganizationId],
            strategyNames ?? RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

    private static AuthorizationRootChildSeed CreateRootChildSeed(
        string documentUuid,
        int authorizationRootChildId,
        string name,
        int schoolId,
        IReadOnlyList<ClassPeriodReferenceSeed> classPeriods
    ) =>
        new(
            new DocumentUuid(Guid.Parse(documentUuid)),
            authorizationRootChildId,
            name,
            schoolId,
            classPeriods
        );

    private static AuthorizationStudentAcademicRecordSeed CreateAuthorizationStudentAcademicRecordSeed(
        string documentUuid,
        int authorizationStudentAcademicRecordId,
        string name,
        StudentAcademicRecordSeed studentAcademicRecordSeed
    ) =>
        new(
            new DocumentUuid(Guid.Parse(documentUuid)),
            authorizationStudentAcademicRecordId,
            name,
            studentAcademicRecordSeed.EducationOrganizationId,
            studentAcademicRecordSeed.SchoolYear,
            studentAcademicRecordSeed.StudentUniqueId,
            studentAcademicRecordSeed.TermDescriptor
        );
}

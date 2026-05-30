// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Get_By_Id_Authorization_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId = 900;
    private const string TermDescriptor = "uri://ed-fi.org/TermDescriptor#Fall Semester";
    private const string EntryGradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";
    private static readonly IReadOnlyList<string> _normalStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
    ];
    private static readonly IReadOnlyList<string> _invertedStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
    ];
    private static readonly IReadOnlyList<string> _normalAndInvertedStrategies =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
    ];
    private static readonly IReadOnlyList<string> _noFurtherAuthorizationRequiredOnlyStrategy =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];
    private static readonly IReadOnlyList<string> _normalPlusNoFurtherAuthorizationRequiredStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];
    private static readonly IReadOnlyList<string> _normalPlusKnownUnsupportedStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        AuthorizationStrategyNameConstants.OwnershipBased,
    ];

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000003")), 300, "West School"),
        new(
            new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000004")),
            (int)ClaimEducationOrganizationId,
            "Claim School"
        ),
    ];
    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000002")), 200, "P2"),
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000003")), 300, "P3"),
    ];
    private static readonly AuthorizationAndSeed[] _authorizationAndSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("44444444-0000-0000-0000-000000000001")),
            1,
            "requires-both",
            100,
            200
        ),
        new(
            new DocumentUuid(Guid.Parse("44444444-0000-0000-0000-000000000002")),
            2,
            "missing-secondary-auth",
            100,
            300
        ),
    ];
    private static readonly AuthorizationRootChildSeed[] _authorizationRootChildSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000001")),
            1,
            "authorized-by-root",
            100,
            [new ClassPeriodReferenceSeed("P3", 300)]
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000002")),
            2,
            "child-would-match-but-root-does-not",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000003")),
            3,
            "authorized-with-empty-child-collection",
            100,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000004")),
            4,
            "read-boundary-probe",
            100,
            []
        ),
    ];
    private static readonly AuthorizationChildOnlySeed _authorizationChildOnlySeed = new(
        new DocumentUuid(Guid.Parse("66666666-0000-0000-0000-000000000001")),
        1,
        "child-only",
        [new ClassPeriodReferenceSeed("P1", 100)]
    );
    private static readonly AuthorizationNullableSeed _authorizationNullableSeed = new(
        new DocumentUuid(Guid.Parse("77777777-0000-0000-0000-000000000001")),
        1,
        "stored-null"
    );
    private static readonly AuthorizationRootChildSeed _directClaimRootChildSeed = new(
        new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000005")),
        5,
        "get-direct-claim",
        (int)ClaimEducationOrganizationId,
        []
    );
    private static readonly SchoolYearTypeSeed _schoolYearSeed = new(
        new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000001")),
        2026,
        true,
        "2026"
    );
    private static readonly StudentSeed _authorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000101")),
        "10001",
        "Ari",
        "Able"
    );
    private static readonly StudentSeed _unauthorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000102")),
        "10002",
        "Blake",
        "Baker"
    );
    private static readonly StudentSchoolAssociationSeed _authorizedStudentSchoolAssociationSeed = new(
        new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000201")),
        _authorizedStudentSeed.StudentUniqueId,
        100,
        _schoolYearSeed.SchoolYear,
        EntryGradeLevelDescriptor,
        new DateOnly(2026, 8, 15)
    );
    private static readonly StudentSchoolAssociationSeed _unauthorizedStudentSchoolAssociationSeed = new(
        new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000202")),
        _unauthorizedStudentSeed.StudentUniqueId,
        300,
        _schoolYearSeed.SchoolYear,
        EntryGradeLevelDescriptor,
        new DateOnly(2026, 8, 15)
    );
    private static readonly StudentAcademicRecordSeed _authorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000301")),
        100,
        _schoolYearSeed.SchoolYear,
        _authorizedStudentSeed.StudentUniqueId,
        TermDescriptor
    );
    private static readonly StudentAcademicRecordSeed _unauthorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000302")),
        300,
        _schoolYearSeed.SchoolYear,
        _unauthorizedStudentSeed.StudentUniqueId,
        TermDescriptor
    );
    private static readonly AuthorizationStudentAcademicRecordSeed _authorizedAuthorizationStudentAcademicRecordSeed =
        new(
            new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000401")),
            101,
            "authorized-student-academic-record",
            _authorizedStudentAcademicRecordSeed.EducationOrganizationId,
            _authorizedStudentAcademicRecordSeed.SchoolYear,
            _authorizedStudentAcademicRecordSeed.StudentUniqueId,
            _authorizedStudentAcademicRecordSeed.TermDescriptor
        );
    private static readonly AuthorizationStudentAcademicRecordSeed _unauthorizedAuthorizationStudentAcademicRecordSeed =
        new(
            new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000402")),
            102,
            "unauthorized-student-academic-record",
            _unauthorizedStudentAcademicRecordSeed.EducationOrganizationId,
            _unauthorizedStudentAcademicRecordSeed.SchoolYear,
            _unauthorizedStudentAcademicRecordSeed.StudentUniqueId,
            _unauthorizedStudentAcademicRecordSeed.TermDescriptor
        );

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false,
            replaceReadTargetLookup: false
        );
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateSchoolAsync(schoolSeed)
            );
        }

        foreach (var classPeriodSeed in _classPeriodSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateClassPeriodAsync(classPeriodSeed)
            );
        }

        foreach (var authorizationAndSeed in _authorizationAndSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateAuthorizationAndAsync(authorizationAndSeed)
            );
        }

        foreach (var authorizationRootChildSeed in _authorizationRootChildSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateAuthorizationRootChildAsync(authorizationRootChildSeed)
            );
        }

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(_directClaimRootChildSeed)
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationChildOnlyAsync(_authorizationChildOnlySeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(_authorizationNullableSeed)
        );

        await _context.SeedTermDescriptorAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000501"),
            TermDescriptor
        );
        await _context.SeedSchoolYearTypeAsync(_schoolYearSeed);
        await _context.SeedStudentAsync(_authorizedStudentSeed);
        await _context.SeedStudentAsync(_unauthorizedStudentSeed);
        await _context.SeedStudentSchoolAssociationAsync(_authorizedStudentSchoolAssociationSeed);
        await _context.SeedStudentSchoolAssociationAsync(_unauthorizedStudentSchoolAssociationSeed);
        await _context.SeedStudentAcademicRecordAsync(_authorizedStudentAcademicRecordSeed);
        await _context.SeedStudentAcademicRecordAsync(_unauthorizedStudentAcademicRecordSeed);
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(
                _authorizedAuthorizationStudentAcademicRecordSeed
            )
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(
                _unauthorizedAuthorizationStudentAcademicRecordSeed
            )
        );

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_authorizes_get_by_id_before_reconstituting()
    {
        var result = await GetRootChildAsync(_authorizationRootChildSeeds[0], _normalStrategy);

        var success = AssertSuccess(result, _authorizationRootChildSeeds[0].DocumentUuid);
        success.EdfiDoc["schoolReference"]!["schoolId"]!.GetValue<long>().Should().Be(100);
        _context.AssertSingleDocumentHydration();
        _context.AssertSingleDocumentMaterialized();
    }

    [Test]
    public async Task It_returns_403_without_hydration_when_the_relationship_is_missing()
    {
        var result = await GetRootChildAsync(_authorizationRootChildSeeds[1], _normalStrategy);

        var failure = AssertRelationshipDenied(
            result,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        RelationshipAuthorizationBackendFailureAssertions.AssertStoredRootSchoolNoRelationshipFailure(
            failure.RelationshipFailure,
            ClaimEducationOrganizationId
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_authorizes_people_get_by_id_through_student_relationship_before_reconstituting()
    {
        var result = await GetStudentAcademicRecordAsync(_authorizedAuthorizationStudentAcademicRecordSeed);

        var success = AssertSuccess(result, _authorizedAuthorizationStudentAcademicRecordSeed.DocumentUuid);
        success.EdfiDoc["studentAcademicRecordReference"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(_authorizedStudentSeed.StudentUniqueId);
        _context.AssertSingleDocumentHydration();
        _context.AssertSingleDocumentMaterialized();
    }

    [Test]
    public async Task It_returns_403_without_hydration_when_people_relationship_is_missing()
    {
        var result = await GetStudentAcademicRecordAsync(_unauthorizedAuthorizationStudentAcademicRecordSeed);

        AssertPeopleRelationshipDenied(result, [ClaimEducationOrganizationId]);
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_people_no_claims_403_without_hydration()
    {
        var result = await GetStudentAcademicRecordAsync(
            _authorizedAuthorizationStudentAcademicRecordSeed,
            []
        );

        AssertPeopleRelationshipDenied(result, []);
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_authorizes_inverted_and_or_composed_relationship_strategies()
    {
        var invertedResult = await GetRootChildAsync(_authorizationRootChildSeeds[1], _invertedStrategy);
        AssertSuccess(invertedResult, _authorizationRootChildSeeds[1].DocumentUuid);
        _context.AssertSingleDocumentHydration();

        var orResult = await GetRootChildAsync(_authorizationRootChildSeeds[1], _normalAndInvertedStrategies);
        AssertSuccess(orResult, _authorizationRootChildSeeds[1].DocumentUuid);
        _context.AssertSingleDocumentHydration();
    }

    [Test]
    public async Task It_authorizes_get_by_id_by_direct_claim_match_without_a_hierarchy_edge()
    {
        (await _context.CountAuthEdgesAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId))
            .Should()
            .Be(0);

        var result = await GetRootChildAsync(_directClaimRootChildSeed, _normalStrategy);

        AssertSuccess(result, _directClaimRootChildSeed.DocumentUuid);
        _context.AssertSingleDocumentHydration();
    }

    [Test]
    public async Task It_ands_multiple_root_edorg_subjects_within_one_strategy()
    {
        var authorizedResult = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.MultiRootEdOrgResourceName,
            _authorizationAndSeeds[0].DocumentUuid,
            [ClaimEducationOrganizationId],
            _normalStrategy
        );
        AssertSuccess(authorizedResult, _authorizationAndSeeds[0].DocumentUuid);

        var unauthorizedResult = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.MultiRootEdOrgResourceName,
            _authorizationAndSeeds[1].DocumentUuid,
            [ClaimEducationOrganizationId],
            _normalStrategy
        );
        AssertRelationshipDenied(
            unauthorizedResult,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_treats_no_further_authorization_required_as_a_bypass_only_when_it_is_the_only_strategy()
    {
        var noOpOnlyResult = await GetRootChildAsync(
            _authorizationRootChildSeeds[1],
            _noFurtherAuthorizationRequiredOnlyStrategy,
            []
        );
        AssertSuccess(noOpOnlyResult, _authorizationRootChildSeeds[1].DocumentUuid);

        var mixedAuthorizedResult = await GetRootChildAsync(
            _authorizationRootChildSeeds[0],
            _normalPlusNoFurtherAuthorizationRequiredStrategy
        );
        AssertSuccess(mixedAuthorizedResult, _authorizationRootChildSeeds[0].DocumentUuid);

        var mixedUnauthorizedResult = await GetRootChildAsync(
            _authorizationRootChildSeeds[1],
            _normalPlusNoFurtherAuthorizationRequiredStrategy
        );
        AssertRelationshipDenied(
            mixedUnauthorizedResult,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_501_for_known_but_not_enabled_mixed_strategies()
    {
        var result = await GetRootChildAsync(
            _authorizationRootChildSeeds[0],
            _normalPlusKnownUnsupportedStrategy
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureNotImplemented>().Subject;
        failure.FailureMessage.Should().Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_security_configuration_failure_before_known_unsupported_strategy_failures()
    {
        var result = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.ChildOnlyEdOrgResourceName,
            _authorizationChildOnlySeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            _normalPlusKnownUnsupportedStrategy
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Contain(error => error.Contains("$.classPeriods[*].classPeriodReference.schoolId"));
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_single_record_403_when_claim_edorgs_are_empty()
    {
        var result = await GetRootChildAsync(_authorizationRootChildSeeds[0], _normalStrategy, []);

        var failure = AssertRelationshipDenied(
            result,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        failure.RelationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_surfaces_stored_null_invalid_data_without_reconstitution()
    {
        var result = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.NullableRootEdOrgResourceName,
            _authorizationNullableSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            _normalStrategy
        );

        var failure = AssertRelationshipDenied(
            result,
            RelationshipAuthorizationSubjectFailureKind.StoredValueNull
        );
        RelationshipAuthorizationBackendFailureAssertions.AssertStoredNullableSchoolNullFailure(
            failure.RelationshipFailure,
            ClaimEducationOrganizationId
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_retries_when_authorization_and_hydration_observe_different_content_versions()
    {
        _context.BeforeNextHydration(ct =>
            _context.MutateAuthorizationRootChildSchoolAsync(
                _authorizationRootChildSeeds[3].DocumentUuid,
                300,
                ct
            )
        );

        var result = await GetRootChildAsync(_authorizationRootChildSeeds[3], _normalStrategy);

        AssertRelationshipDenied(result, RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        _context.AssertHydratedWithoutMaterialization(expectedHydrationCount: 1);
    }

    private async Task<GetResult> GetRootChildAsync(
        AuthorizationRootChildSeed seed,
        IReadOnlyList<string> strategyNames,
        IReadOnlyList<long>? claimEducationOrganizationIds = null
    )
    {
        return await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            claimEducationOrganizationIds ?? [ClaimEducationOrganizationId],
            strategyNames
        );
    }

    private async Task<GetResult> GetStudentAcademicRecordAsync(
        AuthorizationStudentAcademicRecordSeed seed,
        IReadOnlyList<long>? claimEducationOrganizationIds = null
    )
    {
        return await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.StudentAcademicRecordResourceName,
            seed.DocumentUuid,
            claimEducationOrganizationIds ?? [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames
        );
    }

    private static GetResult.GetSuccess AssertSuccess(GetResult result, DocumentUuid expectedDocumentUuid)
    {
        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(expectedDocumentUuid);
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be(expectedDocumentUuid.Value.ToString());
        return success;
    }

    private static GetResult.GetFailureRelationshipNotAuthorized AssertRelationshipDenied(
        GetResult result,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        var failure = result.Should().BeOfType<GetResult.GetFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Contain(expectedFailureKind);
        return failure;
    }

    private static void AssertPeopleRelationshipDenied(
        GetResult result,
        IReadOnlyList<long> expectedClaimEducationOrganizationIds
    )
    {
        var failure = AssertRelationshipDenied(
            result,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Stored);
        failure
            .RelationshipFailure.ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(expectedClaimEducationOrganizationIds);

        var failedStrategy = failure.RelationshipFailure.FailedStrategies.Should().ContainSingle().Subject;
        failedStrategy
            .StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly);
        failedStrategy
            .StrategyKind.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly);
        AssertStudentAuthObject(failedStrategy.AuthObject);

        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Subject;
        failedSubject.RootBinding.TableName.Should().Be("authz.AuthorizationStudentAcademicRecordResource");
        failedSubject.RootBinding.ColumnName.Should().Be("DocumentId");
        AssertStudentAuthObject(failedSubject.AuthObject);
        failedSubject
            .SecurableElements.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RelationshipAuthorizationSecurableElement(
                    "Student",
                    "$.studentAcademicRecordReference.studentUniqueId",
                    "StudentUniqueId"
                )
            );
        if (expectedClaimEducationOrganizationIds.Count == 0)
        {
            failedSubject.Hint.Should().Contain("requires at least one claim EducationOrganizationId");
            failedSubject.Hint.Should().Contain("StudentSchoolAssociation");
        }
        else
        {
            failedSubject
                .Hint.Should()
                .Be(
                    "No matching relationship authorization row was found for the subject value and claim EducationOrganizationIds."
                );
        }

        failedSubject.PersonSubject.Should().NotBeNull();
        var personSubject = failedSubject.PersonSubject!;
        personSubject.PersonKind.Should().Be("Student");
        personSubject.PathKind.Should().Be("TransitiveJoinPath");
        personSubject
            .DocumentIdPath.Select(static step => step.SourceTableName)
            .Should()
            .Equal("authz.AuthorizationStudentAcademicRecordResource", "edfi.StudentAcademicRecord");
        personSubject
            .DocumentIdPath.Select(static step => step.SourceColumnName)
            .Should()
            .Equal("StudentAcademicRecord_DocumentId", "Student_DocumentId");
        personSubject
            .StoredAnchor.RootTableName.Should()
            .Be("authz.AuthorizationStudentAcademicRecordResource");
        personSubject.StoredAnchor.RootDocumentIdColumnName.Should().Be("DocumentId");
        personSubject.Hint.Should().Contain("StudentSchoolAssociation");
    }

    private static void AssertStudentAuthObject(RelationshipAuthorizationAuthObjectInfo? authObject)
    {
        authObject
            .Should()
            .Be(
                new RelationshipAuthorizationAuthObjectInfo(
                    "auth.EducationOrganizationIdToStudentDocumentId",
                    "Student_DocumentId",
                    "SourceEducationOrganizationId",
                    "You may need to create a corresponding 'StudentSchoolAssociation' item."
                )
            );
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Get_By_Id_Authorization_With_An_Unmappable_AUTH1_Payload
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    private static readonly QuerySchoolSeed _schoolSeed = new(
        new DocumentUuid(Guid.Parse("22222222-9000-0000-0000-000000000001")),
        300,
        "West School"
    );

    private static readonly AuthorizationRootChildSeed _authorizationRootChildSeed = new(
        new DocumentUuid(Guid.Parse("55555555-9000-0000-0000-000000000001")),
        901,
        "invalid-auth1-payload",
        300,
        []
    );

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext(
            ReplaceAuth1PayloadWithUnmappableOrdinal
        );
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false,
            replaceReadTargetLookup: false
        );
        await _context.SeedSchoolDescriptorDataAsync();

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateSchoolAsync(_schoolSeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(_authorizationRootChildSeed)
        );
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, _schoolSeed.SchoolId);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_returns_security_configuration_when_the_provider_payload_cannot_map_to_the_plan()
    {
        var result = await _context.GetByIdAsync(
            RelationshipAuthorizationCrudTestSupport.ProjectEndpointName,
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            _authorizationRootChildSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        RelationshipAuthorizationBackendFailureAssertions.AssertInvalidFailurePayloadSecurityConfiguration(
            failure.Errors
        );
        _context.AssertNoHydration();
    }

    private static RelationshipAuthorizationProviderFailure ReplaceAuth1PayloadWithUnmappableOrdinal(
        RelationshipAuthorizationProviderFailure providerFailure
    ) => providerFailure with { Message = "1|0|1|99:0:n" };
}

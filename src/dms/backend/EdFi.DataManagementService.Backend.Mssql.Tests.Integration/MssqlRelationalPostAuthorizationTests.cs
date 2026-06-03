// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalPost")]
public class Given_A_Mssql_RelationalPost_Create_Authorization_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string ProjectEndpointName = RelationshipAuthorizationCrudTestSupport.ProjectEndpointName;
    private const string RootChildResourceName =
        RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName;
    private const string StudentAcademicRecordResourceName =
        RelationshipAuthorizationCrudTestSupport.StudentAcademicRecordResourceName;
    private const string StudentSchoolResourceName =
        RelationshipAuthorizationCrudTestSupport.StudentSchoolResourceName;
    private const string TermDescriptor = "uri://ed-fi.org/TermDescriptor#Fall Semester";
    private const string EntryGradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000003")), 300, "West School"),
        new(
            new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000004")),
            (int)ClaimEducationOrganizationId,
            "Claim School"
        ),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("eeeeeeee-0000-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-0000-0000-0000-000000000002")), 200, "P2"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-0000-0000-0000-000000000003")), 300, "P3"),
    ];

    private static readonly SchoolYearTypeSeed _schoolYearSeed = new(
        new DocumentUuid(Guid.Parse("cccccccc-1000-0000-0000-000000000001")),
        2026,
        true,
        "2026"
    );

    private static readonly StudentSeed _authorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("cccccccc-1000-0000-0000-000000000101")),
        "post-10001",
        "Ari",
        "Able"
    );

    private static readonly StudentSeed _unauthorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("cccccccc-1000-0000-0000-000000000102")),
        "post-10002",
        "Blake",
        "Baker"
    );

    private static readonly StudentSchoolAssociationSeed _authorizedStudentSchoolAssociationSeed = new(
        new DocumentUuid(Guid.Parse("cccccccc-1000-0000-0000-000000000201")),
        _authorizedStudentSeed.StudentUniqueId,
        100,
        _schoolYearSeed.SchoolYear,
        EntryGradeLevelDescriptor,
        new DateOnly(2026, 8, 15)
    );

    private static readonly StudentSchoolAssociationSeed _unauthorizedStudentSchoolAssociationSeed = new(
        new DocumentUuid(Guid.Parse("cccccccc-1000-0000-0000-000000000202")),
        _unauthorizedStudentSeed.StudentUniqueId,
        300,
        _schoolYearSeed.SchoolYear,
        EntryGradeLevelDescriptor,
        new DateOnly(2026, 8, 15)
    );

    private static readonly StudentAcademicRecordSeed _authorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("cccccccc-1000-0000-0000-000000000301")),
        100,
        _schoolYearSeed.SchoolYear,
        _authorizedStudentSeed.StudentUniqueId,
        TermDescriptor
    );

    private static readonly StudentAcademicRecordSeed _unauthorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("cccccccc-1000-0000-0000-000000000302")),
        300,
        _schoolYearSeed.SchoolYear,
        _unauthorizedStudentSeed.StudentUniqueId,
        TermDescriptor
    );

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _context = new MssqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false,
            replaceReadTargetLookup: false
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        await _context.Database.ResetAsync();
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

        await _context.SeedTermDescriptorAsync(
            Guid.Parse("cccccccc-1000-0000-0000-000000000501"),
            TermDescriptor
        );
        await _context.SeedSchoolYearTypeAsync(_schoolYearSeed);
        await _context.SeedStudentAsync(_authorizedStudentSeed);
        await _context.SeedStudentAsync(_unauthorizedStudentSeed);
        await _context.SeedStudentSchoolAssociationAsync(_authorizedStudentSchoolAssociationSeed);
        await _context.SeedStudentSchoolAssociationAsync(_unauthorizedStudentSchoolAssociationSeed);
        await _context.SeedStudentAcademicRecordAsync(_authorizedStudentAcademicRecordSeed);
        await _context.SeedStudentAcademicRecordAsync(_unauthorizedStudentAcademicRecordSeed);

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId);
        _context.ResetRecorder();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Test]
    public async Task It_authorizes_post_create_and_inserts_document_root_and_child_rows()
    {
        var seed = CreateRootChildSeed(
            "ffffffff-0000-0000-0000-000000000001",
            101,
            "authorized-create",
            100,
            [new ClassPeriodReferenceSeed("P3", 300)]
        );

        var result = await PostRootChildAsync(seed);

        var success = result.Should().BeOfType<UpsertResult.InsertSuccess>().Subject;
        success.NewDocumentUuid.Should().Be(seed.DocumentUuid);
        await AssertPersistedRowsAsync(seed);
        _context.AssertPostCreateRelationshipAuthorizationBeforeDocumentInsert();
    }

    [Test]
    public async Task It_authorizes_post_create_by_direct_claim_match_without_a_hierarchy_edge()
    {
        var seed = CreateRootChildSeed(
            "ffffffff-0000-0000-0000-000000000009",
            109,
            "direct-claim-create",
            (int)ClaimEducationOrganizationId,
            []
        );

        (await _context.CountAuthEdgesAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId))
            .Should()
            .Be(0);

        var result = await PostRootChildAsync(seed);

        var success = result.Should().BeOfType<UpsertResult.InsertSuccess>().Subject;
        success.NewDocumentUuid.Should().Be(seed.DocumentUuid);
        await AssertPersistedRowsAsync(seed);
        _context.AssertPostCreateDirectClaimMatchAuthorizationBeforeDocumentInsert();
    }

    [Test]
    public async Task It_returns_403_and_inserts_no_create_side_effects_when_the_relationship_is_missing()
    {
        var seed = CreateRootChildSeed(
            "ffffffff-0000-0000-0000-000000000002",
            102,
            "unauthorized-create",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        var result = await PostRootChildAsync(seed);

        AssertRelationshipDenied(result, RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        await AssertNoCreateSideEffectsAsync(seed);
        _context.AssertPostCreateRelationshipAuthorizationBeforeDocumentInsert();
    }

    [Test]
    public async Task It_keeps_reference_resolution_failure_distinct_from_authorization_denial()
    {
        var seed = CreateRootChildSeed(
            "ffffffff-0000-0000-0000-000000000003",
            103,
            "missing-reference",
            999,
            []
        );

        var result = await PostRootChildAsync(seed);

        var referenceFailure = result.Should().BeOfType<UpsertResult.UpsertFailureReference>().Subject;
        referenceFailure.HasDocumentReferenceFailures.Should().BeTrue();
        await AssertNoCreateSideEffectsAsync(seed);
    }

    [Test]
    public async Task It_authorizes_post_create_with_inverted_relationship_filtering()
    {
        var seed = CreateRootChildSeed(
            "ffffffff-0000-0000-0000-000000000004",
            104,
            "inverted-create",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        var result = await PostRootChildAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.InvertedEdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        await AssertPersistedRowsAsync(seed);
        _context.AssertPostCreateRelationshipAuthorizationBeforeDocumentInsert();
    }

    [Test]
    public async Task It_returns_403_before_create_if_match_when_proposed_values_are_unauthorized()
    {
        var seed = CreateRootChildSeed(
            "ffffffff-0000-0000-0000-000000000005",
            105,
            "unauthorized-if-match",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        var result = await PostRootChildAsync(seed, ifMatch: "\"stale-etag\"");

        AssertRelationshipDenied(result, RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        await AssertNoCreateSideEffectsAsync(seed);
    }

    [Test]
    public async Task It_returns_412_after_authorized_proposed_values_with_create_if_match()
    {
        var seed = CreateRootChildSeed(
            "ffffffff-0000-0000-0000-000000000006",
            106,
            "authorized-if-match",
            100,
            []
        );

        var result = await PostRootChildAsync(seed, ifMatch: "\"stale-etag\"");

        result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
        await AssertNoCreateSideEffectsAsync(seed);
    }

    [Test]
    public async Task It_authorizes_people_post_create_and_inserts_document_and_root_rows()
    {
        var seed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-0000-0000-0000-000000000201",
            201,
            "people-authorized-create",
            _authorizedStudentAcademicRecordSeed
        );

        var result = await PostStudentAcademicRecordAsync(seed);

        var success = result.Should().BeOfType<UpsertResult.InsertSuccess>().Subject;
        success.NewDocumentUuid.Should().Be(seed.DocumentUuid);
        await AssertPeoplePersistedRowsAsync(seed);
        _context.AssertPostCreatePeopleAuthorizationBeforeDocumentInsert();
    }

    [Test]
    public async Task It_returns_403_and_inserts_no_people_create_side_effects_when_the_relationship_is_missing()
    {
        var seed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-0000-0000-0000-000000000202",
            202,
            "people-unauthorized-create",
            _unauthorizedStudentAcademicRecordSeed
        );

        var result = await PostStudentAcademicRecordAsync(seed);

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship,
            [ClaimEducationOrganizationId]
        );
        await AssertNoPeopleCreateSideEffectsAsync(seed);
        _context.AssertPostCreatePeopleAuthorizationBeforeDocumentInsert();
    }

    [Test]
    public async Task It_returns_403_before_reference_resolution_when_people_create_has_empty_claims()
    {
        var seed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-0000-0000-0000-000000000203",
            203,
            "people-empty-claims-missing-reference",
            _authorizedStudentAcademicRecordSeed
        ) with
        {
            StudentUniqueId = "missing-student",
        };

        var result = await PostStudentAcademicRecordAsync(seed, claimEducationOrganizationIds: []);

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship,
            []
        );
        await AssertNoPeopleCreateSideEffectsAsync(seed);
    }

    [Test]
    public async Task It_returns_403_before_create_if_match_when_people_proposed_values_are_unauthorized()
    {
        var seed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-0000-0000-0000-000000000204",
            204,
            "people-unauthorized-if-match",
            _unauthorizedStudentAcademicRecordSeed
        );

        var result = await PostStudentAcademicRecordAsync(seed, ifMatch: "\"stale-etag\"");

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship,
            [ClaimEducationOrganizationId]
        );
        await AssertNoPeopleCreateSideEffectsAsync(seed);
    }

    [Test]
    public async Task It_denies_post_for_unauthorized_people_only_proposed_value()
    {
        var seed = new AuthorizationStudentSchoolSeed(
            new DocumentUuid(Guid.Parse("cccccccc-0000-0000-0000-000000000206")),
            206,
            "people-only-direct-proposed-denied",
            100,
            _unauthorizedStudentSeed.StudentUniqueId
        );

        var result = await PostStudentSchoolAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.PeopleOnlyStrategyNames
        );

        AssertDirectStudentRelationshipDenied(
            result,
            RelationshipAuthorizationCrudTestSupport.RelationshipsWithPeopleOnly
        );
        await AssertNoStudentSchoolCreateSideEffectsAsync(seed);
    }

    [Test]
    public async Task It_denies_post_for_unauthorized_edorgs_and_people_proposed_value()
    {
        var seed = new AuthorizationStudentSchoolSeed(
            new DocumentUuid(Guid.Parse("cccccccc-0000-0000-0000-000000000207")),
            207,
            "mixed-direct-proposed-denied",
            100,
            _unauthorizedStudentSeed.StudentUniqueId
        );

        var result = await PostStudentSchoolAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.EdOrgAndPeopleStrategyNames
        );

        AssertDirectStudentRelationshipDenied(
            result,
            RelationshipAuthorizationCrudTestSupport.RelationshipsWithEdOrgsAndPeople
        );
        await AssertNoStudentSchoolCreateSideEffectsAsync(seed);
    }

    [Test]
    public async Task It_keeps_people_reference_resolution_failure_distinct_when_claims_are_present()
    {
        var seed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-0000-0000-0000-000000000205",
            205,
            "people-missing-reference",
            _authorizedStudentAcademicRecordSeed
        ) with
        {
            StudentUniqueId = "missing-student",
        };

        var result = await PostStudentAcademicRecordAsync(seed);

        var referenceFailure = result.Should().BeOfType<UpsertResult.UpsertFailureReference>().Subject;
        referenceFailure.HasDocumentReferenceFailures.Should().BeTrue();
        await AssertNoPeopleCreateSideEffectsAsync(seed);
    }

    [Test]
    public async Task It_authorizes_post_create_with_mssql_scalar_claim_parameters_below_the_tvp_threshold()
    {
        var seed = CreateRootChildSeed(
            "ffffffff-0000-0000-0000-000000000007",
            107,
            "authorized-scalar-claims",
            (int)ClaimEducationOrganizationId,
            []
        );

        var result = await PostRootChildAsync(seed, CreateUniqueClaimEducationOrganizationIds(1999));

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        await AssertPersistedRowsAsync(seed);
        _context.AssertPostCreateRelationshipAuthorizationUsesScalarClaimParameters(1999);
    }

    [Test]
    public async Task It_authorizes_post_create_with_mssql_structured_tvp_claim_parameters_at_the_threshold()
    {
        var seed = CreateRootChildSeed(
            "ffffffff-0000-0000-0000-000000000008",
            108,
            "authorized-structured-claims",
            (int)ClaimEducationOrganizationId,
            []
        );

        var result = await PostRootChildAsync(seed, CreateUniqueClaimEducationOrganizationIds(2000));

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        await AssertPersistedRowsAsync(seed);
        _context.AssertPostCreateRelationshipAuthorizationUsesStructuredClaimParameter();
    }

    private async Task<UpsertResult> PostRootChildAsync(
        AuthorizationRootChildSeed seed,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        IReadOnlyList<string>? strategyNames = null,
        string? ifMatch = null
    )
    {
        return await _context.UpsertAuthorizationRootChildAsync(
            seed,
            claimEducationOrganizationIds ?? [ClaimEducationOrganizationId],
            strategyNames ?? RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch
        );
    }

    private async Task<UpsertResult> PostRootChildAsync(
        AuthorizationRootChildSeed seed,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null
    )
    {
        return await PostRootChildAsync(seed, null, strategyNames, ifMatch);
    }

    private async Task<UpsertResult> PostStudentAcademicRecordAsync(
        AuthorizationStudentAcademicRecordSeed seed,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        string? ifMatch = null
    )
    {
        return await _context.UpsertAuthorizationStudentAcademicRecordAsync(
            seed,
            claimEducationOrganizationIds ?? [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames,
            ifMatch
        );
    }

    private async Task<UpsertResult> PostStudentSchoolAsync(
        AuthorizationStudentSchoolSeed seed,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null
    )
    {
        return await _context.UpsertAuthorizationStudentSchoolAsync(
            seed,
            [ClaimEducationOrganizationId],
            strategyNames,
            ifMatch
        );
    }

    private async Task AssertPersistedRowsAsync(AuthorizationRootChildSeed seed)
    {
        (await _context.CountDocumentRowsAsync(seed.DocumentUuid)).Should().Be(1);
        (
            await _context.CountResourceRootRowsAsync(
                ProjectEndpointName,
                RootChildResourceName,
                seed.DocumentUuid
            )
        )
            .Should()
            .Be(1);
        (await _context.CountResourceCollectionRowsAsync(ProjectEndpointName, RootChildResourceName))
            .Should()
            .Be(seed.ClassPeriods.Count);
        (await _context.CountReferentialIdentityRowsForAuthorizationRootChildAsync(seed)).Should().Be(1);
    }

    private async Task AssertNoCreateSideEffectsAsync(AuthorizationRootChildSeed seed)
    {
        (await _context.CountDocumentRowsAsync(seed.DocumentUuid)).Should().Be(0);
        (
            await _context.CountResourceRootRowsAsync(
                ProjectEndpointName,
                RootChildResourceName,
                seed.DocumentUuid
            )
        )
            .Should()
            .Be(0);
        (await _context.CountResourceRootRowsAsync(ProjectEndpointName, RootChildResourceName))
            .Should()
            .Be(0);
        (await _context.CountResourceCollectionRowsAsync(ProjectEndpointName, RootChildResourceName))
            .Should()
            .Be(0);
        (await _context.CountReferentialIdentityRowsForAuthorizationRootChildAsync(seed)).Should().Be(0);
    }

    private async Task AssertPeoplePersistedRowsAsync(AuthorizationStudentAcademicRecordSeed seed)
    {
        (await _context.CountDocumentRowsAsync(seed.DocumentUuid)).Should().Be(1);
        (
            await _context.CountResourceRootRowsAsync(
                ProjectEndpointName,
                StudentAcademicRecordResourceName,
                seed.DocumentUuid
            )
        )
            .Should()
            .Be(1);
    }

    private async Task AssertNoPeopleCreateSideEffectsAsync(AuthorizationStudentAcademicRecordSeed seed)
    {
        (await _context.CountDocumentRowsAsync(seed.DocumentUuid)).Should().Be(0);
        (
            await _context.CountResourceRootRowsAsync(
                ProjectEndpointName,
                StudentAcademicRecordResourceName,
                seed.DocumentUuid
            )
        )
            .Should()
            .Be(0);
        (await _context.CountResourceRootRowsAsync(ProjectEndpointName, StudentAcademicRecordResourceName))
            .Should()
            .Be(0);
        (
            await _context.CountResourceCollectionRowsAsync(
                ProjectEndpointName,
                StudentAcademicRecordResourceName
            )
        )
            .Should()
            .Be(0);
    }

    private async Task AssertNoStudentSchoolCreateSideEffectsAsync(AuthorizationStudentSchoolSeed seed)
    {
        (await _context.CountDocumentRowsAsync(seed.DocumentUuid)).Should().Be(0);
        (
            await _context.CountResourceRootRowsAsync(
                ProjectEndpointName,
                StudentSchoolResourceName,
                seed.DocumentUuid
            )
        )
            .Should()
            .Be(0);
        (await _context.CountResourceRootRowsAsync(ProjectEndpointName, StudentSchoolResourceName))
            .Should()
            .Be(0);
    }

    private static void AssertRelationshipDenied(
        UpsertResult result,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        failure
            .RelationshipFailure.ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Contain(expectedFailureKind);
    }

    private static void AssertPeopleRelationshipDenied(
        UpsertResult result,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind,
        IReadOnlyList<long> expectedClaimEducationOrganizationIds
    )
    {
        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        failure
            .RelationshipFailure.ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(expectedClaimEducationOrganizationIds);

        var failedStrategy = failure.RelationshipFailure.FailedStrategies.Should().ContainSingle().Subject;
        failedStrategy
            .StrategyName.Should()
            .Be(RelationshipAuthorizationCrudTestSupport.RelationshipsWithStudentsOnly);
        failedStrategy
            .StrategyKind.Should()
            .Be(RelationshipAuthorizationCrudTestSupport.RelationshipsWithStudentsOnly);

        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Subject;
        failedSubject.FailureKind.Should().Be(expectedFailureKind);
        failedSubject.RootBinding.TableName.Should().Be("authz.AuthorizationStudentAcademicRecordResource");
        failedSubject.RootBinding.ColumnName.Should().Be("StudentAcademicRecord_DocumentId");
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
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
        failedSubject.PersonSubject.PathKind.Should().Be("TransitiveJoinPath");
        failedSubject.PersonSubject.ProposedAnchor.Should().NotBeNull();
        failedSubject.PersonSubject.ProposedAnchor!.Kind.Should().Be("FirstHop");
        failedSubject
            .PersonSubject.ProposedAnchor.Binding.ColumnName.Should()
            .Be("StudentAcademicRecord_DocumentId");
        failedSubject.PersonSubject.Hint.Should().Contain("StudentSchoolAssociation");
    }

    private static void AssertDirectStudentRelationshipDenied(
        UpsertResult result,
        string expectedStrategyName
    )
    {
        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        failure
            .RelationshipFailure.ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);

        var failedStrategy = failure.RelationshipFailure.FailedStrategies.Should().ContainSingle().Subject;
        failedStrategy.StrategyName.Should().Be(expectedStrategyName);
        failedStrategy.StrategyKind.Should().Be(expectedStrategyName);

        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Subject;
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.RootBinding.TableName.Should().Be("authz.AuthorizationStudentSchoolResource");
        failedSubject.RootBinding.ColumnName.Should().Be("Student_DocumentId");
        failedSubject
            .SecurableElements.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RelationshipAuthorizationSecurableElement(
                    "Student",
                    "$.studentReference.studentUniqueId",
                    "StudentUniqueId"
                )
            );
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
        failedSubject.PersonSubject.PathKind.Should().Be("DirectRootColumn");
        failedSubject.PersonSubject.ProposedAnchor.Should().NotBeNull();
        failedSubject.PersonSubject.ProposedAnchor!.Kind.Should().Be("RootRow");
        failedSubject.PersonSubject.ProposedAnchor.Binding.ColumnName.Should().Be("Student_DocumentId");
        failedSubject.PersonSubject.Hint.Should().Contain("StudentSchoolAssociation");
    }

    private static IReadOnlyList<long> CreateUniqueClaimEducationOrganizationIds(int uniqueCount)
    {
        if (uniqueCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(uniqueCount));
        }

        return
        [
            ClaimEducationOrganizationId,
            .. Enumerable.Range(0, uniqueCount - 1).Select(static index => 1000L + index),
        ];
    }

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

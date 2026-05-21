// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("RelationalPut")]
public class Given_A_PostgresqlRelationalPutAuthorizationTests_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-1111-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-1111-0000-0000-000000000003")), 300, "West School"),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("bbbbbbbb-1111-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("bbbbbbbb-1111-0000-0000-000000000002")), 200, "P2"),
    ];

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

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, 300);
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
    public async Task It_denies_stored_value_put_and_leaves_document_resource_identity_and_journal_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000001",
            401,
            "stored-denied-no-op",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            existingSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpdateRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Stored);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_proposed_value_put_and_leaves_document_resource_identity_and_journal_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000002",
            402,
            "proposed-denied-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "proposed-denied-change",
            SchoolId = 300,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpdateRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    private static void AssertUpdateRelationshipDenied(
        UpdateResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource
    )
    {
        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>().Subject;
        failure.RelationshipFailure.ValueSource.Should().Be(expectedValueSource);
        failure
            .RelationshipFailure.ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Contain(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
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
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("RelationalPost")]
public class Given_A_PostgresqlRelationalPostAsUpdateAuthorizationTests_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-2222-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-2222-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-2222-0000-0000-000000000003")), 300, "West School"),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("bbbbbbbb-2222-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("bbbbbbbb-2222-0000-0000-000000000002")), 200, "P2"),
    ];

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

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, 300);
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
    public async Task It_denies_stored_value_post_as_update_and_leaves_document_resource_identity_and_journal_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000001",
            501,
            "stored-denied-existing",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000001")),
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpsertRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Stored);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_denies_proposed_value_post_as_update_and_leaves_document_resource_identity_and_journal_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000002",
            502,
            "proposed-denied-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000002")),
            Name = "proposed-denied-change",
            SchoolId = 300,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpsertRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    private static void AssertUpsertRelationshipDenied(
        UpsertResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource
    )
    {
        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure.RelationshipFailure.ValueSource.Should().Be(expectedValueSource);
        failure
            .RelationshipFailure.ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Contain(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
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
}

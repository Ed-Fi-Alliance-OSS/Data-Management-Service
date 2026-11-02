// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalPut")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_MssqlRelationalPutAuthorizationTests_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    /// <summary>
    /// A custom view over School authorizing School 100 only. An update is checked twice against it, once
    /// for the stored School and once for the proposed School, so the two are covered separately below.
    /// </summary>
    private const string CustomViewStrategyName = "SchoolWithPutProviderTest";
    private static readonly IReadOnlyList<string> _customViewStrategy = [CustomViewStrategyName];

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("eeeeeeee-1111-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-1111-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-1111-0000-0000-000000000003")), 300, "West School"),
        new(
            new DocumentUuid(Guid.Parse("eeeeeeee-1111-0000-0000-000000000004")),
            (int)ClaimEducationOrganizationId,
            "Claim School"
        ),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("ffffffff-1111-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("ffffffff-1111-0000-0000-000000000002")), 200, "P2"),
    ];

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

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, 300);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId);
        _context.ResetRecorder();

        await _context.CreateSchoolCustomAuthViewAsync(CustomViewStrategyName, [100]);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DropCustomAuthViewAsync(CustomViewStrategyName);
            await _context.DisposeAsync();
        }
    }

    [Test]
    public async Task Given_A_Custom_View_Strategy_It_Authorizes_An_Update_Whose_Stored_And_Proposed_Schools_The_View_Includes()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000601",
            601,
            "custom-view-authorized-existing",
            100,
            []
        );
        var proposedSeed = existingSeed with { Name = "custom-view-authorized-change" };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            _customViewStrategy
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public async Task Given_A_Custom_View_Strategy_It_Denies_An_Update_Whose_Proposed_School_The_View_Excludes()
    {
        // The stored School is inside the view, so only the proposed School can produce this denial. Seeding
        // the target row with an authorized School is what keeps the stored check from masking it.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000602",
            602,
            "custom-view-proposed-denied-existing",
            100,
            []
        );
        var proposedSeed = existingSeed with { Name = "custom-view-proposed-denied-change", SchoolId = 200 };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            _customViewStrategy
        );

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureCustomViewNotAuthorized>()
            .Which.CustomViewFailure.StrategyName.Should()
            .Be(CustomViewStrategyName);
    }

    [Test]
    public async Task Given_A_Custom_View_Strategy_It_Denies_An_Update_Whose_Stored_School_The_View_Excludes()
    {
        // The mirror image: the proposed School is inside the view and the stored School is not, so this
        // denial can only come from the stored check.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000603",
            603,
            "custom-view-stored-denied-existing",
            200,
            []
        );
        var proposedSeed = existingSeed with { Name = "custom-view-stored-denied-change", SchoolId = 100 };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            _customViewStrategy
        );

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureCustomViewNotAuthorized>()
            .Which.CustomViewFailure.StrategyName.Should()
            .Be(CustomViewStrategyName);
    }

    [Test]
    public async Task It_authorizes_put_and_updates_document_resource()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000004",
            604,
            "authorized-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "authorized-change",
            SchoolId = 200,
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

        var success = result.Should().BeOfType<UpdateResult.UpdateSuccess>().Subject;
        success.ExistingDocumentUuid.Should().Be(existingSeed.DocumentUuid);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Document.ContentVersion.Should().BeGreaterThan(before.Document.ContentVersion);
        after.Document.DocumentUuid.Should().Be(existingSeed.DocumentUuid.Value);
        AssertRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_denies_stored_value_put_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000001",
            601,
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
    public async Task It_returns_403_before_stale_if_match_when_proposed_put_values_are_unauthorized()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000005",
            605,
            "proposed-denied-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "proposed-denied-if-match-change",
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
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: "\"stale-etag\""
        );

        AssertUpdateRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_returns_412_after_authorized_put_values_with_stale_if_match()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000006",
            606,
            "authorized-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "authorized-if-match-change",
            SchoolId = 200,
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
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: "\"stale-etag\""
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_authorizes_put_with_inverted_relationship_filtering()
    {
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(100, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(200, ClaimEducationOrganizationId);

        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000007",
            607,
            "inverted-existing",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "inverted-change",
            SchoolId = 100,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.InvertedEdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        AssertRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_authorizes_profiled_put_with_hidden_stored_school_in_the_finalized_proposed_row()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000008",
            608,
            "profile-hidden-existing",
            100,
            []
        );
        var proposedSeed = existingSeed with { Name = "profile-hidden-change" };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var writeBody = AuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenBody(proposedSeed);
        var profileContext = AuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenProfileContext(
            _context.MappingSet,
            writeBody.DeepClone()
        );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: profileContext,
            requestBody: writeBody
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        AssertRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_denies_profiled_put_with_unauthorized_finalized_proposed_school_and_leaves_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000012",
            612,
            "profile-proposed-denied-existing",
            100,
            []
        );
        var proposedSeed = existingSeed with { Name = "profile-proposed-denied-change", SchoolId = 300 };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        var writeBody = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(
            proposedSeed
        );
        var profileContext = AuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
            _context.MappingSet,
            writeBody.DeepClone()
        );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: profileContext,
            requestBody: writeBody
        );

        AssertUpdateRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_authorizes_put_with_mssql_scalar_claim_parameters_below_the_tvp_threshold()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000009",
            609,
            "scalar-claims-existing",
            (int)ClaimEducationOrganizationId,
            []
        );
        var proposedSeed = existingSeed with { Name = "scalar-claims-change" };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            CreateUniqueClaimEducationOrganizationIds(1999),
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _context.AssertUpdateRelationshipAuthorizationUsesScalarClaimParameters(1999);
    }

    [Test]
    public async Task It_maps_mssql_auth1_stored_null_failure_for_put_and_leaves_rows_unchanged()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("aaaaaaaa-3333-0000-0000-000000000010")),
            610,
            "stored-null-existing"
        );
        var proposedSeed = existingSeed with { Name = "stored-null-change", NullableSchoolId = 100 };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpdateAuthorizationNullableByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpdateRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Stored,
            RelationshipAuthorizationSubjectFailureKind.StoredValueNull
        );
        var after = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_maps_mssql_auth1_proposed_missing_failure_for_put_and_leaves_rows_unchanged()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("aaaaaaaa-3333-0000-0000-000000000011")),
            611,
            "proposed-missing-existing",
            100
        );
        var proposedSeed = existingSeed with { Name = "proposed-missing-change", NullableSchoolId = null };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpdateAuthorizationNullableByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpdateRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Proposed,
            RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing
        );
        var after = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_proposed_value_put_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000002",
            602,
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

    [Test]
    public async Task It_denies_a_missing_proposed_school_on_put_and_preserves_stored_state()
    {
        // DMS-1659 AC4: the stored School 100 authorizes, so only the proposed School decides. A School that
        // does not exist answers exactly like the existing unrelated School 300.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000021",
            421,
            "missing-proposed-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var missingResult = await _context.UpdateAuthorizationRootChildByIdAsync(
            existingSeed with
            {
                Name = "missing-proposed-change",
                SchoolId = 999,
            },
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );
        var unrelatedResult = await _context.UpdateAuthorizationRootChildByIdAsync(
            existingSeed with
            {
                Name = "unrelated-proposed-change",
                SchoolId = 300,
            },
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertSingleProposedNoRelationshipSubject(missingResult)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [TestCase(999, "P1")]
    [TestCase(999, "Missing")]
    [TestCase(300, "P1")]
    [TestCase(300, "Missing")]
    public async Task It_denies_profiled_put_before_collection_reference_failures_and_preserves_stored_state(
        int proposedSchoolId,
        string classPeriodName
    )
    {
        // Before DMS-1659 a profiled write returned the missing-reference 409 ahead of proposed authorization.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000022",
            422,
            "profile-missing-proposed-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var missingResult = await PutWithVisibleProfileAsync(
            existingSeed with
            {
                Name = "profile-missing-proposed-change",
                SchoolId = proposedSchoolId,
                ClassPeriods = [new ClassPeriodReferenceSeed(classPeriodName, 100)],
            },
            existingSeed.DocumentUuid
        );
        var unrelatedResult = await PutWithVisibleProfileAsync(
            existingSeed with
            {
                Name = "profile-unrelated-proposed-change",
                SchoolId = 300,
                ClassPeriods = [new ClassPeriodReferenceSeed("P1", 100)],
            },
            existingSeed.DocumentUuid
        );

        AssertSingleProposedNoRelationshipSubject(missingResult)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_returns_a_missing_child_reference_conflict_after_profiled_put_authorization_without_changing_rows(
        bool schoolVisible
    )
    {
        // Both visible and hidden-preserved School 100 authorize before the child reference conflict.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000023",
            423,
            "profile-hidden-missing-child-existing",
            100,
            []
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        var proposedSeed = existingSeed with
        {
            Name = "profile-hidden-missing-child-change",
            ClassPeriods = [new ClassPeriodReferenceSeed("Missing", 100)],
        };
        var writeBody = schoolVisible
            ? RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(proposedSeed)
            : CreateRootSchoolHiddenBodyWithClassPeriods(proposedSeed);

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: schoolVisible
                ? AuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
                    _context.MappingSet,
                    writeBody.DeepClone()
                )
                : AuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenProfileContext(
                    _context.MappingSet,
                    writeBody.DeepClone()
                ),
            requestBody: writeBody
        );

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureReference>()
            .Which.InvalidDocumentReferences.Select(static failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing));
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task Given_A_Custom_View_Strategy_It_Denies_An_Update_Whose_Proposed_School_Is_Unresolved_Like_An_Excluded_School()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000604",
            604,
            "custom-view-missing-existing",
            100,
            []
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var missingResult = await _context.UpdateAuthorizationRootChildByIdAsync(
            existingSeed with
            {
                Name = "custom-view-missing-change",
                SchoolId = 999,
            },
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            _customViewStrategy
        );
        var excludedResult = await _context.UpdateAuthorizationRootChildByIdAsync(
            existingSeed with
            {
                Name = "custom-view-excluded-change",
                SchoolId = 200,
            },
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            _customViewStrategy
        );

        var missingFailure = missingResult
            .Should()
            .BeOfType<UpdateResult.UpdateFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure;
        missingFailure.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.NoMatchingRow);
        missingFailure.ValueSource.Should().Be(CustomViewAuthorizationFailureValueSource.Proposed);
        missingFailure
            .Should()
            .BeEquivalentTo(
                excludedResult
                    .Should()
                    .BeOfType<UpdateResult.UpdateFailureCustomViewNotAuthorized>()
                    .Subject.CustomViewFailure,
                options => options.WithStrictOrdering()
            );
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_profiled_put_identically_before_non_creatable_collection_inserts()
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000027",
            427,
            "profile-policy-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(storedSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            Name = "profile-policy-change",
            SchoolId = 999,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        var missingResult = await PutWithVisibleProfileAsync(
            candidate,
            storedSeed.DocumentUuid,
            classPeriodsCreatable: false
        );
        var afterMissing = await _context.ReadAuthorizationRootChildSideEffectStateAsync(
            storedSeed.DocumentUuid
        );
        afterMissing.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        var unrelatedResult = await PutWithVisibleProfileAsync(
            candidate with
            {
                SchoolId = 300,
            },
            storedSeed.DocumentUuid,
            classPeriodsCreatable: false
        );

        AuthorizationRootChildProfileTestSupport.AssertNoRelationshipResponseParity(
            AssertSingleProposedNoRelationshipSubject(missingResult),
            AssertSingleProposedNoRelationshipSubject(unrelatedResult)
        );
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_enforces_non_creatable_collection_policy_after_authorized_profiled_put()
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000028",
            428,
            "profile-authorized-policy-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(storedSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);

        var result = await PutWithVisibleProfileAsync(
            storedSeed with
            {
                Name = "profile-authorized-policy-change",
                ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
            },
            storedSeed.DocumentUuid,
            classPeriodsCreatable: false
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureProfileDataPolicy>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [TestCase(999)]
    [TestCase(300)]
    [TestCase(100)]
    public async Task It_authorizes_profiled_put_before_multiple_missing_collection_reference_failures(
        int proposedSchoolId
    )
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000029",
            429,
            "profile-multiple-missing-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(storedSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            Name = "profile-multiple-missing-change",
            SchoolId = proposedSchoolId,
            ClassPeriods =
            [
                new ClassPeriodReferenceSeed("Missing1", 100),
                new ClassPeriodReferenceSeed("Missing2", 200),
            ],
        };

        var result = await PutWithVisibleProfileAsync(candidate, storedSeed.DocumentUuid);

        if (proposedSchoolId == 100)
        {
            result
                .Should()
                .BeOfType<UpdateResult.UpdateFailureReference>()
                .Which.InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
                .Should()
                .Equal(
                    ("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing),
                    ("$.classPeriods[1].classPeriodReference", DocumentReferenceFailureReason.Missing)
                );
        }
        else
        {
            var unrelatedResult = await PutWithVisibleProfileAsync(
                candidate with
                {
                    SchoolId = 300,
                    ClassPeriods =
                    [
                        new ClassPeriodReferenceSeed("P1", 100),
                        new ClassPeriodReferenceSeed("P2", 200),
                    ],
                },
                storedSeed.DocumentUuid
            );
            AuthorizationRootChildProfileTestSupport.AssertNoRelationshipResponseParity(
                AssertSingleProposedNoRelationshipSubject(result),
                AssertSingleProposedNoRelationshipSubject(unrelatedResult)
            );
        }
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    private async Task<UpdateResult> PutWithVisibleProfileAsync(
        AuthorizationRootChildSeed proposedSeed,
        DocumentUuid documentUuid,
        bool classPeriodsCreatable = true
    )
    {
        var writeBody = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(
            proposedSeed
        );

        return await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            documentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: AuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
                _context.MappingSet,
                writeBody.DeepClone(),
                classPeriodsCreatable
            ),
            requestBody: writeBody
        );
    }

    private static JsonNode CreateRootSchoolHiddenBodyWithClassPeriods(AuthorizationRootChildSeed seed)
    {
        var body = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(seed);
        body.AsObject().Remove("schoolReference");

        return body;
    }

    private static RelationshipAuthorizationFailure AssertSingleProposedNoRelationshipSubject(
        UpdateResult result
    )
    {
        if (result is UpdateResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>().Subject;
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

    private static void AssertUpdateRelationshipDenied(
        UpdateResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind =
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
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
            .Contain(expectedFailureKind);
    }

    private static void AssertRootRow(
        AuthorizationWriteSideEffectState state,
        AuthorizationRootChildSeed expectedSeed
    )
    {
        var rootRow = state
            .ResourceTables.Single(static table => table.TableName == "authz.AuthorizationRootChildResource")
            .Rows.Should()
            .ContainSingle()
            .Subject;

        rootRow["AuthorizationRootChildId"].Should().Be(expectedSeed.AuthorizationRootChildId.ToString());
        rootRow["Name"].Should().Be(expectedSeed.Name);
        rootRow["School_SchoolId"].Should().Be(expectedSeed.SchoolId.ToString());
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
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalPut")]
[Category("RelationalPost")]
[Category("PeopleRelationship")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_MssqlRelationalPutAuthorizationTests_With_A_Synthetic_People_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string TermDescriptor = "uri://ed-fi.org/TermDescriptor#Fall Semester";
    private const string EntryGradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-5555-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-5555-0000-0000-000000000002")), 300, "West School"),
    ];

    private static readonly SchoolYearTypeSeed _schoolYearSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000001")),
        2026,
        true,
        "2026"
    );

    private static readonly StudentSeed _authorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000101")),
        "put-10001",
        "Ari",
        "Able"
    );

    private static readonly StudentSeed _unauthorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000102")),
        "put-10002",
        "Blake",
        "Baker"
    );

    private static readonly StudentSchoolAssociationSeed _authorizedStudentSchoolAssociationSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000201")),
        _authorizedStudentSeed.StudentUniqueId,
        100,
        _schoolYearSeed.SchoolYear,
        EntryGradeLevelDescriptor,
        new DateOnly(2026, 8, 15)
    );

    private static readonly StudentSchoolAssociationSeed _unauthorizedStudentSchoolAssociationSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000202")),
        _unauthorizedStudentSeed.StudentUniqueId,
        300,
        _schoolYearSeed.SchoolYear,
        EntryGradeLevelDescriptor,
        new DateOnly(2026, 8, 15)
    );

    private static readonly StudentAcademicRecordSeed _authorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000301")),
        100,
        _schoolYearSeed.SchoolYear,
        _authorizedStudentSeed.StudentUniqueId,
        TermDescriptor
    );

    private static readonly StudentAcademicRecordSeed _unauthorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000302")),
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

        await _context.SeedTermDescriptorAsync(
            Guid.Parse("bbbbbbbb-5555-0000-0000-000000000501"),
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
    public async Task It_authorizes_people_put_and_updates_the_existing_document()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000001",
            801,
            "people-put-authorized-existing",
            _authorizedStudentAcademicRecordSeed
        );
        var proposedSeed = existingSeed with { Name = "people-put-authorized-change" };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentAcademicRecordAsync(proposedSeed, existingSeed.DocumentUuid);

        var success = result.Should().BeOfType<UpdateResult.UpdateSuccess>().Subject;
        success.ExistingDocumentUuid.Should().Be(existingSeed.DocumentUuid);
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Document.ContentVersion.Should().BeGreaterThan(before.Document.ContentVersion);
        AssertPeopleRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_denies_people_put_on_stored_values_before_request_reference_resolution()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000002",
            802,
            "people-put-stored-denied-existing",
            _unauthorizedStudentAcademicRecordSeed
        );
        var proposedSeed = existingSeed with
        {
            Name = "people-put-stored-denied-missing-reference",
            StudentUniqueId = "missing-student",
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentAcademicRecordAsync(proposedSeed, existingSeed.DocumentUuid);

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Stored,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_returns_people_403_before_stale_if_match_when_put_proposed_values_are_unauthorized()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000003",
            803,
            "people-put-proposed-denied-existing",
            _authorizedStudentAcademicRecordSeed
        );
        var proposedSeed = existingSeed with
        {
            Name = "people-put-proposed-denied-change",
            EducationOrganizationId = _unauthorizedStudentAcademicRecordSeed.EducationOrganizationId,
            StudentUniqueId = _unauthorizedStudentAcademicRecordSeed.StudentUniqueId,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentAcademicRecordAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            ifMatch: "\"stale-etag\""
        );

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Proposed,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_put_for_unauthorized_people_only_proposed_value()
    {
        var existingSeed = new AuthorizationStudentSchoolSeed(
            new DocumentUuid(Guid.Parse("cccccccc-5555-0000-0000-000000000008")),
            808,
            "people-only-direct-put-existing",
            100,
            _authorizedStudentSeed.StudentUniqueId
        );
        var proposedSeed = existingSeed with
        {
            Name = "people-only-direct-put-denied-change",
            StudentUniqueId = _unauthorizedStudentSeed.StudentUniqueId,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentSchoolAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentSchoolSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentSchoolAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            RelationshipAuthorizationCrudTestSupport.PeopleOnlyStrategyNames
        );

        AssertDirectStudentRelationshipDenied(
            result,
            RelationshipAuthorizationCrudTestSupport.RelationshipsWithPeopleOnly
        );
        var after = await _context.ReadAuthorizationStudentSchoolSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_put_for_unauthorized_edorgs_and_people_proposed_value()
    {
        var existingSeed = new AuthorizationStudentSchoolSeed(
            new DocumentUuid(Guid.Parse("cccccccc-5555-0000-0000-000000000009")),
            809,
            "mixed-direct-put-existing",
            100,
            _authorizedStudentSeed.StudentUniqueId
        );
        var proposedSeed = existingSeed with
        {
            Name = "mixed-direct-put-denied-change",
            StudentUniqueId = _unauthorizedStudentSeed.StudentUniqueId,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentSchoolAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentSchoolSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentSchoolAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            RelationshipAuthorizationCrudTestSupport.EdOrgAndPeopleStrategyNames
        );

        AssertDirectStudentRelationshipDenied(
            result,
            RelationshipAuthorizationCrudTestSupport.RelationshipsWithEdOrgsAndPeople
        );
        var after = await _context.ReadAuthorizationStudentSchoolSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_authorizes_people_put_proposed_values_before_guarded_no_op_success()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000004",
            804,
            "people-put-guarded-no-op",
            _authorizedStudentAcademicRecordSeed
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentAcademicRecordAsync(existingSeed, existingSeed.DocumentUuid);

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _context.AssertPeopleUpdateRunsStoredThenProposedRelationshipAuthorization();
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_people_post_as_update_on_stored_values_before_request_reference_resolution()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000005",
            805,
            "people-post-update-stored-denied-existing",
            _unauthorizedStudentAcademicRecordSeed
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-5555-0000-0000-000000000005")),
            Name = "people-post-update-stored-denied-missing-reference",
            StudentUniqueId = "missing-student",
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PostStudentAcademicRecordAsync(candidateSeed);

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Stored,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_returns_people_403_before_stale_if_match_when_post_as_update_proposed_values_are_unauthorized()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000006",
            806,
            "people-post-update-proposed-denied-existing",
            _authorizedStudentAcademicRecordSeed
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-5555-0000-0000-000000000006")),
            Name = "people-post-update-proposed-denied-change",
            EducationOrganizationId = _unauthorizedStudentAcademicRecordSeed.EducationOrganizationId,
            StudentUniqueId = _unauthorizedStudentAcademicRecordSeed.StudentUniqueId,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PostStudentAcademicRecordAsync(candidateSeed, ifMatch: "\"stale-etag\"");

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Proposed,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_authorizes_people_post_as_update_proposed_values_before_guarded_no_op_success()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000007",
            807,
            "people-post-update-guarded-no-op",
            _authorizedStudentAcademicRecordSeed
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-5555-0000-0000-000000000007")),
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PostStudentAcademicRecordAsync(candidateSeed);

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _context.AssertPeopleUpdateRunsStoredThenProposedRelationshipAuthorization();
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    private async Task<UpdateResult> PutStudentAcademicRecordAsync(
        AuthorizationStudentAcademicRecordSeed seed,
        DocumentUuid documentUuid,
        string? ifMatch = null
    )
    {
        return await _context.UpdateAuthorizationStudentAcademicRecordByIdAsync(
            seed,
            documentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames,
            ifMatch
        );
    }

    private async Task<UpdateResult> PutStudentSchoolAsync(
        AuthorizationStudentSchoolSeed seed,
        DocumentUuid documentUuid,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null
    )
    {
        return await _context.UpdateAuthorizationStudentSchoolByIdAsync(
            seed,
            documentUuid,
            [ClaimEducationOrganizationId],
            strategyNames,
            ifMatch
        );
    }

    private async Task<UpsertResult> PostStudentAcademicRecordAsync(
        AuthorizationStudentAcademicRecordSeed seed,
        string? ifMatch = null
    )
    {
        return await _context.UpsertAuthorizationStudentAcademicRecordAsync(
            seed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames,
            ifMatch
        );
    }

    private static void AssertPeopleRelationshipDenied(
        UpdateResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        if (result is UpdateResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>().Subject;
        AssertPeopleRelationshipFailure(
            failure.RelationshipFailure,
            expectedValueSource,
            expectedFailureKind
        );
    }

    private static void AssertDirectStudentRelationshipDenied(
        UpdateResult result,
        string expectedStrategyName
    )
    {
        if (result is UpdateResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>().Subject;
        AssertDirectStudentRelationshipFailure(failure.RelationshipFailure, expectedStrategyName);
    }

    private static void AssertPeopleRelationshipDenied(
        UpsertResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        if (result is UpsertResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        AssertPeopleRelationshipFailure(
            failure.RelationshipFailure,
            expectedValueSource,
            expectedFailureKind
        );
    }

    private static void AssertDirectStudentRelationshipFailure(
        RelationshipAuthorizationFailure relationshipFailure,
        string expectedStrategyName
    )
    {
        relationshipFailure.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Proposed);
        relationshipFailure
            .ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);

        var failedStrategy = relationshipFailure.FailedStrategies.Should().ContainSingle().Subject;
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

    private static void AssertPeopleRelationshipFailure(
        RelationshipAuthorizationFailure relationshipFailure,
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        relationshipFailure.ValueSource.Should().Be(expectedValueSource);
        relationshipFailure
            .ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);

        var failedStrategy = relationshipFailure.FailedStrategies.Should().ContainSingle().Subject;
        failedStrategy
            .StrategyName.Should()
            .Be(RelationshipAuthorizationCrudTestSupport.RelationshipsWithStudentsOnly);
        failedStrategy
            .StrategyKind.Should()
            .Be(RelationshipAuthorizationCrudTestSupport.RelationshipsWithStudentsOnly);

        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Subject;
        failedSubject.FailureKind.Should().Be(expectedFailureKind);
        failedSubject.RootBinding.TableName.Should().Be("authz.AuthorizationStudentAcademicRecordResource");
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
        failedSubject.PersonSubject.Hint.Should().Contain("StudentSchoolAssociation");

        if (expectedValueSource is RelationshipAuthorizationFailureValueSource.Proposed)
        {
            failedSubject.PersonSubject.ProposedAnchor.Should().NotBeNull();
            failedSubject
                .PersonSubject.ProposedAnchor!.Binding.ColumnName.Should()
                .Be("StudentAcademicRecord_DocumentId");
        }
    }

    private static void AssertPeopleRootRow(
        AuthorizationWriteSideEffectState state,
        AuthorizationStudentAcademicRecordSeed expectedSeed
    )
    {
        var rootRow = state
            .ResourceTables.Single(static table =>
                table.TableName == "authz.AuthorizationStudentAcademicRecordResource"
            )
            .Rows.Should()
            .ContainSingle()
            .Subject;

        rootRow["AuthorizationStudentAcademicRecordId"]
            .Should()
            .Be(expectedSeed.AuthorizationStudentAcademicRecordId.ToString());
        rootRow["Name"].Should().Be(expectedSeed.Name);
        rootRow["StudentAcademicRecord_EducationOrganizationId"]
            .Should()
            .Be(expectedSeed.EducationOrganizationId.ToString());
        rootRow["StudentAcademicRecord_SchoolYear"].Should().Be(expectedSeed.SchoolYear.ToString());
        rootRow["StudentAcademicRecord_StudentUniqueId"].Should().Be(expectedSeed.StudentUniqueId);
    }

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

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalPost")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_MssqlRelationalPostAsUpdateAuthorizationTests_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("eeeeeeee-2222-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-2222-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-2222-0000-0000-000000000003")), 300, "West School"),
        new(
            new DocumentUuid(Guid.Parse("eeeeeeee-2222-0000-0000-000000000004")),
            (int)ClaimEducationOrganizationId,
            "Claim School"
        ),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("ffffffff-2222-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("ffffffff-2222-0000-0000-000000000002")), 200, "P2"),
    ];

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
    public async Task It_authorizes_post_as_update_and_updates_the_existing_document()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000004",
            704,
            "authorized-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000004")),
            Name = "authorized-change",
            SchoolId = 200,
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

        var success = result.Should().BeOfType<UpsertResult.UpdateSuccess>().Subject;
        success.ExistingDocumentUuid.Should().Be(existingSeed.DocumentUuid);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Document.ContentVersion.Should().BeGreaterThan(before.Document.ContentVersion);
        after.Document.DocumentUuid.Should().Be(existingSeed.DocumentUuid.Value);
        AssertRootRow(after, candidateSeed);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_denies_stored_value_post_as_update_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000001",
            701,
            "stored-denied-existing",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000001")),
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
    public async Task It_returns_403_before_stale_if_match_when_proposed_post_as_update_values_are_unauthorized()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000005",
            705,
            "proposed-denied-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000005")),
            Name = "proposed-denied-if-match-change",
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
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: "\"stale-etag\""
        );

        AssertUpsertRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_returns_412_after_authorized_post_as_update_values_with_stale_if_match()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000006",
            706,
            "authorized-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000006")),
            Name = "authorized-if-match-change",
            SchoolId = 200,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: "\"stale-etag\""
        );

        result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_authorizes_post_as_update_with_inverted_relationship_filtering()
    {
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(100, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(200, ClaimEducationOrganizationId);

        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000007",
            707,
            "inverted-existing",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000007")),
            Name = "inverted-change",
            SchoolId = 100,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.InvertedEdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        AssertRootRow(after, candidateSeed);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_authorizes_profiled_post_as_update_with_hidden_stored_school_in_the_finalized_proposed_row()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000008",
            708,
            "profile-hidden-existing",
            100,
            []
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000008")),
            Name = "profile-hidden-change",
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var writeBody = AuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenBody(candidateSeed);
        var profileContext = AuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenProfileContext(
            _context.MappingSet,
            writeBody.DeepClone()
        );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: profileContext,
            requestBody: writeBody
        );

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        AssertRootRow(after, candidateSeed);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_denies_profiled_post_as_update_with_unauthorized_finalized_proposed_school_and_leaves_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000010",
            710,
            "profile-proposed-denied-existing",
            100,
            []
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000010")),
            Name = "profile-proposed-denied-change",
            SchoolId = 300,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        var writeBody = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(
            candidateSeed
        );
        var profileContext = AuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
            _context.MappingSet,
            writeBody.DeepClone()
        );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: profileContext,
            requestBody: writeBody
        );

        AssertUpsertRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_maps_mssql_auth1_stored_null_post_as_update_failure_and_leaves_rows_unchanged()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("aaaaaaaa-4444-0000-0000-000000000011")),
            711,
            "stored-null-existing"
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000011")),
            Name = "stored-null-change",
            NullableSchoolId = 100,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationNullableAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpsertRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Stored,
            RelationshipAuthorizationSubjectFailureKind.StoredValueNull
        );
        var after = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_maps_mssql_auth1_proposed_missing_post_as_update_failure_and_leaves_rows_unchanged()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("aaaaaaaa-4444-0000-0000-000000000012")),
            712,
            "proposed-missing-existing",
            100
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000012")),
            Name = "proposed-missing-change",
            NullableSchoolId = null,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationNullableAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpsertRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Proposed,
            RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing
        );
        var after = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_authorizes_post_as_update_with_mssql_structured_tvp_claim_parameters_at_the_threshold()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000009",
            709,
            "structured-claims-existing",
            (int)ClaimEducationOrganizationId,
            []
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000009")),
            Name = "structured-claims-change",
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            CreateUniqueClaimEducationOrganizationIds(2000),
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _context.AssertUpdateRelationshipAuthorizationUsesStructuredClaimParameter();
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_denies_proposed_value_post_as_update_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000002",
            702,
            "proposed-denied-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000002")),
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

    [Test]
    public async Task It_denies_a_missing_proposed_school_on_post_as_update_and_preserves_stored_state()
    {
        // DMS-1659 AC4: the natural key (AuthorizationRootChildId) is unchanged, so the POST resolves to the
        // existing target; its stored School 100 authorizes and only the proposed School decides.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000021",
            521,
            "missing-proposed-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        var missingCandidate = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000021")),
            Name = "missing-proposed-change",
            SchoolId = 999,
        };
        var unrelatedCandidate = missingCandidate with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000121")),
            Name = "unrelated-proposed-change",
            SchoolId = 300,
        };

        var missingResult = await _context.UpsertAuthorizationRootChildAsync(
            missingCandidate,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );
        var unrelatedResult = await _context.UpsertAuthorizationRootChildAsync(
            unrelatedCandidate,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertSingleProposedNoRelationshipSubject(missingResult)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(missingCandidate.DocumentUuid)).Should().Be(0);
        (await _context.CountDocumentRowsAsync(unrelatedCandidate.DocumentUuid)).Should().Be(0);
    }

    [TestCase(999, "P1")]
    [TestCase(999, "Missing")]
    [TestCase(300, "P1")]
    [TestCase(300, "Missing")]
    public async Task It_denies_profiled_post_as_update_before_collection_reference_failures_and_preserves_stored_state(
        int proposedSchoolId,
        string classPeriodName
    )
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000022",
            522,
            "profile-missing-proposed-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000022")),
            Name = "profile-missing-proposed-change",
            SchoolId = proposedSchoolId,
            ClassPeriods = [new ClassPeriodReferenceSeed(classPeriodName, 100)],
        };

        var result = await PostWithVisibleProfileAsync(candidateSeed);

        var unrelatedResult = await PostWithVisibleProfileAsync(
            candidateSeed with
            {
                SchoolId = 300,
                ClassPeriods = [new ClassPeriodReferenceSeed("P1", 100)],
            }
        );
        AssertSingleProposedNoRelationshipSubject(result)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [TestCase(999, "P1")]
    [TestCase(999, "Missing")]
    [TestCase(300, "P1")]
    [TestCase(300, "Missing")]
    public async Task It_denies_profiled_post_create_before_collection_reference_failures_without_side_effects(
        int proposedSchoolId,
        string classPeriodName
    )
    {
        var missingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000023",
            523,
            "profile-missing-create",
            proposedSchoolId,
            [new ClassPeriodReferenceSeed(classPeriodName, 100)]
        );
        var unrelatedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000024",
            524,
            "profile-unrelated-create",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        var missingResult = await PostWithVisibleProfileAsync(missingSeed);
        var unrelatedResult = await PostWithVisibleProfileAsync(unrelatedSeed);

        AssertSingleProposedNoRelationshipSubject(missingResult)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        (await _context.CountDocumentRowsAsync(missingSeed.DocumentUuid)).Should().Be(0);
        (await _context.CountDocumentRowsAsync(unrelatedSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_returns_a_missing_child_reference_conflict_after_profiled_post_as_update_authorization_without_side_effects()
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000025",
            525,
            "profile-authorized-child-conflict",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(storedSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000025")),
            Name = "profile-authorized-child-conflict-change",
            ClassPeriods = [new ClassPeriodReferenceSeed("Missing", 100)],
        };

        var result = await PostWithVisibleProfileAsync(candidate);

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureReference>()
            .Which.InvalidDocumentReferences.Select(static failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing));
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidate.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_returns_a_missing_child_reference_conflict_after_profiled_post_create_authorization_without_side_effects()
    {
        var seed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000026",
            526,
            "profile-authorized-create-child-conflict",
            100,
            [new ClassPeriodReferenceSeed("Missing", 100)]
        );

        var result = await PostWithVisibleProfileAsync(seed);

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureReference>()
            .Which.InvalidDocumentReferences.Select(static failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing));
        (await _context.CountDocumentRowsAsync(seed.DocumentUuid)).Should().Be(0);
        (await _context.CountReferentialIdentityRowsForAuthorizationRootChildAsync(seed)).Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_denies_profiled_post_identically_before_non_creatable_collection_inserts(
        bool createNew
    )
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000027",
            527,
            "profile-policy-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(storedSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000027")),
            AuthorizationRootChildId = createNew ? 529 : storedSeed.AuthorizationRootChildId,
            Name = "profile-policy-change",
            SchoolId = 999,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        var missingResult = await PostWithVisibleProfileAsync(candidate, classPeriodsCreatable: false);
        var afterMissing = await _context.ReadAuthorizationRootChildSideEffectStateAsync(
            storedSeed.DocumentUuid
        );
        afterMissing.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        var unrelatedResult = await PostWithVisibleProfileAsync(
            candidate with
            {
                SchoolId = 300,
            },
            classPeriodsCreatable: false
        );

        AuthorizationRootChildProfileTestSupport.AssertNoRelationshipResponseParity(
            AssertSingleProposedNoRelationshipSubject(missingResult),
            AssertSingleProposedNoRelationshipSubject(unrelatedResult)
        );
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidate.DocumentUuid)).Should().Be(0);
        if (createNew)
        {
            (await _context.CountReferentialIdentityRowsForAuthorizationRootChildAsync(candidate))
                .Should()
                .Be(0);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_enforces_non_creatable_collection_policy_after_authorized_profiled_post(
        bool createNew
    )
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000028",
            528,
            "profile-authorized-policy-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(storedSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000028")),
            AuthorizationRootChildId = createNew ? 530 : storedSeed.AuthorizationRootChildId,
            Name = "profile-authorized-policy-change",
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        var result = await PostWithVisibleProfileAsync(candidate, classPeriodsCreatable: false);

        result.Should().BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidate.DocumentUuid)).Should().Be(0);
        if (createNew)
        {
            (await _context.CountReferentialIdentityRowsForAuthorizationRootChildAsync(candidate))
                .Should()
                .Be(0);
        }
    }

    [TestCase(false, 999)]
    [TestCase(false, 300)]
    [TestCase(false, 100)]
    [TestCase(true, 999)]
    [TestCase(true, 300)]
    [TestCase(true, 100)]
    public async Task It_authorizes_profiled_post_before_multiple_missing_collection_reference_failures(
        bool createNew,
        int proposedSchoolId
    )
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000029",
            531,
            "profile-multiple-missing-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(storedSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000029")),
            AuthorizationRootChildId = createNew ? 532 : storedSeed.AuthorizationRootChildId,
            Name = "profile-multiple-missing-change",
            SchoolId = proposedSchoolId,
            ClassPeriods =
            [
                new ClassPeriodReferenceSeed("Missing1", 100),
                new ClassPeriodReferenceSeed("Missing2", 200),
            ],
        };

        var result = await PostWithVisibleProfileAsync(candidate);

        if (proposedSchoolId == 100)
        {
            result
                .Should()
                .BeOfType<UpsertResult.UpsertFailureReference>()
                .Which.InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
                .Should()
                .Equal(
                    ("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing),
                    ("$.classPeriods[1].classPeriodReference", DocumentReferenceFailureReason.Missing)
                );
        }
        else
        {
            var unrelatedResult = await PostWithVisibleProfileAsync(
                candidate with
                {
                    SchoolId = 300,
                    ClassPeriods =
                    [
                        new ClassPeriodReferenceSeed("P1", 100),
                        new ClassPeriodReferenceSeed("P2", 200),
                    ],
                }
            );
            AuthorizationRootChildProfileTestSupport.AssertNoRelationshipResponseParity(
                AssertSingleProposedNoRelationshipSubject(result),
                AssertSingleProposedNoRelationshipSubject(unrelatedResult)
            );
        }
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidate.DocumentUuid)).Should().Be(0);
        if (createNew)
        {
            (await _context.CountReferentialIdentityRowsForAuthorizationRootChildAsync(candidate))
                .Should()
                .Be(0);
        }
    }

    private async Task<UpsertResult> PostWithVisibleProfileAsync(
        AuthorizationRootChildSeed seed,
        bool classPeriodsCreatable = true
    )
    {
        var writeBody = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(
            seed
        );

        return await _context.UpsertAuthorizationRootChildAsync(
            seed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: AuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
                _context.MappingSet,
                writeBody.DeepClone(),
                classPeriodsCreatable
            ),
            requestBody: writeBody
        );
    }

    private static RelationshipAuthorizationFailure AssertSingleProposedNoRelationshipSubject(
        UpsertResult result
    )
    {
        if (result is UpsertResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

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

    private static void AssertUpsertRelationshipDenied(
        UpsertResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind =
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
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
            .Contain(expectedFailureKind);
    }

    private static void AssertRootRow(
        AuthorizationWriteSideEffectState state,
        AuthorizationRootChildSeed expectedSeed
    )
    {
        var rootRow = state
            .ResourceTables.Single(static table => table.TableName == "authz.AuthorizationRootChildResource")
            .Rows.Should()
            .ContainSingle()
            .Subject;

        rootRow["AuthorizationRootChildId"].Should().Be(expectedSeed.AuthorizationRootChildId.ToString());
        rootRow["Name"].Should().Be(expectedSeed.Name);
        rootRow["School_SchoolId"].Should().Be(expectedSeed.SchoolId.ToString());
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
}

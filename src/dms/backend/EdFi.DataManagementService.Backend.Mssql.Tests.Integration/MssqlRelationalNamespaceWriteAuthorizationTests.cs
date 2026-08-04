// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Real-SQL-Server coverage for NamespaceBased write authorization (DMS-1286): POST create, PUT,
/// POST-as-update, and DELETE through the production MSSQL backend.
/// </summary>
/// <remarks>
/// Every denial asserts three independent things. The typed failure carries the right kind and value source,
/// which is what proves the stored check ran before the proposed one — a proposed failure is only reachable
/// once the stored check has passed. The complete before/after
/// <see cref="AuthorizationWriteSideEffectState"/> snapshot proves no authoritative row, referential identity,
/// or tracking stamp moved. The no-DML command boundary proves the write session issued no persistence at all,
/// which is what generalizes the guarantee to root, child, extension, identity, and tracking writes alike.
/// The recorder is reset after arrangement in each test so only the act's commands are evaluated.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalNamespace")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Relational_Namespace_Write_Authorization_With_A_Synthetic_Namespace_Fixture
{
    private const string ProjectEndpointName = RelationshipAuthorizationCrudTestSupport.ProjectEndpointName;
    private const string ResourceName = RelationshipAuthorizationCrudTestSupport.NamespaceResourceName;
    private const string AuthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix;
    private const string SecondAuthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.SecondAuthorizedNamespacePrefix;
    private const string UnauthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.UnauthorizedNamespacePrefix;
    private const int AuthorizedSchoolId = (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId;
    private const string StaleETag = "\"stale-etag\"";
    private const string RootTableName = $"authz.{ResourceName}";

    private static readonly IReadOnlyList<string> _configuredPrefixes =
        RelationshipAuthorizationCrudTestSupport.ConfiguredNamespacePrefixes;
    private static readonly IReadOnlyList<string> _namespaceStrategy =
        RelationshipAuthorizationCrudTestSupport.NamespaceBasedStrategyNames;

    private static readonly QuerySchoolSeed _schoolSeed = new(
        new DocumentUuid(Guid.Parse("b1b1b1b1-0000-0000-0000-000000000001")),
        AuthorizedSchoolId,
        "North"
    );

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("b2b2b2b2-0000-0000-0000-000000000001")), AuthorizedSchoolId, "P1"),
        new(new DocumentUuid(Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002")), AuthorizedSchoolId, "P2"),
    ];

    private static readonly ClassPeriodReferenceSeed[] _firstClassPeriod = [new("P1", AuthorizedSchoolId)];
    private static readonly ClassPeriodReferenceSeed[] _secondClassPeriod = [new("P2", AuthorizedSchoolId)];

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
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateSchoolAsync(_schoolSeed)
        );

        foreach (var classPeriodSeed in _classPeriodSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateClassPeriodAsync(classPeriodSeed)
            );
        }

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

    // ── POST create ─────────────────────────────────────────────────────

    [Test]
    public async Task It_authorizes_post_create_and_inserts_document_root_and_child_rows()
    {
        var seed = NamespaceSeed(101, "create-authorized", AuthorizedPrefix + "assessments");

        var result = await UpsertAsync(seed);

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        var state = await _context.ReadAuthorizationNamespaceSideEffectStateAsync(seed.DocumentUuid);
        AssertRootRow(state, seed);
        AssertChildRowCount(state, 1);
        state.ReferentialIdentities.Should().ContainSingle();
    }

    [TestCase(
        false,
        NamespaceAuthorizationFailureKind.NamespaceMismatch,
        TestName = "It_denies_post_create_with_a_mismatching_proposed_namespace_and_writes_nothing"
    )]
    [TestCase(
        true,
        NamespaceAuthorizationFailureKind.ProposedNamespaceMissing,
        TestName = "It_denies_post_create_with_a_missing_proposed_namespace_and_writes_nothing"
    )]
    public async Task It_denies_post_create_and_writes_nothing(
        bool omitNamespace,
        NamespaceAuthorizationFailureKind expectedFailureKind
    )
    {
        var seed = NamespaceSeed(
            102,
            "create-denied",
            omitNamespace ? null : UnauthorizedPrefix + "assessments"
        );

        var result = await UpsertAsync(seed);

        AssertUpsertDenied(result, expectedFailureKind, NamespaceAuthorizationFailureValueSource.Proposed);
        await AssertNoRowsExistAsync(seed);
        _context.AssertNoPersistenceAfterNamespaceAuthorization();
    }

    // ── PUT ─────────────────────────────────────────────────────────────

    [Test]
    public async Task It_authorizes_put_and_updates_the_existing_document()
    {
        var existingSeed = await SeedExistingAsync(201, "put-existing", AuthorizedPrefix + "assessments");
        var proposedSeed = existingSeed with
        {
            Name = "put-changed",
            Namespace = SecondAuthorizedPrefix + "surveys",
            ClassPeriods = _secondClassPeriod,
        };
        var before = await ReadStateAsync(existingSeed);
        _context.ResetRecorder();

        var result = await UpdateAsync(proposedSeed, existingSeed.DocumentUuid);

        var success = result.Should().BeOfType<UpdateResult.UpdateSuccess>().Subject;
        success.ExistingDocumentUuid.Should().Be(existingSeed.DocumentUuid);
        var after = await ReadStateAsync(existingSeed);
        after.Document.ContentVersion.Should().BeGreaterThan(before.Document.ContentVersion);
        AssertRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_denies_put_on_an_unauthorized_stored_namespace_even_when_the_proposed_namespace_matches()
    {
        var existingSeed = await SeedExistingAsync(202, "put-stored-denied", UnauthorizedPrefix + "stored");
        var proposedSeed = existingSeed with
        {
            Name = "put-stored-denied-change",
            Namespace = AuthorizedPrefix + "assessments",
        };
        var before = await ReadStateAsync(existingSeed);
        _context.ResetRecorder();

        var result = await UpdateAsync(proposedSeed, existingSeed.DocumentUuid);

        // Stored is authorized first, so an authorized proposed value cannot rescue an unauthorized stored one.
        AssertUpdateDenied(
            result,
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored
        );
        await AssertStateUnchangedAsync(existingSeed, before);
    }

    [TestCase(
        false,
        NamespaceAuthorizationFailureKind.NamespaceMismatch,
        TestName = "It_denies_put_with_a_mismatching_proposed_namespace_after_stored_authorization_passes"
    )]
    [TestCase(
        true,
        NamespaceAuthorizationFailureKind.ProposedNamespaceMissing,
        TestName = "It_denies_put_with_a_missing_proposed_namespace_after_stored_authorization_passes"
    )]
    public async Task It_denies_put_on_the_proposed_namespace_after_stored_authorization_passes(
        bool omitNamespace,
        NamespaceAuthorizationFailureKind expectedFailureKind
    )
    {
        var existingSeed = await SeedExistingAsync(
            203,
            "put-proposed-denied",
            AuthorizedPrefix + "assessments"
        );
        var proposedSeed = existingSeed with
        {
            Name = "put-proposed-denied-change",
            Namespace = omitNamespace ? null : UnauthorizedPrefix + "proposed",
        };
        var before = await ReadStateAsync(existingSeed);
        _context.ResetRecorder();

        var result = await UpdateAsync(proposedSeed, existingSeed.DocumentUuid);

        // A proposed-value failure is only reachable once the stored check authorized, so the value source
        // itself establishes the stored-then-proposed order.
        AssertUpdateDenied(result, expectedFailureKind, NamespaceAuthorizationFailureValueSource.Proposed);
        await AssertStateUnchangedAsync(existingSeed, before);
    }

    [Test]
    public async Task It_returns_403_before_a_stale_if_match_when_the_stored_namespace_is_unauthorized()
    {
        var existingSeed = await SeedExistingAsync(204, "put-collision", UnauthorizedPrefix + "collision");
        var proposedSeed = existingSeed with
        {
            Name = "put-collision-change",
            Namespace = AuthorizedPrefix + "assessments",
        };
        var before = await ReadStateAsync(existingSeed);
        _context.ResetRecorder();

        var result = await UpdateAsync(proposedSeed, existingSeed.DocumentUuid, ifMatch: StaleETag);

        AssertUpdateDenied(
            result,
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored
        );
        await AssertStateUnchangedAsync(existingSeed, before);
    }

    [Test]
    public async Task It_returns_412_for_a_stale_if_match_once_namespace_authorization_passes()
    {
        var existingSeed = await SeedExistingAsync(205, "put-precondition", AuthorizedPrefix + "assessments");
        var proposedSeed = existingSeed with
        {
            Name = "put-precondition-change",
            Namespace = SecondAuthorizedPrefix + "surveys",
        };
        var before = await ReadStateAsync(existingSeed);
        _context.ResetRecorder();

        var result = await UpdateAsync(proposedSeed, existingSeed.DocumentUuid, ifMatch: StaleETag);

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>(
                "the precondition result must survive unchanged once authorization succeeds"
            );
        await AssertStateUnchangedAsync(existingSeed, before);
    }

    // ── POST-as-update ──────────────────────────────────────────────────

    [Test]
    public async Task It_authorizes_post_as_update_and_updates_the_existing_document()
    {
        var existingSeed = await SeedExistingAsync(301, "upsert-existing", AuthorizedPrefix + "assessments");
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("b4b4b4b4-0000-0000-0000-000000000301")),
            Name = "upsert-changed",
            Namespace = SecondAuthorizedPrefix + "surveys",
            ClassPeriods = _secondClassPeriod,
        };
        var before = await ReadStateAsync(existingSeed);
        _context.ResetRecorder();

        var result = await UpsertAsync(candidateSeed);

        var success = result.Should().BeOfType<UpsertResult.UpdateSuccess>().Subject;
        success.ExistingDocumentUuid.Should().Be(existingSeed.DocumentUuid);
        var after = await ReadStateAsync(existingSeed);
        after.Document.ContentVersion.Should().BeGreaterThan(before.Document.ContentVersion);
        after.Document.DocumentUuid.Should().Be(existingSeed.DocumentUuid.Value);
        AssertRootRow(after, candidateSeed);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid))
            .Should()
            .Be(0, "the candidate uuid must not create a second document");
    }

    [Test]
    public async Task It_denies_post_as_update_on_an_unauthorized_stored_namespace_even_when_the_proposed_namespace_matches()
    {
        var existingSeed = await SeedExistingAsync(
            302,
            "upsert-stored-denied",
            UnauthorizedPrefix + "stored"
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("b4b4b4b4-0000-0000-0000-000000000302")),
            Name = "upsert-stored-denied-change",
            Namespace = AuthorizedPrefix + "assessments",
        };
        var before = await ReadStateAsync(existingSeed);
        _context.ResetRecorder();

        var result = await UpsertAsync(candidateSeed);

        AssertUpsertDenied(
            result,
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored
        );
        await AssertStateUnchangedAsync(existingSeed, before);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [TestCase(
        false,
        NamespaceAuthorizationFailureKind.NamespaceMismatch,
        TestName = "It_denies_post_as_update_with_a_mismatching_proposed_namespace_after_stored_authorization_passes"
    )]
    [TestCase(
        true,
        NamespaceAuthorizationFailureKind.ProposedNamespaceMissing,
        TestName = "It_denies_post_as_update_with_a_missing_proposed_namespace_after_stored_authorization_passes"
    )]
    public async Task It_denies_post_as_update_on_the_proposed_namespace_after_stored_authorization_passes(
        bool omitNamespace,
        NamespaceAuthorizationFailureKind expectedFailureKind
    )
    {
        var existingSeed = await SeedExistingAsync(
            303,
            "upsert-proposed-denied",
            AuthorizedPrefix + "assessments"
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("b4b4b4b4-0000-0000-0000-000000000303")),
            Name = "upsert-proposed-denied-change",
            Namespace = omitNamespace ? null : UnauthorizedPrefix + "proposed",
        };
        var before = await ReadStateAsync(existingSeed);
        _context.ResetRecorder();

        var result = await UpsertAsync(candidateSeed);

        AssertUpsertDenied(result, expectedFailureKind, NamespaceAuthorizationFailureValueSource.Proposed);
        await AssertStateUnchangedAsync(existingSeed, before);
    }

    [Test]
    public async Task It_returns_403_before_a_stale_post_as_update_if_match_when_the_proposed_namespace_is_unauthorized()
    {
        var existingSeed = await SeedExistingAsync(304, "upsert-collision", AuthorizedPrefix + "assessments");
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("b4b4b4b4-0000-0000-0000-000000000304")),
            Name = "upsert-collision-change",
            Namespace = UnauthorizedPrefix + "proposed",
        };
        var before = await ReadStateAsync(existingSeed);
        _context.ResetRecorder();

        var result = await UpsertAsync(candidateSeed, ifMatch: StaleETag);

        AssertUpsertDenied(
            result,
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Proposed
        );
        await AssertStateUnchangedAsync(existingSeed, before);
    }

    // ── DELETE ──────────────────────────────────────────────────────────

    [Test]
    public async Task It_authorizes_delete_and_removes_document_root_and_child_rows()
    {
        var seed = await SeedExistingAsync(401, "delete-authorized", AuthorizedPrefix + "assessments");
        _context.ResetRecorder();

        var result = await DeleteAsync(seed);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        await AssertNoRowsExistAsync(seed);
    }

    [Test]
    public async Task It_returns_403_before_if_match_and_preserves_rows_when_both_would_fail()
    {
        var seed = await SeedExistingAsync(402, "delete-collision", UnauthorizedPrefix + "collision");
        var before = await ReadStateAsync(seed);
        _context.ResetRecorder();

        var result = await DeleteAsync(seed, ifMatch: StaleETag);

        AssertDeleteDenied(
            result,
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored
        );
        await AssertStateUnchangedAsync(seed, before);
    }

    [Test]
    public async Task It_returns_412_for_a_stale_delete_if_match_once_namespace_authorization_passes()
    {
        var seed = await SeedExistingAsync(403, "delete-precondition", AuthorizedPrefix + "assessments");
        var before = await ReadStateAsync(seed);
        _context.ResetRecorder();

        var result = await DeleteAsync(seed, ifMatch: StaleETag);

        result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
        await AssertStateUnchangedAsync(seed, before);
    }

    [Test]
    public async Task It_locks_authorizes_then_deletes_within_one_guarded_session()
    {
        var seed = await SeedExistingAsync(404, "delete-ordering", AuthorizedPrefix + "assessments");
        var getResult = await _context.GetByIdAsync(
            ProjectEndpointName,
            ResourceName,
            seed.DocumentUuid,
            [],
            _namespaceStrategy,
            namespacePrefixes: _configuredPrefixes
        );
        var etag = getResult.Should().BeOfType<GetResult.GetSuccess>().Subject.EdfiDoc[
            "_etag"
        ]!.GetValue<string>();
        _context.ResetRecorder();

        var result = await DeleteAsync(seed, ifMatch: etag);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        _context.AssertDeleteWithIfMatchNamespaceOrdering();
        await AssertNoRowsExistAsync(seed);
    }

    // ── Prefix cap on the write path ────────────────────────────────────

    [Test]
    public async Task It_returns_security_configuration_before_opening_a_write_session_at_the_prefix_cap()
    {
        var seed = NamespaceSeed(501, "create-prefix-cap", AuthorizedPrefix + "assessments");
        IReadOnlyList<string> prefixes =
        [
            AuthorizedPrefix,
            .. Enumerable
                .Range(0, NamespacePrefixLimitExceededException.MssqlScalarParameterLimit - 1)
                .Select(static index => $"uri://filler{index:D5}.example/"),
        ];

        var result = await UpsertAsync(seed, namespacePrefixes: prefixes);

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(NamespaceAuthorizationSecurityConfigurationMessages.PrefixCapExceeded(prefixes.Count));
        await AssertNoRowsExistAsync(seed);

        // The prefix cap is a planner terminal, so it must resolve before the write session issues anything.
        _context.AssertNoWriteCommandsIssued();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static AuthorizationNamespaceSeed NamespaceSeed(
        int authorizationNamespaceId,
        string name,
        string? @namespace
    ) =>
        new(
            new DocumentUuid(Guid.Parse($"b3b3b3b3-0000-0000-0000-{authorizationNamespaceId:D12}")),
            authorizationNamespaceId,
            name,
            @namespace,
            AuthorizedSchoolId,
            _firstClassPeriod
        );

    /// <summary>
    /// Seeds an existing row through the production write path with no strategies configured, so any stored
    /// namespace — authorized or not — can be established without first passing namespace authorization.
    /// </summary>
    private async Task<AuthorizationNamespaceSeed> SeedExistingAsync(
        int authorizationNamespaceId,
        string name,
        string? @namespace
    )
    {
        var seed = NamespaceSeed(authorizationNamespaceId, name, @namespace);

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNamespaceAsync(seed)
        );

        return seed;
    }

    private async Task<UpsertResult> UpsertAsync(
        AuthorizationNamespaceSeed seed,
        string? ifMatch = null,
        IReadOnlyList<string>? namespacePrefixes = null
    ) =>
        await _context.UpsertAuthorizationNamespaceAsync(
            seed,
            [],
            _namespaceStrategy,
            namespacePrefixes ?? _configuredPrefixes,
            ifMatch
        );

    private async Task<UpdateResult> UpdateAsync(
        AuthorizationNamespaceSeed seed,
        DocumentUuid documentUuid,
        string? ifMatch = null
    ) =>
        await _context.UpdateAuthorizationNamespaceByIdAsync(
            seed,
            documentUuid,
            [],
            _namespaceStrategy,
            _configuredPrefixes,
            ifMatch
        );

    private async Task<DeleteResult> DeleteAsync(AuthorizationNamespaceSeed seed, string? ifMatch = null) =>
        await _context.DeleteByIdAsync(
            ProjectEndpointName,
            ResourceName,
            seed.DocumentUuid,
            [],
            _namespaceStrategy,
            ifMatch,
            namespacePrefixes: _configuredPrefixes
        );

    private async Task<AuthorizationWriteSideEffectState> ReadStateAsync(AuthorizationNamespaceSeed seed) =>
        await _context.ReadAuthorizationNamespaceSideEffectStateAsync(seed.DocumentUuid);

    /// <summary>
    /// Every authoritative row and stamp for the target is byte-for-byte what it was before the denied
    /// operation, and the write session issued no persistence command after authorization ran.
    /// </summary>
    private async Task AssertStateUnchangedAsync(
        AuthorizationNamespaceSeed seed,
        AuthorizationWriteSideEffectState before
    )
    {
        var after = await ReadStateAsync(seed);

        after.Should().BeEquivalentTo(before, static options => options.WithStrictOrdering());
        _context.AssertNoPersistenceAfterNamespaceAuthorization();
    }

    private async Task AssertNoRowsExistAsync(AuthorizationNamespaceSeed seed)
    {
        (await _context.CountDocumentRowsAsync(seed.DocumentUuid)).Should().Be(0);
        (await _context.CountResourceRootRowsAsync(ProjectEndpointName, ResourceName, seed.DocumentUuid))
            .Should()
            .Be(0);
        (await _context.CountReferentialIdentityRowsForAuthorizationNamespaceAsync(seed)).Should().Be(0);
    }

    private static void AssertRootRow(
        AuthorizationWriteSideEffectState state,
        AuthorizationNamespaceSeed expectedSeed
    )
    {
        var rootRow = state
            .ResourceTables.Single(static table => table.TableName == RootTableName)
            .Rows.Should()
            .ContainSingle()
            .Subject;

        rootRow["AuthorizationNamespaceId"].Should().Be(expectedSeed.AuthorizationNamespaceId.ToString());
        rootRow["Name"].Should().Be(expectedSeed.Name);
        rootRow["Namespace"].Should().Be(expectedSeed.Namespace);
    }

    private static void AssertChildRowCount(AuthorizationWriteSideEffectState state, int expectedRowCount) =>
        state
            .ResourceTables.Single(static table => table.TableName == $"{RootTableName}ClassPeriod")
            .Rows.Should()
            .HaveCount(expectedRowCount);

    private static void AssertUpsertDenied(
        UpsertResult result,
        NamespaceAuthorizationFailureKind expectedFailureKind,
        NamespaceAuthorizationFailureValueSource expectedValueSource
    ) =>
        AssertNamespaceFailure(
            result
                .Should()
                .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
                .Subject.NamespaceFailure,
            expectedFailureKind,
            expectedValueSource
        );

    private static void AssertUpdateDenied(
        UpdateResult result,
        NamespaceAuthorizationFailureKind expectedFailureKind,
        NamespaceAuthorizationFailureValueSource expectedValueSource
    ) =>
        AssertNamespaceFailure(
            result
                .Should()
                .BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>()
                .Subject.NamespaceFailure,
            expectedFailureKind,
            expectedValueSource
        );

    private static void AssertDeleteDenied(
        DeleteResult result,
        NamespaceAuthorizationFailureKind expectedFailureKind,
        NamespaceAuthorizationFailureValueSource expectedValueSource
    ) =>
        AssertNamespaceFailure(
            result
                .Should()
                .BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>()
                .Subject.NamespaceFailure,
            expectedFailureKind,
            expectedValueSource
        );

    private static void AssertNamespaceFailure(
        NamespaceAuthorizationFailure failure,
        NamespaceAuthorizationFailureKind expectedFailureKind,
        NamespaceAuthorizationFailureValueSource expectedValueSource
    )
    {
        failure.FailureKind.Should().Be(expectedFailureKind);
        failure.ValueSource.Should().Be(expectedValueSource);
        failure.StrategyName.Should().Be(RelationshipAuthorizationCrudTestSupport.NamespaceBased);
        failure.ConfiguredNamespacePrefixes.Should().Equal(_configuredPrefixes);
    }
}

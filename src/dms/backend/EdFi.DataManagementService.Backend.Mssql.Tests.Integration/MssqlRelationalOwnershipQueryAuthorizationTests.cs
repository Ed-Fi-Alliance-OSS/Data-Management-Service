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
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Live-provider coverage for the <c>OwnershipBased</c> GET-many page filter on SQL Server. Every row is seeded
/// through the production write path with a creator token, so what a create stamps on
/// <c>dms.Document.CreatedByOwnershipTokenId</c> is exactly what the page filter is later evaluated against.
/// </summary>
/// <remarks>
/// SQL Server binds one scalar parameter per token, so this fixture also proves the 1,999-token list — the
/// largest configuration CMS permits — executes against the real engine, and that the per-command parameter
/// budget fails closed when the token list is composed with a prefix list that together exceed it.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Relational_Ownership_Query_Authorization_With_A_Synthetic_Fixture
{
    private const string ProjectEndpointName = RelationshipAuthorizationCrudTestSupport.ProjectEndpointName;
    private const string RootChildResourceName =
        RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName;
    private const string NamespaceResourceName =
        RelationshipAuthorizationCrudTestSupport.NamespaceResourceName;
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const int AuthorizedSchoolId = (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId;
    private const int InvertedOnlySchoolId = (int)
        RelationshipAuthorizationCrudTestSupport.SecondAuthorizedSchoolId;
    private const int UnauthorizedSchoolId = (int)
        RelationshipAuthorizationCrudTestSupport.UnauthorizedSchoolId;
    private const string AuthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix;
    private const string UnauthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.UnauthorizedNamespacePrefix;
    private const string CustomViewStrategyName = "SchoolWithOwnershipQueryCustomViewProviderTest";

    private const short OwnerToken = 42;
    private const short OtherToken = 7;
    private const short UnusedToken = 99;
    private const int OwnershipTokenLimit = OwnershipTokenLimitExceededException.OwnershipTokenLimit;

    private const string OwnershipJoinSql =
        "INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = r.[DocumentId]";
    private const string OwnershipPredicatePrefixSql =
        "doc.[CreatedByOwnershipTokenId] IS NOT NULL AND doc.[CreatedByOwnershipTokenId] IN (@ownershipTokenIds_0";

    private static readonly IReadOnlyList<string> _ownershipStrategy =
    [
        AuthorizationStrategyNameConstants.OwnershipBased,
    ];

    /// <summary>
    /// School 100 is owned, 200 is stamped with another client's token, and 300 is stamped null. The custom
    /// view below authorizes 100 and 200, so intersecting it with ownership isolates the owned one.
    /// </summary>
    private static readonly StampedSchoolSeed[] _schoolSeeds =
    [
        new(new QuerySchoolSeed(Uuid("c1c1c1c1", 1), AuthorizedSchoolId, "Owned North"), OwnerToken),
        new(new QuerySchoolSeed(Uuid("c1c1c1c1", 2), InvertedOnlySchoolId, "Other South"), OtherToken),
        new(new QuerySchoolSeed(Uuid("c1c1c1c1", 3), UnauthorizedSchoolId, "Unstamped West"), null),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(Uuid("c2c2c2c2", 1), AuthorizedSchoolId, "P1"),
        new(Uuid("c2c2c2c2", 2), AuthorizedSchoolId, "P2"),
    ];

    /// <summary>
    /// Seeded in this order, so dms.Document identity order — the GET-many sort order — matches the array
    /// order. The school each row references decides which relationship branch can satisfy it:
    /// school 100 the normal branch only, school 200 the inverted branch only, school 300 neither.
    /// Owned rows are 1, 2, 5, 6 and 7; row 3 carries another client's token and row 4 is stamped null.
    /// </summary>
    private static readonly StampedRootChildSeed[] _rootChildSeeds =
    [
        new(RootChild(1, "owned-plain", AuthorizedSchoolId, []), OwnerToken),
        new(
            RootChild(
                2,
                "owned-with-children",
                AuthorizedSchoolId,
                [
                    new ClassPeriodReferenceSeed("P1", AuthorizedSchoolId),
                    new ClassPeriodReferenceSeed("P2", AuthorizedSchoolId),
                ]
            ),
            OwnerToken
        ),
        new(RootChild(3, "other-token", AuthorizedSchoolId, []), OtherToken),
        new(RootChild(4, "unstamped", AuthorizedSchoolId, []), null),
        new(RootChild(5, "owned-inverted-only-school", InvertedOnlySchoolId, []), OwnerToken),
        new(RootChild(6, "owned-unauthorized-school", UnauthorizedSchoolId, []), OwnerToken),
        new(RootChild(7, "owned-second-plain", AuthorizedSchoolId, []), OwnerToken),
    ];

    private static readonly StampedNamespaceSeed[] _namespaceSeeds =
    [
        new(NamespaceSeed(1, "owned-matching", AuthorizedPrefix + "assessments"), OwnerToken),
        new(NamespaceSeed(2, "other-token-matching", AuthorizedPrefix + "surveys"), OtherToken),
        new(NamespaceSeed(3, "owned-nonmatching", UnauthorizedPrefix + "assessments"), OwnerToken),
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
            strict: false
        );
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateSchoolAsync(schoolSeed.Seed, schoolSeed.CreatorOwnershipTokenId)
            );
        }

        foreach (var classPeriodSeed in _classPeriodSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateClassPeriodAsync(classPeriodSeed)
            );
        }

        foreach (var rootChildSeed in _rootChildSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateAuthorizationRootChildAsync(
                    rootChildSeed.Seed,
                    rootChildSeed.CreatorOwnershipTokenId
                )
            );
        }

        foreach (var namespaceSeed in _namespaceSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateAuthorizationNamespaceAsync(
                    namespaceSeed.Seed,
                    namespaceSeed.CreatorOwnershipTokenId
                )
            );
        }

        await _context.CreateSchoolCustomAuthViewAsync(
            CustomViewStrategyName,
            [AuthorizedSchoolId, InvertedOnlySchoolId]
        );

        // One edge per branch, in one direction only, so the normal and inverted strategies are independently
        // satisfiable and no row can satisfy both.
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, AuthorizedSchoolId);
        await _context.DeleteAuthEdgeAsync(AuthorizedSchoolId, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(InvertedOnlySchoolId, ClaimEducationOrganizationId);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, InvertedOnlySchoolId);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, UnauthorizedSchoolId);
        await _context.DeleteAuthEdgeAsync(UnauthorizedSchoolId, ClaimEducationOrganizationId);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId);
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

    [SetUp]
    public void SetUp() => _context.ResetRecorder();

    [Test]
    public async Task It_returns_only_the_documents_stamped_with_the_callers_token_and_excludes_null_stamps()
    {
        var result = await QueryRootChildrenAsync(ownershipTokenIds: [OwnerToken]);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        AssertReturnedRootChildren(success, 1, 2, 5, 6, 7);
        success.TotalCount.Should().Be(5, "totalCount must count only owned rows");

        var pageSql = _context.AssertSingleQueryHydration().Plan.PageDocumentIdSql;
        pageSql.Should().Contain(OwnershipJoinSql);
        pageSql.Should().Contain(OwnershipPredicatePrefixSql);
    }

    [Test]
    public async Task It_matches_any_of_several_tokens_and_still_excludes_null_stamps()
    {
        var result = await QueryRootChildrenAsync(ownershipTokenIds: [OwnerToken, OtherToken]);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        AssertReturnedRootChildren(success, 1, 2, 3, 5, 6, 7);
        success.TotalCount.Should().Be(6);
    }

    [Test]
    public async Task It_returns_an_empty_page_and_zero_count_for_a_token_nothing_was_stamped_with()
    {
        var result = await QueryRootChildrenAsync(ownershipTokenIds: [UnusedToken]);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(0);

        // A non-matching token is a real filter that matched nothing, not a short-circuit: the page ran.
        success.SelectionSkipped.Should().BeFalse();
        _context.AssertSingleQueryHydration();
    }

    [Test]
    public async Task It_returns_an_empty_page_and_zero_count_without_hydrating_when_the_caller_has_no_tokens()
    {
        var result = await QueryRootChildrenAsync(ownershipTokenIds: []);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(0);
        success.SelectionSkipped.Should().BeTrue();
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_filters_before_paging_so_the_page_window_lands_on_owned_rows()
    {
        // Owned rows in document order are 1, 2, 5, 6 and 7. Skipping one owned row yields 2 and 5. Paging
        // before filtering would window rows 2 and 3 of the unfiltered set instead and return only row 2.
        var result = await QueryRootChildrenAsync(ownershipTokenIds: [OwnerToken], limit: 2, offset: 1);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        AssertReturnedRootChildren(success, 2, 5);
        success.TotalCount.Should().Be(5, "totalCount is the owned total, not the page size");
    }

    [Test]
    public async Task It_composes_the_ownership_filter_as_an_and_term_ahead_of_the_relationship_or_group()
    {
        // The OR group admits school 100 through the normal branch and school 200 through the inverted one,
        // never school 300. Ownership then keeps only the owned rows among those: 1, 2 and 7 at school 100 and
        // 5 at school 200. Row 3 (other token) and row 4 (null) are at an authorized school and still excluded.
        var result = await _context.QueryAsync(
            ProjectEndpointName,
            RootChildResourceName,
            [ClaimEducationOrganizationId],
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                AuthorizationStrategyNameConstants.OwnershipBased,
            ],
            ownershipTokenIds: [OwnerToken]
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        AssertReturnedRootChildren(success, 1, 2, 5, 7);
        success.TotalCount.Should().Be(4);

        var pageSql = _context.AssertSingleQueryHydration().Plan.PageDocumentIdSql;
        pageSql
            .IndexOf(OwnershipPredicatePrefixSql, StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                pageSql.IndexOf("EducationOrganizationIdToEducationOrganizationId", StringComparison.Ordinal),
                "ownership executes last among the AND strategies and ahead of the relationship OR group"
            );
    }

    [Test]
    public async Task It_returns_each_owned_root_once_when_the_root_carries_child_rows()
    {
        // Row 2 carries two class-period children. The page must still list it once, and the count must
        // count roots, not child rows.
        var result = await _context.QueryAsync(
            ProjectEndpointName,
            RootChildResourceName,
            [ClaimEducationOrganizationId],
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.OwnershipBased,
            ],
            ownershipTokenIds: [OwnerToken]
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        AssertReturnedRootChildren(success, 1, 2, 7);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .OnlyHaveUniqueItems();
        success.TotalCount.Should().Be(3);
    }

    [Test]
    public async Task It_intersects_the_ownership_filter_with_the_namespace_filter()
    {
        // Seed 1 is owned and matches the prefix; seed 2 matches the prefix but carries another client's
        // token; seed 3 is owned but does not match the prefix. Only the intersection survives.
        var result = await _context.QueryAsync(
            ProjectEndpointName,
            NamespaceResourceName,
            [],
            [
                AuthorizationStrategyNameConstants.NamespaceBased,
                AuthorizationStrategyNameConstants.OwnershipBased,
            ],
            namespacePrefixes: [AuthorizedPrefix],
            ownershipTokenIds: [OwnerToken]
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_namespaceSeeds[0].Seed.DocumentUuid.Value.ToString());
        success.TotalCount.Should().Be(1);

        var pageSql = _context.AssertSingleQueryHydration().Plan.PageDocumentIdSql;
        pageSql
            .IndexOf("LIKE @namespacePrefixes_0", StringComparison.Ordinal)
            .Should()
            .BeLessThan(pageSql.IndexOf(OwnershipPredicatePrefixSql, StringComparison.Ordinal));
    }

    [Test]
    public async Task It_intersects_the_ownership_filter_with_a_custom_view()
    {
        // The view authorizes schools 100 and 200; only school 100 is stamped with the caller's token.
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [],
            [CustomViewStrategyName, AuthorizationStrategyNameConstants.OwnershipBased],
            ownershipTokenIds: [OwnerToken]
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_schoolSeeds[0].Seed.DocumentUuid.Value.ToString());
        success.TotalCount.Should().Be(1);

        var pageSql = _context.AssertSingleQueryHydration().Plan.PageDocumentIdSql;
        pageSql
            .IndexOf($"[auth].[{CustomViewStrategyName}]", StringComparison.Ordinal)
            .Should()
            .BeLessThan(pageSql.IndexOf(OwnershipPredicatePrefixSql, StringComparison.Ordinal));
    }

    [Test]
    public async Task It_fails_closed_at_the_ownership_token_cap_without_running_the_page()
    {
        var result = await QueryRootChildrenAsync(ownershipTokenIds: Tokens(OwnershipTokenLimit));

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;

        failure
            .Errors.Should()
            .Equal(OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(OwnershipTokenLimit));
        _context.AssertNoHydration();
    }

    /// <summary>
    /// 1,999 is the largest configuration CMS permits and binds 1,999 scalar parameters here. The list carries
    /// both seeded tokens, so every stamped row is served and only the null stamp is excluded — which also
    /// proves the parameterized IN list executes against the real engine.
    /// </summary>
    [Test]
    public async Task It_binds_one_token_below_the_cap_as_scalar_parameters_and_serves_every_stamped_row()
    {
        var result = await QueryRootChildrenAsync(ownershipTokenIds: Tokens(OwnershipTokenLimit - 1));

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        AssertReturnedRootChildren(success, 1, 2, 3, 5, 6, 7);
        success.TotalCount.Should().Be(6);

        var pageSql = _context.AssertSingleQueryHydration().Plan.PageDocumentIdSql;
        pageSql.Should().Contain($"@ownershipTokenIds_{OwnershipTokenLimit - 2})");
        pageSql.Should().NotContain($"@ownershipTokenIds_{OwnershipTokenLimit - 1}");
    }

    /// <summary>
    /// Each list is within its own cap, but 1,999 token scalars plus 200 prefix scalars plus paging exceed the
    /// 2,098 parameters one SQL Server command can bind. The budget must refuse the page as a
    /// security-configuration failure before the engine rejects the command.
    /// </summary>
    [Test]
    public async Task It_fails_closed_when_the_token_list_and_prefix_list_together_exceed_the_command_parameter_budget()
    {
        var result = await _context.QueryAsync(
            ProjectEndpointName,
            NamespaceResourceName,
            [],
            [
                AuthorizationStrategyNameConstants.NamespaceBased,
                AuthorizationStrategyNameConstants.OwnershipBased,
            ],
            namespacePrefixes:
            [
                AuthorizedPrefix,
                .. Enumerable.Range(1, 199).Select(static i => $"uri://filler-{i}.org/"),
            ],
            ownershipTokenIds: Tokens(OwnershipTokenLimit - 1)
        );

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;

        failure
            .Errors.Should()
            .Equal(
                NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                    namespacePrefixCount: 200,
                    claimEducationOrganizationIdCount: 0,
                    ownershipTokenCount: OwnershipTokenLimit - 1,
                    nonAuthorizationParameterCount: AuthorizationParameterBudget.PaginationParameterCount
                )
            );
        _context.AssertNoHydration();
    }

    private Task<QueryResult> QueryRootChildrenAsync(
        IReadOnlyList<short> ownershipTokenIds,
        int? limit = null,
        int? offset = null
    ) =>
        _context.QueryAsync(
            ProjectEndpointName,
            RootChildResourceName,
            [],
            _ownershipStrategy,
            limit: limit,
            offset: offset,
            ownershipTokenIds: ownershipTokenIds
        );

    private static void AssertReturnedRootChildren(
        QueryResult.QuerySuccess success,
        params int[] expectedAuthorizationRootChildIds
    ) =>
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                expectedAuthorizationRootChildIds.Select(static id =>
                    _rootChildSeeds
                        .Single(seed => seed.Seed.AuthorizationRootChildId == id)
                        .Seed.DocumentUuid.Value.ToString()
                )
            );

    private static IReadOnlyList<short> Tokens(int count) =>
        [.. Enumerable.Range(1, count).Select(static value => (short)value)];

    private static DocumentUuid Uuid(string prefix, int ordinal) =>
        new(Guid.Parse($"{prefix}-0000-0000-0000-{ordinal:D12}"));

    private static AuthorizationRootChildSeed RootChild(
        int authorizationRootChildId,
        string name,
        int schoolId,
        IReadOnlyList<ClassPeriodReferenceSeed> classPeriods
    ) =>
        new(
            Uuid("c3c3c3c3", authorizationRootChildId),
            authorizationRootChildId,
            name,
            schoolId,
            classPeriods
        );

    private static AuthorizationNamespaceSeed NamespaceSeed(
        int authorizationNamespaceId,
        string name,
        string @namespace
    ) =>
        new(
            Uuid("c4c4c4c4", authorizationNamespaceId),
            authorizationNamespaceId,
            name,
            @namespace,
            AuthorizedSchoolId,
            []
        );

    private sealed record StampedSchoolSeed(QuerySchoolSeed Seed, short? CreatorOwnershipTokenId);

    private sealed record StampedRootChildSeed(
        AuthorizationRootChildSeed Seed,
        short? CreatorOwnershipTokenId
    );

    private sealed record StampedNamespaceSeed(
        AuthorizationNamespaceSeed Seed,
        short? CreatorOwnershipTokenId
    );
}

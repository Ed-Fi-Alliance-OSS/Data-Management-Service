// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Real-SQL-Server coverage for NamespaceBased read authorization (DMS-1286). Exercises the production MSSQL
/// backend against the synthetic <c>AuthorizationNamespaceResource</c>, whose root table carries a nullable
/// <c>Namespace</c> securable column plus a root EdOrg securable column, so GET-many filtering, GET-by-id
/// denial, provider-exception decoding, prefix/parameter limits, and AND/OR composition all run against
/// genuinely emitted SQL rather than a compiler fixture.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalNamespace")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Relational_Namespace_Read_Authorization_With_A_Synthetic_Namespace_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string ProjectEndpointName = RelationshipAuthorizationCrudTestSupport.ProjectEndpointName;
    private const string ResourceName = RelationshipAuthorizationCrudTestSupport.NamespaceResourceName;
    private const string AuthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix;
    private const string SecondAuthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.SecondAuthorizedNamespacePrefix;
    private const string UnauthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.UnauthorizedNamespacePrefix;
    private const int AuthorizedSchoolId = (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId;
    private const int UnauthorizedSchoolId = (int)
        RelationshipAuthorizationCrudTestSupport.UnauthorizedSchoolId;

    /// <summary>
    /// A school reachable only through the inverted relationship branch, so a row referencing it satisfies the
    /// OR group without the normal branch.
    /// </summary>
    private const int InvertedOnlySchoolId = (int)
        RelationshipAuthorizationCrudTestSupport.SecondAuthorizedSchoolId;

    /// <summary>The engine error number SQL Server raises for the compiler's intentional AUTH1 cast failure.</summary>
    private const int ConversionFailedErrorNumber = 245;

    private static readonly IReadOnlyList<string> _configuredPrefixes =
        RelationshipAuthorizationCrudTestSupport.ConfiguredNamespacePrefixes;
    private static readonly IReadOnlyList<string> _namespaceStrategy =
        RelationshipAuthorizationCrudTestSupport.NamespaceBasedStrategyNames;
    private static readonly IReadOnlyList<string> _namespaceAndEdOrgStrategies =
        RelationshipAuthorizationCrudTestSupport.NamespaceBasedPlusEdOrgOnlyStrategyNames;
    private static readonly IReadOnlyList<string> _namespaceAndRelationshipOrGroupStrategies =
        RelationshipAuthorizationCrudTestSupport.NamespaceBasedPlusEdOrgNormalOrInvertedStrategyNames;
    private static readonly IReadOnlyList<string> _namespaceAndEdOrgInvertedStrategies =
    [
        RelationshipAuthorizationCrudTestSupport.NamespaceBased,
        RelationshipAuthorizationCrudTestSupport.RelationshipsWithEdOrgsOnlyInverted,
    ];

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001")),
            AuthorizedSchoolId,
            "North"
        ),
        new(
            new DocumentUuid(Guid.Parse("a1a1a1a1-0000-0000-0000-000000000002")),
            UnauthorizedSchoolId,
            "West"
        ),
        // Reached only by the inverted branch: the seeded edge runs school 200 -> claim, never claim -> 200.
        new(
            new DocumentUuid(Guid.Parse("a1a1a1a1-0000-0000-0000-000000000003")),
            InvertedOnlySchoolId,
            "South"
        ),
    ];

    private static readonly ClassPeriodSeed _classPeriodSeed = new(
        new DocumentUuid(Guid.Parse("a2a2a2a2-0000-0000-0000-000000000001")),
        AuthorizedSchoolId,
        "P1"
    );

    // Seeded in this order, so dms.Document identity order — the GET-many sort order — matches the array
    // order. Authorized-by-namespace rows are indexes 0, 2, and 5.
    //
    // The school each row references decides which relationship branch can satisfy it, which is what makes the
    // composed truth table below reachable:
    //   school 100 — normal branch only   (edge claim -> 100)
    //   school 200 — inverted branch only (edge 200 -> claim)
    //   school 300 — neither branch       (no edge in either direction)
    private static readonly AuthorizationNamespaceSeed[] _seeds =
    [
        NamespaceSeed(1, "first-matching-prefix", AuthorizedPrefix + "assessments", AuthorizedSchoolId),
        NamespaceSeed(2, "second-nonmatching-prefix", UnauthorizedPrefix + "assessments", AuthorizedSchoolId),
        NamespaceSeed(3, "third-second-prefix", SecondAuthorizedPrefix + "surveys", InvertedOnlySchoolId),
        NamespaceSeed(4, "fourth-null-namespace", null, AuthorizedSchoolId),
        NamespaceSeed(5, "fifth-empty-namespace", string.Empty, AuthorizedSchoolId),
        NamespaceSeed(
            6,
            "sixth-matching-unauthorized-edorg",
            AuthorizedPrefix + "surveys",
            UnauthorizedSchoolId
        ),
        NamespaceSeed(7, "seventh-stale-target", UnauthorizedPrefix + "stale", AuthorizedSchoolId),
        NamespaceSeed(
            8,
            "eighth-nonmatching-inverted-edorg",
            UnauthorizedPrefix + "surveys",
            InvertedOnlySchoolId
        ),
    ];

    private static readonly AuthorizationNamespaceSeed _matchingSeed = _seeds[0];
    private static readonly AuthorizationNamespaceSeed _mismatchingSeed = _seeds[1];
    private static readonly AuthorizationNamespaceSeed _nullNamespaceSeed = _seeds[3];
    private static readonly AuthorizationNamespaceSeed _emptyNamespaceSeed = _seeds[4];
    private static readonly AuthorizationNamespaceSeed _staleTargetSeed = _seeds[6];

    private readonly List<DbException> _observedProviderFailures = [];

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

        // The observer only records the exception the production path already raised; the extractor still
        // returns the default extraction, so no other test in this fixture changes behavior.
        _context = new MssqlRelationalQueryAuthorizationTestContext(
            providerFailureObserver: _observedProviderFailures.Add
        );
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false,
            replaceReadTargetLookup: false,
            interceptReadTargetLookup: true
        );
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateSchoolAsync(schoolSeed)
            );
        }

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateClassPeriodAsync(_classPeriodSeed)
        );

        foreach (var seed in _seeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateAuthorizationNamespaceAsync(seed)
            );
        }

        // One edge per branch, in one direction only, so the normal and inverted strategies are independently
        // satisfiable and no row can satisfy both. The reverse edges are deleted explicitly because a row that
        // satisfied both branches would make the OR group indistinguishable from a single strategy.
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
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
        _observedProviderFailures.Clear();
    }

    [Test]
    public async Task It_includes_matching_prefixes_and_excludes_nonmatching_null_and_empty_namespaces()
    {
        var result = await QueryAsync();

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        // Both configured prefixes contribute (seeds 1 and 6 match the first, seed 3 the second), while the
        // nonmatching, null, and empty stored namespaces are filtered out of the page and the total count.
        AssertReturnedDocuments(success, _seeds[0], _seeds[2], _seeds[5]);
        success.TotalCount.Should().Be(3, "totalCount must count only namespace-authorized rows");
    }

    [Test]
    public async Task It_filters_before_paging_so_the_page_window_lands_on_authorized_rows()
    {
        // Authorized rows in document order are seeds 1, 3, and 6. Skipping one authorized row yields seeds 3
        // and 6. Paging before filtering would instead window rows 2 and 3 of the unfiltered set and return
        // only seed 3, so this window fails unless authorization runs first.
        var result = await QueryAsync(limit: 2, offset: 1);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        AssertReturnedDocuments(success, _seeds[2], _seeds[5]);
        success.TotalCount.Should().Be(3);
    }

    [Test]
    public async Task It_authorizes_get_by_id_for_a_matching_prefix()
    {
        var result = await GetByIdAsync(_matchingSeed);

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(_matchingSeed.DocumentUuid);
        success.EdfiDoc["namespace"]!.GetValue<string>().Should().Be(_matchingSeed.Namespace);
        _context.AssertSingleDocumentHydration();
        _context.AssertSingleDocumentMaterialized();
    }

    [Test]
    public async Task It_returns_a_typed_mismatch_denial_from_a_real_sql_exception_without_hydrating()
    {
        var result = await GetByIdAsync(_mismatchingSeed);

        AssertStoredNamespaceDenial(result, NamespaceAuthorizationFailureKind.NamespaceMismatch);
        _context.AssertNoHydration();

        // Provenance: the denial was decoded from an actual Microsoft.Data.SqlClient.SqlException raised by
        // the production compiler's intentional AUTH1 cast, not from a stubbed DbException.
        AssertDecodedFromRealSqlException("AUTH1 - ns1|0|m");
    }

    [TestCase(null, TestName = "It_returns_a_typed_stored_uninitialized_denial_for_a_null_namespace")]
    [TestCase("", TestName = "It_returns_a_typed_stored_uninitialized_denial_for_an_empty_namespace")]
    public async Task It_returns_a_typed_stored_uninitialized_denial(string? storedNamespace)
    {
        var seed = storedNamespace is null ? _nullNamespaceSeed : _emptyNamespaceSeed;

        var result = await GetByIdAsync(seed);

        AssertStoredNamespaceDenial(result, NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized);
        _context.AssertNoHydration();
        AssertDecodedFromRealSqlException("AUTH1 - ns1|0|u");
    }

    [Test]
    public async Task It_returns_not_found_when_the_target_is_deleted_between_lookup_and_authorization()
    {
        // The production target lookup resolves the row, then the row is deleted before the stored namespace
        // check executes. The check's NOT EXISTS branch raises the stale AUTH1 kind, the read boundary
        // re-resolves the target, and the vanished row surfaces as 404 rather than a namespace denial.
        _context.AfterNextTargetLookup(async _ => await DeleteNamespaceRowAsync(_staleTargetSeed));

        var result = await GetByIdAsync(_staleTargetSeed);

        result
            .Should()
            .BeOfType<GetResult.GetFailureNotExists>(
                "a target that vanished between lookup and authorization must follow the stale-target path"
            );

        // The 404 must come from decoding the stale-target payload, not from an early re-query that never ran
        // the authorization check: an implementation that skipped the check would record no provider exception.
        AssertDecodedFromRealSqlException("AUTH1 - ns1|0|s");
    }

    [Test]
    public async Task It_executes_a_supported_prefix_count_with_expanded_scalar_parameters()
    {
        var prefixes = CreateUniquePrefixes(
            NamespacePrefixLimitExceededException.MssqlScalarParameterLimit - 1
        );

        var result = await QueryAsync(namespacePrefixes: prefixes);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        AssertReturnedDocuments(success, _seeds[0], _seeds[5]);
        success.TotalCount.Should().Be(2);

        var keyset = _context.AssertSingleQueryHydration();
        var pageFilterParameters = keyset
            .Plan.PageParametersInOrder.Where(static parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
            )
            .ToArray();

        pageFilterParameters.Should().HaveCount(prefixes.Count);
        pageFilterParameters[0].ParameterName.Should().Be("namespacePrefixes_0");
        pageFilterParameters[^1].ParameterName.Should().Be($"namespacePrefixes_{prefixes.Count - 1}");
        pageFilterParameters
            .Select(static parameter => parameter.Binding.Kind)
            .Should()
            .OnlyContain(static kind => kind == QuerySqlParameterBindingKind.Scalar);
        keyset.ParameterValues.Values.Should().Contain(AuthorizedPrefix + "%");
    }

    [Test]
    public async Task It_returns_security_configuration_at_the_sql_server_prefix_cap()
    {
        var prefixes = CreateUniquePrefixes(NamespacePrefixLimitExceededException.MssqlScalarParameterLimit);

        var result = await QueryAsync(namespacePrefixes: prefixes);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(NamespaceAuthorizationSecurityConfigurationMessages.PrefixCapExceeded(prefixes.Count));
        failure
            .Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be("NamespaceAuthorization.PrefixCapExceeded");
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_security_configuration_when_composed_authorization_parameters_exceed_the_command_limit()
    {
        // Each list is inside its own per-list cap, but composing them with the paging parameters exceeds
        // SQL Server's per-command parameter ceiling, so the query must fail closed before executing.
        const int PrefixCount = 1500;
        const int ClaimCount = 599;
        var prefixes = CreateUniquePrefixes(PrefixCount);
        long[] claimEducationOrganizationIds =
        [
            .. Enumerable.Range(0, ClaimCount).Select(static index => 100000L + index),
        ];

        var result = await QueryAsync(
            claimEducationOrganizationIds: claimEducationOrganizationIds,
            strategyNames: _namespaceAndEdOrgStrategies,
            namespacePrefixes: prefixes
        );

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                    namespacePrefixCount: PrefixCount,
                    claimEducationOrganizationIdCount: ClaimCount,
                    nonAuthorizationParameterCount: AuthorizationParameterBudget.PaginationParameterCount
                )
            );
        var diagnostic = failure.Diagnostics.Should().ContainSingle().Subject;
        diagnostic
            .ProviderOrPlannerFailureKind.Should()
            .Be("AuthorizationParameterBudget.CommandParameterCapExceeded");
        diagnostic.ResourceFullName.Should().Be($"Authz.{ResourceName}");
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_ands_namespace_authorization_around_the_relationship_or_group()
    {
        // Namespace AND (normal OR inverted), with both relationship branches independently satisfiable:
        //
        //   seed | namespace | normal | inverted | expected
        //   -----+-----------+--------+----------+---------
        //      1 | match     | true   | false    | included
        //      3 | match     | false  | true     | included
        //      6 | match     | false  | false    | excluded
        //      2 | mismatch  | true   | false    | excluded
        //      8 | mismatch  | false  | true     | excluded
        //
        // Seeds 1 and 3 prove each relationship branch alone satisfies the OR group. Seed 6 proves the OR group
        // is still required. Seeds 2 and 8 falsify the two incorrect flattenings: under
        // (Namespace AND R1) OR R2 seed 8 would be included, and under R1 OR (Namespace AND R2) seed 2 would be.
        //
        // The normal and inverted columns are established rather than assumed: composing the namespace check
        // with one branch at a time must return that branch's row alone.
        var normalOnlyResult = await QueryAsync(
            claimEducationOrganizationIds: [ClaimEducationOrganizationId],
            strategyNames: _namespaceAndEdOrgStrategies
        );
        AssertReturnedDocuments(
            normalOnlyResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject,
            _seeds[0]
        );

        var invertedOnlyResult = await QueryAsync(
            claimEducationOrganizationIds: [ClaimEducationOrganizationId],
            strategyNames: _namespaceAndEdOrgInvertedStrategies
        );
        AssertReturnedDocuments(
            invertedOnlyResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject,
            _seeds[2]
        );

        var result = await QueryAsync(
            claimEducationOrganizationIds: [ClaimEducationOrganizationId],
            strategyNames: _namespaceAndRelationshipOrGroupStrategies
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        AssertReturnedDocuments(success, _seeds[0], _seeds[2]);
        success.TotalCount.Should().Be(2);
    }

    private static AuthorizationNamespaceSeed NamespaceSeed(
        int authorizationNamespaceId,
        string name,
        string? @namespace,
        int schoolId
    ) =>
        new(
            new DocumentUuid(Guid.Parse($"a3a3a3a3-0000-0000-0000-{authorizationNamespaceId:D12}")),
            authorizationNamespaceId,
            name,
            @namespace,
            schoolId,
            authorizationNamespaceId == 1 ? [new ClassPeriodReferenceSeed("P1", AuthorizedSchoolId)] : []
        );

    /// <summary>
    /// The authorized prefix plus filler prefixes, ordinal-distinct, so a test can drive an exact configured
    /// prefix count while still matching the seeded rows.
    /// </summary>
    private static IReadOnlyList<string> CreateUniquePrefixes(int prefixCount) =>
        [
            AuthorizedPrefix,
            .. Enumerable
                .Range(0, prefixCount - 1)
                .Select(static index => $"uri://filler{index:D5}.example/"),
        ];

    private async Task<QueryResult> QueryAsync(
        int? limit = null,
        int? offset = null,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        IReadOnlyList<string>? strategyNames = null,
        IReadOnlyList<string>? namespacePrefixes = null
    ) =>
        await _context.QueryAsync(
            ProjectEndpointName,
            ResourceName,
            claimEducationOrganizationIds ?? [],
            strategyNames ?? _namespaceStrategy,
            limit: limit,
            offset: offset,
            namespacePrefixes: namespacePrefixes ?? _configuredPrefixes
        );

    private async Task<GetResult> GetByIdAsync(AuthorizationNamespaceSeed seed) =>
        await _context.GetByIdAsync(
            ProjectEndpointName,
            ResourceName,
            seed.DocumentUuid,
            [],
            _namespaceStrategy,
            namespacePrefixes: _configuredPrefixes
        );

    private static void AssertReturnedDocuments(
        QueryResult.QuerySuccess success,
        params AuthorizationNamespaceSeed[] expectedSeeds
    ) =>
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(expectedSeeds.Select(static seed => seed.DocumentUuid.Value.ToString()));

    /// <summary>
    /// Asserts the typed outcome was decoded from one genuine <see cref="SqlException"/> carrying
    /// <paramref name="expectedPayload"/>. The executor probes the extractor from more than one exception
    /// filter, so the observer legitimately records the same exception instance repeatedly; requiring exactly
    /// one distinct instance proves a single real engine failure drove the decision.
    /// </summary>
    private void AssertDecodedFromRealSqlException(string expectedPayload)
    {
        _observedProviderFailures
            .Distinct()
            .Should()
            .ContainSingle("every exception filter must probe the one real provider exception");

        var sqlException = _observedProviderFailures[0].Should().BeOfType<SqlException>().Subject;
        sqlException
            .Number.Should()
            .Be(
                ConversionFailedErrorNumber,
                "the payload must arrive through SQL Server's conversion failure, which is how the production "
                    + "compiler raises AUTH1"
            );
        sqlException.Message.Should().Contain(expectedPayload);
    }

    private static void AssertStoredNamespaceDenial(
        GetResult result,
        NamespaceAuthorizationFailureKind expectedFailureKind
    )
    {
        var failure = result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Subject;

        failure.NamespaceFailure.FailureKind.Should().Be(expectedFailureKind);
        failure.NamespaceFailure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Stored);
        failure.NamespaceFailure.EmittedAuth1Index.Should().Be(0);
        failure
            .NamespaceFailure.StrategyName.Should()
            .Be(RelationshipAuthorizationCrudTestSupport.NamespaceBased);
        failure.NamespaceFailure.ConfiguredNamespacePrefixes.Should().Equal(_configuredPrefixes);
    }

    /// <summary>
    /// Removes every authoritative row for a seeded document in dependency order, so the deletion works
    /// whether or not the generated foreign keys cascade.
    /// </summary>
    private async Task DeleteNamespaceRowAsync(AuthorizationNamespaceSeed seed)
    {
        await _context.Database.ExecuteNonQueryAsync(
            """
            DECLARE @documentId bigint = (
                SELECT [DocumentId] FROM [dms].[Document] WHERE [DocumentUuid] = @documentUuid
            );

            DELETE FROM [authz].[AuthorizationNamespaceResourceClassPeriod]
            WHERE [AuthorizationNamespaceResource_DocumentId] = @documentId;

            DELETE FROM [authz].[AuthorizationNamespaceResource] WHERE [DocumentId] = @documentId;

            DELETE FROM [dms].[ReferentialIdentity] WHERE [DocumentId] = @documentId;

            DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentUuid", seed.DocumentUuid.Value)
        );
    }
}

/// <summary>
/// Real-SQL-Server coverage for the namespace provider-failure mapper's fail-closed path. The request first
/// raises a genuine <c>SqlException</c> through the production authorization SQL; only then does the test-only
/// extractor seam rewrite the extracted payload to an unmappable <c>ns1|…</c> value, so the malformed-payload
/// mapping is exercised without altering production SQL.
/// </summary>
/// <remarks>
/// This fixture asserts the structured <c>Diagnostics</c> the wire response deliberately withholds; the
/// sanitized ProblemDetails envelope itself is covered at the API integration boundary.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalNamespace")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Relational_Namespace_Authorization_With_An_Unmappable_Auth1_Payload
{
    private const string ProjectEndpointName = RelationshipAuthorizationCrudTestSupport.ProjectEndpointName;
    private const string ResourceName = RelationshipAuthorizationCrudTestSupport.NamespaceResourceName;
    private const int AuthorizedSchoolId = (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId;

    /// <summary>
    /// A namespace payload whose emitted index exceeds the single planned stored check, so it decodes as a
    /// namespace payload but cannot be mapped onto the plan.
    /// </summary>
    private const string UnmappableProviderMessage =
        "Conversion failed when converting the varchar value 'AUTH1 - ns1|9|m' to data type int.";

    private static readonly QuerySchoolSeed _schoolSeed = new(
        new DocumentUuid(Guid.Parse("a4a4a4a4-0000-0000-0000-000000000001")),
        AuthorizedSchoolId,
        "North"
    );

    private static readonly AuthorizationNamespaceSeed _seed = new(
        new DocumentUuid(Guid.Parse("a5a5a5a5-0000-0000-0000-000000000001")),
        901,
        "unmappable-auth1-payload",
        RelationshipAuthorizationCrudTestSupport.UnauthorizedNamespacePrefix + "assessments",
        AuthorizedSchoolId,
        []
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

        _context = new MssqlRelationalQueryAuthorizationTestContext(providerFailure =>
            providerFailure with
            {
                Message = UnmappableProviderMessage,
            }
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
            await _context.CreateAuthorizationNamespaceAsync(_seed)
        );
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
    public async Task It_fails_closed_with_sanitized_security_configuration_diagnostics()
    {
        var result = await _context.GetByIdAsync(
            ProjectEndpointName,
            ResourceName,
            _seed.DocumentUuid,
            [],
            RelationshipAuthorizationCrudTestSupport.NamespaceBasedStrategyNames,
            namespacePrefixes: RelationshipAuthorizationCrudTestSupport.ConfiguredNamespacePrefixes
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(NamespaceAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata);
        failure
            .Errors.Should()
            .NotContain(error =>
                error.Contains("AUTH1", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Conversion failed", StringComparison.OrdinalIgnoreCase)
                || error.Contains("varchar", StringComparison.OrdinalIgnoreCase)
            );

        var diagnostic = failure.Diagnostics.Should().ContainSingle().Subject;
        diagnostic
            .ProviderOrPlannerFailureKind.Should()
            .Be("NamespaceAuthorization.Auth1.PayloadMappingFailed");
        diagnostic
            .ConfiguredStrategyNames.Should()
            .Equal(RelationshipAuthorizationCrudTestSupport.NamespaceBased);
        diagnostic.PhysicalPath.Should().Be($"authz.{ResourceName}.Namespace");
        _context.AssertNoHydration();
    }
}

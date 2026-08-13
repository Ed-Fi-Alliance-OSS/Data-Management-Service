// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// AC2. Proves the anchored authorization predicate returns the same rows as the primary-key self-join it
/// replaces, by executing both shapes over the generated volume and comparing results.
/// </summary>
/// <remarks>
/// <para>
/// The captured <c>PageKeysetSpec.Query</c> exposes SQL text and parameter values but not the authorization
/// spec, so the pre-change SQL cannot be recovered from a pipeline run. Both shapes are therefore compiled from
/// a spec this test owns.
/// </para>
/// <para>
/// Three assertions in a deliberate order. First, the test-owned emitter's Anchored mode is proved to return
/// what production's <c>Compile()</c> returns — without that, the differential would be measuring the test's own
/// wrapper rather than the product. Then the whole-population sweep compares Anchored against Legacy over every
/// generated row, which is the row-set equivalence proof. The paged offsets come last and claim less: sampled
/// pages plus an equal totalCount cannot detect a membership or ordering difference at an offset never read,
/// since equal counts are satisfied by any permutation or by compensating swaps.
/// </para>
/// <para>
/// Equal result rows say nothing about plans or runtime, so nothing here licenses timing the emitter. The
/// measurement fixtures take production's own SQL as their subject.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("RelationshipAuthorizationVolume")]
public class Given_A_Postgresql_Anchored_Authorization_Row_Set_Equivalence
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int PageLimit = 100;

    /// <summary>
    /// A nonempty claim whose authorization subquery returns no rows. An empty claim set short-circuits before
    /// page SQL ever runs, so this is the executable analogue of AC2's empty-authorization requirement.
    /// </summary>
    private const long UnreachableClaimEducationOrganizationId = 999_999L;

    private static readonly long[] _authorizedClaim =
    [
        RelationshipAuthorizationVolumeIdentifiers.ClaimEducationOrganizationId,
    ];

    private static readonly long[] _emptySubqueryClaim = [UnreachableClaimEducationOrganizationId];

    private static readonly RelationshipAuthorizationPredicateShape[] _bothShapes =
    [
        RelationshipAuthorizationPredicateShape.Anchored,
        RelationshipAuthorizationPredicateShape.Legacy,
    ];

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    public static IEnumerable<string> ResourceNames =>
        RelationshipAuthorizationDifferentialSpecs
            .Create(SqlDialect.Pgsql, _authorizedClaim)
            .Select(static spec => spec.ResourceName);

    /// <summary>Bound above the generated population so one page enumerates the entire result set.</summary>
    private static int SweepLimit => RelationshipAuthorizationVolumeCounts.Ci.TotalRowsPerRoot + 1;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: true);
        await PostgresqlRelationalQueryAuthorizationVolumeGenerator.GenerateAsync(
            _context,
            RelationshipAuthorizationVolumeCounts.Ci
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _context.DisposeAsync();
    }

    /// <summary>
    /// Step one: the wrapper is faithful to the product. If this fails, nothing below it means anything.
    /// </summary>
    [TestCaseSource(nameof(ResourceNames))]
    public async Task It_should_return_what_production_returns_when_the_emitter_is_anchored(
        string resourceName
    )
    {
        var spec = SpecFor(resourceName);
        var productionPlan = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(spec.QuerySpec);
        var emitted = Emit(spec, RelationshipAuthorizationPredicateShape.Anchored, _authorizedClaim);

        var productionIds = await SelectDocumentIdsAsync(
            productionPlan.PageDocumentIdSql,
            _authorizedClaim,
            offset: 0,
            limit: SweepLimit
        );
        var emittedIds = await SelectDocumentIdsAsync(
            emitted.PageSql,
            _authorizedClaim,
            offset: 0,
            limit: SweepLimit
        );

        emittedIds
            .Should()
            .Equal(
                productionIds,
                "the differential is only meaningful if its anchored arm is the product's own result set"
            );

        productionPlan.TotalCountSql.Should().NotBeNull();
        (await CountAsync(emitted.TotalCountSql, _authorizedClaim))
            .Should()
            .Be(await CountAsync(productionPlan.TotalCountSql!, _authorizedClaim));
    }

    /// <summary>
    /// Step two, and the actual row-set equivalence proof: one full ordered enumeration per resource, compared
    /// element-wise. Ordered equality subsumes membership and ordering together, so no separate
    /// symmetric-difference pass is needed, and unlike sampled pages it cannot miss a divergence between the
    /// offsets tested.
    /// </summary>
    [TestCaseSource(nameof(ResourceNames))]
    public async Task It_should_enumerate_the_whole_population_identically_in_both_shapes(string resourceName)
    {
        var spec = SpecFor(resourceName);

        var anchoredIds = await SelectPageAsync(
            spec,
            RelationshipAuthorizationPredicateShape.Anchored,
            _authorizedClaim,
            offset: 0,
            limit: SweepLimit
        );
        var legacyIds = await SelectPageAsync(
            spec,
            RelationshipAuthorizationPredicateShape.Legacy,
            _authorizedClaim,
            offset: 0,
            limit: SweepLimit
        );

        anchoredIds
            .Should()
            .HaveCount(
                RelationshipAuthorizationVolumeCounts.Ci.AuthorizedRowsPerRoot,
                "the sweep must enumerate the entire authorized population, not a page of it"
            );
        anchoredIds.Should().Equal(legacyIds);

        var anchoredCount = await CountForShapeAsync(
            spec,
            RelationshipAuthorizationPredicateShape.Anchored,
            _authorizedClaim
        );
        var legacyCount = await CountForShapeAsync(
            spec,
            RelationshipAuthorizationPredicateShape.Legacy,
            _authorizedClaim
        );

        anchoredCount.Should().Be(legacyCount);
    }

    /// <summary>
    /// Step three: paging assertions, and only claiming to be. These exercise LIMIT/OFFSET arithmetic and the
    /// empty-page boundary past the last authorized row, not set equivalence.
    /// </summary>
    [TestCaseSource(nameof(ResourceNames))]
    public async Task It_should_page_identically_in_both_shapes_at_sampled_offsets(string resourceName)
    {
        var authorizedRows = RelationshipAuthorizationVolumeCounts.Ci.AuthorizedRowsPerRoot;
        int[] offsets = [0, authorizedRows / 2, authorizedRows + PageLimit];
        long[][] claims = [_authorizedClaim, _emptySubqueryClaim];

        foreach (var claim in claims)
        {
            var spec = SpecFor(resourceName, claim);

            foreach (var offset in offsets)
            {
                var anchoredIds = await SelectPageAsync(
                    spec,
                    RelationshipAuthorizationPredicateShape.Anchored,
                    claim,
                    offset,
                    PageLimit
                );
                var legacyIds = await SelectPageAsync(
                    spec,
                    RelationshipAuthorizationPredicateShape.Legacy,
                    claim,
                    offset,
                    PageLimit
                );

                anchoredIds
                    .Should()
                    .Equal(
                        legacyIds,
                        $"the page at offset {offset} for {resourceName} must match in both shapes"
                    );
            }

            var anchoredCount = await CountForShapeAsync(
                spec,
                RelationshipAuthorizationPredicateShape.Anchored,
                claim
            );
            var legacyCount = await CountForShapeAsync(
                spec,
                RelationshipAuthorizationPredicateShape.Legacy,
                claim
            );

            anchoredCount.Should().Be(legacyCount);
        }
    }

    /// <summary>
    /// The boundary the paged offsets rest on: a nonempty claim whose authorization subquery matches nothing
    /// yields an empty page and a zero count in both shapes, rather than silently authorizing everything.
    /// </summary>
    [TestCaseSource(nameof(ResourceNames))]
    public async Task It_should_return_no_rows_for_a_nonempty_claim_whose_authorization_subquery_is_empty(
        string resourceName
    )
    {
        var spec = SpecFor(resourceName, _emptySubqueryClaim);

        foreach (var shape in _bothShapes)
        {
            var ids = await SelectPageAsync(spec, shape, _emptySubqueryClaim, offset: 0, limit: PageLimit);

            ids.Should().BeEmpty();
            (await CountForShapeAsync(spec, shape, _emptySubqueryClaim)).Should().Be(0);
        }
    }

    /// <summary>
    /// The differential's non-vacuity control. Equal results across two shapes prove nothing if the emitter
    /// produced the same SQL twice, so this pins the one difference that is supposed to exist: Legacy reopens
    /// the root relation in a primary-key self-join, Anchored reads it once.
    /// </summary>
    [TestCaseSource(nameof(ResourceNames))]
    public void It_should_emit_a_root_relation_self_join_only_in_the_legacy_shape(string resourceName)
    {
        var spec = SpecFor(resourceName);
        var anchored = Emit(spec, RelationshipAuthorizationPredicateShape.Anchored, _authorizedClaim);
        var legacy = Emit(spec, RelationshipAuthorizationPredicateShape.Legacy, _authorizedClaim);

        legacy.PageSql.Should().NotBe(anchored.PageSql);

        var quotedRootRelation = SqlDialectFactory.Create(SqlDialect.Pgsql).QualifyTable(spec.RootTable);

        CountOccurrences(anchored.PageSql, quotedRootRelation).Should().Be(1);
        CountOccurrences(legacy.PageSql, quotedRootRelation).Should().Be(2);
        CountOccurrences(anchored.TotalCountSql, quotedRootRelation).Should().Be(1);
        CountOccurrences(legacy.TotalCountSql, quotedRootRelation).Should().Be(2);
    }

    private static int CountOccurrences(string value, string text) =>
        value.Split(text, StringSplitOptions.None).Length - 1;

    private static RelationshipAuthorizationDifferentialSpec SpecFor(
        string resourceName,
        IReadOnlyList<long>? claimEducationOrganizationIds = null
    ) =>
        RelationshipAuthorizationDifferentialSpecs
            .Create(SqlDialect.Pgsql, claimEducationOrganizationIds ?? _authorizedClaim)
            .Single(spec => spec.ResourceName == resourceName);

    private static RelationshipAuthorizationDifferentialSql Emit(
        RelationshipAuthorizationDifferentialSpec spec,
        RelationshipAuthorizationPredicateShape shape,
        IReadOnlyList<long> claimEducationOrganizationIds
    ) =>
        RelationshipAuthorizationDifferentialSqlEmitter.Emit(
            spec.QuerySpec,
            SqlDialect.Pgsql,
            shape,
            claimEducationOrganizationIds.Count
        );

    private async Task<IReadOnlyList<long>> SelectPageAsync(
        RelationshipAuthorizationDifferentialSpec spec,
        RelationshipAuthorizationPredicateShape shape,
        IReadOnlyList<long> claimEducationOrganizationIds,
        int offset,
        int limit
    ) =>
        await SelectDocumentIdsAsync(
            Emit(spec, shape, claimEducationOrganizationIds).PageSql,
            claimEducationOrganizationIds,
            offset,
            limit
        );

    private async Task<long> CountForShapeAsync(
        RelationshipAuthorizationDifferentialSpec spec,
        RelationshipAuthorizationPredicateShape shape,
        IReadOnlyList<long> claimEducationOrganizationIds
    ) =>
        await CountAsync(
            Emit(spec, shape, claimEducationOrganizationIds).TotalCountSql,
            claimEducationOrganizationIds
        );

    private async Task<IReadOnlyList<long>> SelectDocumentIdsAsync(
        string sql,
        IReadOnlyList<long> claimEducationOrganizationIds,
        int offset,
        int limit
    )
    {
        var rows = await _context.Database.QueryRowsAsync(
            sql,
            ClaimParameter(claimEducationOrganizationIds),
            new NpgsqlParameter("offset", offset),
            new NpgsqlParameter("limit", limit)
        );

        return rows.Select(static row => (long)row["DocumentId"]!).ToArray();
    }

    private async Task<long> CountAsync(string sql, IReadOnlyList<long> claimEducationOrganizationIds) =>
        await _context.Database.ExecuteScalarAsync<long>(sql, ClaimParameter(claimEducationOrganizationIds));

    private static NpgsqlParameter ClaimParameter(IReadOnlyList<long> claimEducationOrganizationIds) =>
        new("ClaimEducationOrganizationIds", claimEducationOrganizationIds.ToArray());
}

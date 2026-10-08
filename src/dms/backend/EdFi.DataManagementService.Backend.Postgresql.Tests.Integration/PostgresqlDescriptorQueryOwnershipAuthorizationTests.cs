// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Live-provider coverage for the <c>OwnershipBased</c> page filter on descriptor GET-many and partitions on
/// PostgreSQL, through the repository and the production descriptor read handler.
/// </summary>
/// <remarks>
/// Ten descriptors are seeded through the production create path under <c>NoFurtherAuthorizationRequired</c>,
/// which stamps the creating client's token — or null — exactly as the page filter later reads it. Six are
/// owned and four carry another client's token or none, interleaved by <c>DocumentId</c>, so a filter applied
/// after pagination instead of before it would change what every page, count and boundary returns.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Descriptor_Query_With_Ownership_Authorization
{
    private const string DescriptorProject = "ed-fi";
    private const string DescriptorResource = "SchoolTypeDescriptor";
    private const string AuthorizedNamespace = "uri://ns1.org/SchoolTypeDescriptor";
    private const string OtherNamespace = "uri://ns2.org/SchoolTypeDescriptor";
    private const string AuthorizedPrefix = "uri://ns1.org/";
    private const string CustomViewStrategyName =
        "SchoolTypeDescriptorWithOwnershipPageCustomViewProviderTest";

    private const short OwnerToken = 42;
    private const short OtherToken = 7;
    private const short UnusedToken = 99;
    private const int OwnershipTokenLimit = OwnershipTokenLimitExceededException.OwnershipTokenLimit;

    private static readonly IReadOnlyList<string> _ownershipStrategy =
    [
        AuthorizationStrategyNameConstants.OwnershipBased,
    ];

    /// <summary>
    /// Seeded in this order, so <c>DocumentId</c> order — the GET-many sort order — is the array order. Owned
    /// rows are 1, 3, 5, 6, 8 and 10; rows 2 and 7 carry another client's token and rows 4 and 9 none. Rows 3
    /// and 7 sit outside the authorized namespace, and the custom view authorizes rows 1, 2 and 3.
    /// </summary>
    private static readonly DescriptorSeed[] _seeds =
    [
        new(1, AuthorizedNamespace, OwnerToken),
        new(2, AuthorizedNamespace, OtherToken),
        new(3, OtherNamespace, OwnerToken),
        new(4, AuthorizedNamespace, null),
        new(5, AuthorizedNamespace, OwnerToken),
        new(6, AuthorizedNamespace, OwnerToken),
        new(7, OtherNamespace, OtherToken),
        new(8, AuthorizedNamespace, OwnerToken),
        new(9, AuthorizedNamespace, null),
        new(10, AuthorizedNamespace, OwnerToken),
    ];

    private static readonly int[] _ownedRows = [1, 3, 5, 6, 8, 10];

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
        await _context.Database.ResetAsync();

        foreach (var seed in _seeds)
        {
            var created = await _context.UpsertWithActionAuthorizationAsync(
                DescriptorProject,
                DescriptorResource,
                Body(seed),
                seed.DocumentUuid,
                UpsertActionAuthorizationTestSupport.NoFurtherAuthorizationRequiredForCreateAndUpdate,
                creatorOwnershipTokenId: seed.StoredToken,
                ownershipTokenIds: []
            );
            created.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        await _context.CreateDescriptorCustomAuthViewAsync(
            CustomViewStrategyName,
            [CodeValue(1), CodeValue(2), CodeValue(3)]
        );
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

    // ── GET-many ─────────────────────────────────────────────────────────

    [Test]
    public async Task It_returns_only_the_descriptors_stamped_with_the_callers_token_and_excludes_null_stamps()
    {
        var success = await QuerySuccessAsync([OwnerToken]);

        ServedIds(success).Should().Equal(Ids(_ownedRows));
        success.TotalCount.Should().Be(6, "totalCount must count only owned descriptors");
    }

    [Test]
    public async Task It_matches_any_of_several_tokens_and_still_excludes_null_stamps()
    {
        var success = await QuerySuccessAsync([OwnerToken, OtherToken]);

        ServedIds(success).Should().Equal(Ids(1, 2, 3, 5, 6, 7, 8, 10));
        success.TotalCount.Should().Be(8);
    }

    /// <summary>
    /// Six owned rows interleaved with four others, five to a page: filtering after pagination would put the
    /// foreign and null rows on page one and short it; filtering first gives five owned rows, then one.
    /// </summary>
    [Test]
    public async Task It_filters_before_paging_so_each_page_and_the_count_are_cut_from_owned_rows()
    {
        var firstPage = await QuerySuccessAsync([OwnerToken], limit: 5, offset: 0);
        var secondPage = await QuerySuccessAsync([OwnerToken], limit: 5, offset: 5);

        ServedIds(firstPage).Should().Equal(Ids(1, 3, 5, 6, 8));
        firstPage.TotalCount.Should().Be(6);
        ServedIds(secondPage).Should().Equal(Ids(10));
        secondPage.TotalCount.Should().Be(6);
    }

    [Test]
    public async Task It_windows_an_offset_and_limit_over_the_owned_rows()
    {
        var success = await QuerySuccessAsync([OwnerToken], limit: 2, offset: 2);

        ServedIds(success).Should().Equal(Ids(5, 6));
        success.TotalCount.Should().Be(6);
    }

    [Test]
    public async Task It_returns_an_empty_page_and_zero_count_for_a_token_nothing_was_stamped_with()
    {
        var success = await QuerySuccessAsync([UnusedToken]);

        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(0);
        success.SelectionSkipped.Should().BeFalse("a non-matching token is a real filter that ran");
    }

    [Test]
    public async Task It_returns_an_empty_page_and_zero_count_without_sql_when_the_caller_has_no_tokens()
    {
        var success = await QuerySuccessAsync([]);

        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(0);
        success.SelectionSkipped.Should().BeTrue();
    }

    [Test]
    public async Task It_intersects_the_ownership_filter_with_the_namespace_filter()
    {
        var result = await _context.QueryAsync(
            DescriptorProject,
            DescriptorResource,
            [],
            [
                AuthorizationStrategyNameConstants.NamespaceBased,
                AuthorizationStrategyNameConstants.OwnershipBased,
            ],
            namespacePrefixes: [AuthorizedPrefix],
            ownershipTokenIds: [OwnerToken]
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        ServedIds(success).Should().Equal(Ids(1, 5, 6, 8, 10));
        success.TotalCount.Should().Be(5);
    }

    [Test]
    public async Task It_intersects_the_ownership_filter_with_a_custom_view()
    {
        var result = await _context.QueryAsync(
            DescriptorProject,
            DescriptorResource,
            [],
            [CustomViewStrategyName, AuthorizationStrategyNameConstants.OwnershipBased],
            ownershipTokenIds: [OwnerToken]
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        ServedIds(success).Should().Equal(Ids(1, 3));
        success.TotalCount.Should().Be(2);
    }

    [Test]
    public async Task It_fails_closed_at_the_ownership_token_cap()
    {
        var result = await QueryAsync(Tokens(OwnershipTokenLimit));

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .Equal(OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(OwnershipTokenLimit));
    }

    /// <summary>
    /// 1,999 is the largest configuration CMS permits, and it carries both seeded tokens, so every stamped
    /// descriptor is served and only the null stamps are excluded.
    /// </summary>
    [Test]
    public async Task It_binds_one_token_below_the_cap_and_serves_every_stamped_descriptor()
    {
        var success = await QuerySuccessAsync(Tokens(OwnershipTokenLimit - 1));

        ServedIds(success).Should().Equal(Ids(1, 2, 3, 5, 6, 7, 8, 10));
        success.TotalCount.Should().Be(8);
    }

    /// <summary>
    /// PostgreSQL binds the token list and the prefix list as one array each, so the composition SQL Server's
    /// parameter budget rejects runs here.
    /// </summary>
    [Test]
    public async Task It_never_reaches_the_command_parameter_budget_with_array_parameters()
    {
        var result = await _context.QueryAsync(
            DescriptorProject,
            DescriptorResource,
            [],
            [
                AuthorizationStrategyNameConstants.NamespaceBased,
                AuthorizationStrategyNameConstants.OwnershipBased,
            ],
            namespacePrefixes:
            [
                AuthorizedPrefix,
                .. Enumerable.Range(1, 99).Select(static index => $"uri://filler-{index}.org/"),
            ],
            ownershipTokenIds: Tokens(OwnershipTokenLimit - 1)
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        ServedIds(success).Should().Equal(Ids(1, 2, 5, 6, 8, 10));
        success.TotalCount.Should().Be(6);
    }

    // ── Cursor pages and partitions ──────────────────────────────────────

    [Test]
    public async Task It_filters_a_cursor_page_to_owned_rows_ahead_of_the_cursor_bounds()
    {
        var success = await QuerySuccessAsync(
            [OwnerToken],
            paging: new CollectionPaging.Cursor(CursorRange.From(1), new PageSize(3))
        );

        ServedIds(success).Should().Equal(Ids(1, 3, 5));
        success.TotalCount.Should().BeNull("cursor paging never counts");
        success.HighestSelectedAnchor.Should().Be(await ReadDocumentIdAsync(5));
    }

    [Test]
    public async Task It_walks_every_owned_descriptor_exactly_once_across_cursor_pages()
    {
        var walked = await WalkOwnedCursorPagesAsync(CursorRange.From(1), pageSize: 2);

        walked.Should().Equal(Ids(_ownedRows));
    }

    [Test]
    public async Task It_cuts_partition_boundaries_only_on_owned_document_ids()
    {
        var ownedDocumentIds = await ReadDocumentIdsAsync(_ownedRows);

        var result = await QueryPartitionsAsync([OwnerToken], requestedPartitionCount: 6);

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;
        success.Ranges.Select(static range => range.InclusiveMinimum).Should().Equal(ownedDocumentIds);
        success.Ranges[^1].InclusiveMaximum.Should().Be(long.MaxValue);
        AssertContiguous(success.Ranges);
        success.SelectionSkipped.Should().BeFalse();
    }

    [Test]
    public async Task It_covers_the_owned_set_exactly_once_when_cursor_pages_walk_the_partitions()
    {
        var partitions = await QueryPartitionsAsync([OwnerToken], requestedPartitionCount: 2);

        var ranges = partitions.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject.Ranges;
        ranges.Should().HaveCount(2);
        AssertContiguous(ranges);

        List<string> walked = [];

        foreach (var range in ranges)
        {
            walked.AddRange(await WalkOwnedCursorPagesAsync(range, pageSize: 2));
        }

        walked.Should().Equal(Ids(_ownedRows));
    }

    [Test]
    public async Task It_returns_no_partitions_without_sql_when_the_caller_has_no_tokens()
    {
        var result = await QueryPartitionsAsync([], requestedPartitionCount: 2);

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;
        success.Ranges.Should().BeEmpty();
        success.SelectionSkipped.Should().BeTrue();
    }

    [Test]
    public async Task It_returns_no_partitions_for_a_token_nothing_was_stamped_with()
    {
        var result = await QueryPartitionsAsync([UnusedToken], requestedPartitionCount: 2);

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;
        success.Ranges.Should().BeEmpty();
        success.SelectionSkipped.Should().BeFalse("the boundary statement ran and matched nothing");
    }

    [Test]
    public async Task It_fails_partitions_closed_at_the_ownership_token_cap()
    {
        var result = await QueryPartitionsAsync(Tokens(OwnershipTokenLimit), requestedPartitionCount: 2);

        result
            .Should()
            .BeOfType<PartitionResult.PartitionFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .Equal(OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(OwnershipTokenLimit));
    }

    // ── Support ──────────────────────────────────────────────────────────

    private Task<QueryResult> QueryAsync(
        IReadOnlyList<short> ownershipTokenIds,
        int? limit = null,
        int? offset = null,
        CollectionPaging? paging = null
    ) =>
        _context.QueryAsync(
            DescriptorProject,
            DescriptorResource,
            [],
            _ownershipStrategy,
            limit: limit,
            offset: offset,
            ownershipTokenIds: ownershipTokenIds,
            paging: paging
        );

    private async Task<QueryResult.QuerySuccess> QuerySuccessAsync(
        IReadOnlyList<short> ownershipTokenIds,
        int? limit = null,
        int? offset = null,
        CollectionPaging? paging = null
    ) =>
        (await QueryAsync(ownershipTokenIds, limit, offset, paging))
            .Should()
            .BeOfType<QueryResult.QuerySuccess>()
            .Subject;

    private Task<PartitionResult> QueryPartitionsAsync(
        IReadOnlyList<short> ownershipTokenIds,
        int requestedPartitionCount
    ) =>
        _context.QueryPartitionsAsync(
            DescriptorProject,
            DescriptorResource,
            [],
            _ownershipStrategy,
            requestedPartitionCount,
            minimumPartitionSize: 1,
            ownershipTokenIds: ownershipTokenIds
        );

    /// <summary>
    /// Walks the owner's cursor pages over <paramref name="range"/> until a page selects nothing, and returns
    /// the served ids in walk order. No cursor page counts.
    /// </summary>
    private async Task<IReadOnlyList<string>> WalkOwnedCursorPagesAsync(CursorRange range, int pageSize)
    {
        List<string> walked = [];
        var current = range;

        // One page per seeded row plus the terminal empty page is the most a correct walk can take.
        for (var page = 0; page <= _seeds.Length + 1; page++)
        {
            var success = await QuerySuccessAsync(
                [OwnerToken],
                paging: new CollectionPaging.Cursor(current, new PageSize(pageSize))
            );

            success.TotalCount.Should().BeNull();
            walked.AddRange(ServedIds(success));

            if (success.HighestSelectedAnchor is not { } highestSelectedDocumentId)
            {
                success.EdfiDocs.Should().BeEmpty();
                return walked;
            }

            current = new CursorRange(highestSelectedDocumentId + 1, current.InclusiveMaximum);
        }

        throw new AssertionException("The cursor walk did not reach a terminal empty page.");
    }

    private async Task<IReadOnlyList<long>> ReadDocumentIdsAsync(IEnumerable<int> rows)
    {
        List<long> documentIds = [];

        foreach (var row in rows)
        {
            documentIds.Add(await ReadDocumentIdAsync(row));
        }

        return documentIds;
    }

    private Task<long> ReadDocumentIdAsync(int row) =>
        _context.Database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", Seed(row).DocumentUuid.Value)
        );

    private static void AssertContiguous(IReadOnlyList<CursorRange> ranges)
    {
        for (var index = 0; index + 1 < ranges.Count; index++)
        {
            ranges[index].InclusiveMaximum.Should().Be(ranges[index + 1].InclusiveMinimum - 1);
        }
    }

    private static IReadOnlyList<string> ServedIds(QueryResult.QuerySuccess success) =>
        [.. success.EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())];

    private static IReadOnlyList<string> Ids(params int[] rows) =>
        [.. rows.Select(static row => Seed(row).DocumentUuid.Value.ToString())];

    private static DescriptorSeed Seed(int row) => _seeds.Single(seed => seed.Row == row);

    private static IReadOnlyList<short> Tokens(int count) =>
        [.. Enumerable.Range(1, count).Select(static value => (short)value)];

    private static string CodeValue(int row) => $"OwnershipPage{row:D2}";

    private static JsonNode Body(DescriptorSeed seed) =>
        new JsonObject
        {
            ["namespace"] = seed.Namespace,
            ["codeValue"] = CodeValue(seed.Row),
            ["shortDescription"] = CodeValue(seed.Row),
        };

    private sealed record DescriptorSeed(int Row, string Namespace, short? StoredToken)
    {
        public DocumentUuid DocumentUuid { get; } = new(Guid.Parse($"d4d4d4d4-0000-0000-0000-{Row:D12}"));
    }
}

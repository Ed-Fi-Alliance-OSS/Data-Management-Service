// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// What a client gets when it actually consumes a partitioned collection: several real tokens, walked
/// either one after another or all at once, together covering every accessible member exactly once and
/// never twice.
///
/// <para>
/// This is the assertion neither a boundary query nor a single page can make. Provider tests can show
/// which identities a boundary statement selects, and page tests can show that one range returns the
/// rows inside it, but only walking every returned range shows that the ranges tile the candidate set:
/// a sizing error that left a gap, an off-by-one that overlapped two ranges, and a token the codec and
/// page selection disagreed about all produce a green single-surface test and fail here.
/// </para>
///
/// <para>
/// Every expected set comes from the seed, never from either surface, so a defect that authorized or
/// selected the same wrong set on both surfaces cannot cancel itself out. Every walk is entered from a
/// token the partitions response handed it, and each partition is walked to a request that both offered
/// no continuation and returned nothing.
/// </para>
///
/// <para>
/// The filtered and change-version-bounded walks repeat the identical query suffix on every page
/// request, which the contract requires because the token stores neither. Their seeds interleave
/// matching and non-matching documents, so a walk that dropped its filter after the first page would
/// pull a non-matching document into the union and fail the equality assertion rather than land in an
/// untouched tail.
/// </para>
/// </summary>
internal static class PartitionWalkCoverageScenario
{
    /// <summary>
    /// The maximum page size the fixtures binding this scenario must configure their host with.
    /// </summary>
    /// <remarks>
    /// The mandatory minimum partition size is <c>MaximumPageSize * 5</c>. At the deployed value of 500
    /// every collection this scenario could seed over HTTP would be a single partition, and every
    /// coverage, disjointness, and parallel-consumption assertion below would hold vacuously over one
    /// range. A page size of two puts the minimum at ten rows, which the seeds clear.
    /// </remarks>
    internal const int HostMaximumPageSize = 2;

    /// <summary>How a shared tiling assertion names this scenario in a failure message.</summary>
    private const string WalkContext = "the partition walk";

    private const string NumberCollisionContext = "the number-collision partition walk";

    /// <summary>
    /// Enough documents that the requested count of three is reachable at a minimum partition size of
    /// ten: three partitions of ten start at candidate rows one, eleven, and twenty-one.
    /// </summary>
    private const int SeededDocumentCount = 25;

    /// <summary>
    /// The count every walk requests. Three is the smallest count that makes disjointness a claim about
    /// more than one boundary.
    /// </summary>
    private const int RequestedPartitionCount = 3;

    /// <summary>
    /// Enough documents on each side of the filter that the matching side still exceeds the minimum
    /// partition size, so a filtered walk crosses a real boundary rather than staying in one range.
    /// </summary>
    private const int FilteredSeedCount = 24;

    /// <summary>
    /// The change-version window seed. Half of it is updated into the window, and that half must still
    /// exceed the minimum partition size so the windowed walk crosses a real boundary.
    /// </summary>
    private const int WindowSeedCount = 24;

    /// <summary>
    /// A short batch written after the window's upper bound is captured. It only has to be non-empty:
    /// its documents carry larger identities, so a walk that dropped the upper bound would find them in
    /// the final unbounded partition.
    /// </summary>
    private const int LaterBatchCount = 5;

    private const string MatchingLabel = "included";
    private const string OtherLabel = "excluded";

    /// <summary>
    /// The label an update writes. It must differ from the label the document was created with,
    /// because an update that changed nothing would be answered as a no-op and would leave the
    /// document's change version where it was.
    /// </summary>
    private const string UpdatedLabel = "updated";

    /// <summary>
    /// The number the extension documents carry when the value itself is not the subject. Any value in
    /// the accepted partition-count range would do; the collision test below is the only place the
    /// value's second meaning matters.
    /// </summary>
    private const int SharedExtensionNumber = 105;

    /// <summary>
    /// The lowest <c>number</c> the collision seed assigns. Each document gets a distinct value from
    /// here upward, so filtering the collection GET on one selects exactly one document — which is what
    /// keeps the assertion inside a page size of <see cref="HostMaximumPageSize"/>.
    /// </summary>
    private const int CollisionNumberBase = 100;

    /// <summary>
    /// The offset into the collision seed whose <c>number</c> both requests supply. It is inside the
    /// accepted partition-count range of 1 to 200, which is what lets one raw value be a filter on one
    /// operation and a count on the other.
    /// </summary>
    private const int CollisionNumberOffset = 5;

    public static Task It_covers_a_regular_resource_collection_sequentially(ApiIntegrationHarness harness) =>
        CoverRegularResourceAsync(harness, "regular-sequential", inParallel: false);

    public static Task It_covers_a_regular_resource_collection_in_parallel(ApiIntegrationHarness harness) =>
        CoverRegularResourceAsync(harness, "regular-parallel", inParallel: true);

    public static Task It_covers_a_descriptor_collection_sequentially(ApiIntegrationHarness harness) =>
        CoverDescriptorsAsync(harness, "descriptor-sequential", inParallel: false);

    public static Task It_covers_a_descriptor_collection_in_parallel(ApiIntegrationHarness harness) =>
        CoverDescriptorsAsync(harness, "descriptor-parallel", inParallel: true);

    public static Task It_covers_an_extension_resource_collection_sequentially(
        ApiIntegrationHarness harness
    ) => CoverExtensionResourceAsync(harness, inParallel: false);

    public static Task It_covers_an_extension_resource_collection_in_parallel(
        ApiIntegrationHarness harness
    ) => CoverExtensionResourceAsync(harness, inParallel: true);

    /// <summary>
    /// A resource filter narrows the candidate set the boundaries are calculated over, and the same
    /// filter must be repeated on every page of every partition.
    /// </summary>
    /// <remarks>
    /// The seed alternates matching and non-matching labels, so the two sides are interleaved by
    /// identity. A walk that applied the filter only to its first request would therefore return
    /// non-matching documents from a later page, and the union equality below would fail with those
    /// documents named.
    /// </remarks>
    public static async Task It_repeats_a_resource_filter_on_every_page_of_every_partition(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            FilteredSeedCount,
            labelFor: index => index % 2 == 0 ? MatchingLabel : OtherLabel,
            numberFor: _ => SharedExtensionNumber
        );

        string[] expectedIds = [.. seeded.Where(item => item.Label == MatchingLabel).Select(item => item.Id)];
        string filter = $"&label={MatchingLabel}";

        await AssertPartitionsTileTheCandidateSetAsync(
            harness,
            CursorContractSupport.ExtensionItemsEndpoint,
            CursorContractSupport.ExtensionItemsPartitionsEndpoint,
            expectedIds,
            filter,
            inParallel: false
        );
    }

    /// <summary>
    /// A live change-version window narrows the candidate set the same way a filter does, and
    /// <em>both</em> of its bounds are repeated on every request.
    /// </summary>
    /// <remarks>
    /// The seed is arranged so each bound is independently load-bearing, which a one-sided window cannot
    /// show. Every document is created first, so all of them sit below the lower bound. An interleaved
    /// half is then updated, which raises only those documents' change versions above it. The upper
    /// bound is captured after those updates, and a further batch is created and updated above it.
    /// <para>
    /// Dropping <c>minChangeVersion</c> would readmit the never-updated half, whose identities are
    /// interleaved with the expected set and therefore fall inside the same partitions. Dropping
    /// <c>maxChangeVersion</c> would admit the later batch. Either failure shows up as a union that is
    /// not equal to the expected set, with the intruding documents named.
    /// </para>
    /// <para>
    /// Both bounds are read from the published change-queries endpoint, so they are values a client
    /// could have obtained for itself, and nothing here depends on timing or on a sleep: the ordering is
    /// established by the sequence of writes alone.
    /// </para>
    /// </remarks>
    public static async Task It_repeats_a_change_version_window_on_every_page_of_every_partition(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            WindowSeedCount,
            labelFor: _ => MatchingLabel,
            numberFor: _ => SharedExtensionNumber
        );

        // Every document was created before this, so the window's lower bound excludes all of them
        // until one is updated.
        long belowWindow = await CursorContractSupport.ReadNewestChangeVersionAsync(harness);

        var expectedItems = seeded.Where((_, index) => index % 2 == 0).ToArray();

        foreach (var item in expectedItems)
        {
            await CursorContractSupport.UpdateExtensionItemLabelAsync(harness, item, UpdatedLabel);
        }

        long windowMaximum = await CursorContractSupport.ReadNewestChangeVersionAsync(harness);

        windowMaximum
            .Should()
            .BeGreaterThan(belowWindow, "updating a document must raise its change version");

        // Written after the upper bound was captured: a walk that dropped maxChangeVersion would find
        // these, both the newly created documents and the newly updated ones.
        var laterItems = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            LaterBatchCount,
            labelFor: _ => MatchingLabel,
            numberFor: _ => SharedExtensionNumber
        );

        laterItems.Should().NotBeEmpty("the later batch is what a dropped upper bound would readmit");

        // One of them is also updated above the upper bound, so the excluded-above set contains a
        // document that arrived there by update as well as documents that arrived there by creation.
        await CursorContractSupport.UpdateExtensionItemLabelAsync(harness, laterItems[0], UpdatedLabel);

        string window =
            $"&minChangeVersion={(belowWindow + 1).ToString(CultureInfo.InvariantCulture)}"
            + $"&maxChangeVersion={windowMaximum.ToString(CultureInfo.InvariantCulture)}";

        await AssertPartitionsTileTheCandidateSetAsync(
            harness,
            CursorContractSupport.ExtensionItemsEndpoint,
            CursorContractSupport.ExtensionItemsPartitionsEndpoint,
            [.. expectedItems.Select(item => item.Id)],
            window,
            inParallel: false
        );
    }

    /// <summary>
    /// One raw query key, two meanings, one on each sibling operation: the collection GET filters on the
    /// extension resource's <c>number</c> field, while <c>/partitions</c> consumes the same key as the
    /// requested partition count and does not filter on it at all.
    /// </summary>
    /// <remarks>
    /// This is the approved intentional ODS difference made executable. ODS 7.3.2 binds one supplied
    /// <c>?number=</c> into both meanings at once, so its partitions request both counts and filters;
    /// DMS removes the partition control from filter matching before the query-field lookup runs and
    /// answers with one meaning. The assertion needs a schema that really declares a query field of that
    /// name, which is why the cursor-partition-contract fixture declares one.
    /// <para>
    /// The two requests use the same value, so nothing about the comparison rests on the number chosen.
    /// The collection GET returns the documents carrying it; the partitions request returns tokens whose
    /// union is the whole collection, which a filtered partition calculation could not produce.
    /// </para>
    /// </remarks>
    public static async Task It_consumes_a_number_query_key_as_a_filter_on_a_collection_and_as_a_count_on_partitions(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        // Every document gets a distinct number, so filtering on one selects exactly one document. That
        // keeps the collection assertion inside the host's small maximum page size while still being an
        // observably narrower answer than the whole collection.
        var seeded = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            SeededDocumentCount,
            labelFor: _ => MatchingLabel,
            numberFor: index => CollisionNumberBase + index
        );

        int collidingNumber = CollisionNumberBase + CollisionNumberOffset;

        var filteredCollection = await CursorContractSupport.ReadPageAsync(
            harness,
            $"{CursorContractSupport.ExtensionItemsEndpoint}?number={collidingNumber.ToString(CultureInfo.InvariantCulture)}"
        );

        filteredCollection
            .DocumentIds.Should()
            .BeEquivalentTo(
                new[] { seeded[CollisionNumberOffset].Id },
                "the collection GET treats number as the resource query field the schema declares"
            );

        var pageTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.ExtensionItemsPartitionsEndpoint}?number={collidingNumber.ToString(CultureInfo.InvariantCulture)}"
        );

        pageTokens
            .Should()
            .HaveCountGreaterThan(
                1,
                "the seed exceeds the minimum partition size, so the count really produced several "
                    + "partitions rather than collapsing into one"
            );

        CursorContractSupport.AssertTokenRangesTileTheIdentitySpace(pageTokens, NumberCollisionContext);

        var walkedIds = await WalkEveryPartitionAsync(
            harness,
            CursorContractSupport.ExtensionItemsEndpoint,
            pageTokens,
            querySuffix: string.Empty,
            inParallel: false
        );

        walkedIds
            .SelectMany(static partition => partition)
            .Should()
            .BeEquivalentTo(
                seeded.Select(item => item.Id),
                "the partitions operation consumed number as its count, so the boundaries cover the "
                    + "whole collection rather than only the documents carrying that value"
            );
    }

    /// <summary>
    /// The partitions endpoint reports the count as unsupported nowhere, but the collection endpoint has
    /// no partition count: a bare <c>number</c> on the collection GET that no query field matches is an
    /// unknown query field, which is what keeps the collision confined to schemas that declare the
    /// field.
    /// </summary>
    /// <remarks>
    /// Asserted on the regular resource, whose schema declares no <c>number</c> query field. Without
    /// this row the collision test above could be read as evidence that <c>number</c> is globally
    /// reserved on collection GETs, which would be the opposite of the recorded behavior.
    /// </remarks>
    public static async Task It_rejects_a_number_query_key_on_a_collection_whose_schema_omits_it(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsEndpoint}?number=5"
        );

        await BadRequestProblemDetails.AssertShellAsync(
            response,
            BadRequestProblemDetails.UnknownQueryField("number")
        );
    }

    private static async Task CoverRegularResourceAsync(
        ApiIntegrationHarness harness,
        string scenario,
        bool inParallel
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await CursorContractSupport.SeedMergeItemsAsync(
            harness,
            scenario,
            SeededDocumentCount
        );

        await AssertPartitionsTileTheCandidateSetAsync(
            harness,
            CursorContractSupport.MergeItemsEndpoint,
            CursorContractSupport.MergeItemsPartitionsEndpoint,
            seededIds,
            querySuffix: string.Empty,
            inParallel
        );
    }

    private static async Task CoverDescriptorsAsync(
        ApiIntegrationHarness harness,
        string scenario,
        bool inParallel
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await CursorContractSupport.SeedDescriptorsAsync(harness, scenario, SeededDocumentCount);

        await AssertPartitionsTileTheCandidateSetAsync(
            harness,
            CursorContractSupport.DescriptorEndpoint,
            CursorContractSupport.DescriptorPartitionsEndpoint,
            seeded.Ids,
            querySuffix: string.Empty,
            inParallel
        );
    }

    private static async Task CoverExtensionResourceAsync(ApiIntegrationHarness harness, bool inParallel)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            SeededDocumentCount,
            labelFor: _ => MatchingLabel,
            numberFor: _ => SharedExtensionNumber
        );

        await AssertPartitionsTileTheCandidateSetAsync(
            harness,
            CursorContractSupport.ExtensionItemsEndpoint,
            CursorContractSupport.ExtensionItemsPartitionsEndpoint,
            [.. seeded.Select(item => item.Id)],
            querySuffix: string.Empty,
            inParallel
        );
    }

    /// <summary>
    /// The shared proof: several real tokens whose walks are pairwise disjoint and whose union is exactly
    /// the expected set.
    /// </summary>
    private static async Task AssertPartitionsTileTheCandidateSetAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        string partitionsEndpoint,
        IReadOnlyCollection<string> expectedIds,
        string querySuffix,
        bool inParallel
    )
    {
        CursorContractSupport.ValidateQuerySuffix(querySuffix);

        var pageTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{partitionsEndpoint}?number={RequestedPartitionCount.ToString(CultureInfo.InvariantCulture)}{querySuffix}"
        );

        pageTokens
            .Should()
            .HaveCountGreaterThan(
                1,
                "the candidate set exceeds the minimum partition size, so consumption really crosses a "
                    + "boundary instead of holding vacuously over one range"
            );
        pageTokens
            .Should()
            .HaveCountLessThanOrEqualTo(
                RequestedPartitionCount,
                "the requested count is an upper bound the response never exceeds"
            );

        CursorContractSupport.AssertTokenRangesTileTheIdentitySpace(pageTokens, WalkContext);

        var walkedPartitions = await WalkEveryPartitionAsync(
            harness,
            collectionEndpoint,
            pageTokens,
            querySuffix,
            inParallel
        );

        CursorContractSupport.AssertPartitionsCoverExactly(walkedPartitions, expectedIds, WalkContext);
    }

    /// <summary>
    /// Walks every partition, either one after another or all at once, and returns each partition's
    /// documents separately so disjointness can be asserted across them.
    /// </summary>
    /// <remarks>
    /// Parallel consumption is the mode a client uses in production, and it is not merely a faster
    /// spelling of the sequential walk: the ranges are consumed concurrently against one host, so a
    /// boundary that depended on request order or on state left behind by an earlier walk would show up
    /// here and nowhere else.
    /// </remarks>
    private static async Task<IReadOnlyList<IReadOnlyList<string>>> WalkEveryPartitionAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        IReadOnlyList<string> pageTokens,
        string querySuffix,
        bool inParallel
    )
    {
        if (inParallel)
        {
            return await Task.WhenAll(
                pageTokens.Select(pageToken =>
                    CursorContractSupport.WalkFromTokenAsync(
                        harness,
                        collectionEndpoint,
                        pageToken,
                        HostMaximumPageSize,
                        querySuffix
                    )
                )
            );
        }

        List<IReadOnlyList<string>> walked = [];

        foreach (string pageToken in pageTokens)
        {
            walked.Add(
                await CursorContractSupport.WalkFromTokenAsync(
                    harness,
                    collectionEndpoint,
                    pageToken,
                    HostMaximumPageSize,
                    querySuffix
                )
            );
        }

        return walked;
    }
}

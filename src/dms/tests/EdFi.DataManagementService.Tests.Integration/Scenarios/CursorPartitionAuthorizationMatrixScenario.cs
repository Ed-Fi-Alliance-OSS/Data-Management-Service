// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.CursorPartitionAuthorizationMatrixSupport;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Proves that a cursor walk and the partition boundaries handed out for the same principal, filters, and
/// seeded data resolve the same accessible candidate set, and that a client cannot widen that set by
/// editing the range inside a token.
///
/// <para>
/// The two surfaces compile the same authorized candidate relation but reach it differently: pages select
/// a bounded range from it, while boundaries rank and count the whole of it. A defect that authorized one
/// and not the other would leave every single-surface test green, so every row here compares the two
/// against an expected set that comes from the seed rather than from either surface.
/// </para>
///
/// <para>
/// The strategies covered are those the production read path actually compiles into the candidate
/// relation. Relationship coverage spans both subject kinds the page and boundary relations can carry,
/// the education-organization subject and the person subject, because those are the two the compiler
/// emits different predicate shapes for; the named strategies within a kind differ upstream of the point
/// the two surfaces share, and are covered where that difference lives. <c>OwnershipBased</c> is not
/// among them for any operation, GET-many included: it is recognized but not enabled, so the request
/// fails closed before a candidate relation exists, and enabling it for read-many is work this matrix
/// does not cover. Descriptor coverage spans the
/// no-further, namespace, and custom-view strategies; relationship strategies are the descriptor
/// exclusion, because descriptors expose no education-organization or person securable elements for one
/// to range over. A descriptor custom view is not excluded: the descriptor read path plans custom-view
/// checks into the same authorization spec for pages and for boundaries, which is exactly the agreement
/// this matrix exists to prove.
/// </para>
/// </summary>
internal static class CursorPartitionAuthorizationMatrixScenario
{
    /// <summary>
    /// The maximum page size the fixtures binding this scenario must configure their host with.
    /// </summary>
    /// <remarks>
    /// The mandatory minimum partition size is <c>MaximumPageSize * 5</c>, so at the deployed value of 500
    /// no collection seeded over HTTP could ever be cut into more than one partition and every union and
    /// disjointness assertion below would pass without crossing a single range boundary. Lowering it to
    /// two puts the minimum at ten candidate rows, which the seeded accessible sets clear.
    /// </remarks>
    internal const int HostMaximumPageSize = 2;

    /// <summary>
    /// Requested so that the seeded accessible sets produce three partitions: the computed size is smaller
    /// than the ten-row minimum, so the minimum decides, and 21 accessible candidates fall into three
    /// ranges.
    /// </summary>
    private const int RequestedPartitionCount = 3;

    /// <summary>
    /// A walk that failed to advance exhausts this and fails with the pages it did retrieve rather than
    /// running forever. Derived from the accessible count so a larger seed does not silently truncate a
    /// walk into a false failure.
    /// </summary>
    private static int MaximumWalkedPages(int accessibleCount) =>
        ((accessibleCount + HostMaximumPageSize - 1) / HostMaximumPageSize) + 2;

    public static async Task It_agrees_on_the_candidate_set_under_no_further_authorization(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedNamespaceResourcesAsync(harness, MatrixAccessibility.All);

        await RunMatrixAsync(harness, NamespaceResourcesEndpoint, seeded);
    }

    public static async Task It_agrees_on_the_candidate_set_under_relationship_authorization(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedNamespaceResourcesAsync(harness, MatrixAccessibility.EducationOrganization);
        await PeopleRelationshipGetManyScenarioHelpers.InsertAuthEdgeAsync(
            harness,
            ClaimEducationOrganizationId,
            AuthorizedSchoolId
        );

        await RunMatrixAsync(harness, NamespaceResourcesEndpoint, seeded);
    }

    public static async Task It_agrees_on_the_candidate_set_under_namespace_authorization(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedNamespaceResourcesAsync(harness, MatrixAccessibility.Namespace);

        await RunMatrixAsync(harness, NamespaceResourcesEndpoint, seeded);
    }

    public static async Task It_agrees_on_the_candidate_set_under_view_based_authorization(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        // Every field another strategy could authorize on is seeded so that it would admit the whole
        // collection: each namespace sits under the caller's authorized prefix, and every reference points
        // at the authorized school. Only the identity column the view selects on, and the name no strategy
        // reads, tell an accessible document from a denied one, so the view is what excludes them.
        var seeded = await SeedNamespaceResourcesAsync(harness, MatrixAccessibility.Identity);
        await CreateMatrixCustomViewAsync(harness);

        await RunMatrixAsync(harness, NamespaceResourcesEndpoint, seeded);
    }

    /// <summary>
    /// The same carrier and strategy as the relationship row, read by a principal holding two education
    /// organization claims that both reach the authorized school. The hierarchy relation the authorization
    /// predicate ranges over therefore holds two matching rows for every accessible candidate, so a plan
    /// that joined to it instead of testing membership would return each document twice, inflate the total
    /// count, and shift every partition boundary.
    /// </summary>
    public static async Task It_agrees_on_the_candidate_set_when_authorization_matches_several_rows(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedNamespaceResourcesAsync(harness, MatrixAccessibility.EducationOrganization);
        await PeopleRelationshipGetManyScenarioHelpers.InsertAuthEdgeAsync(
            harness,
            ClaimEducationOrganizationId,
            AuthorizedSchoolId
        );
        await PeopleRelationshipGetManyScenarioHelpers.InsertAuthEdgeAsync(
            harness,
            SecondClaimEducationOrganizationId,
            AuthorizedSchoolId
        );

        await RunMatrixAsync(harness, NamespaceResourcesEndpoint, seeded);
    }

    /// <summary>
    /// A people strategy over a carrier whose person is reached through a reference to another resource.
    /// The predicate compiled for it is anchored on the root row's reference column rather than on its
    /// DocumentId, and nests a subquery over the referenced table inside the auth view membership test, so
    /// the candidate relation the two surfaces share has a shape no other row in the matrix produces.
    /// </summary>
    public static async Task It_agrees_on_the_candidate_set_under_transitive_person_authorization(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedTransitivePersonResourcesAsync(harness, MatrixAccessibility.Person);
        await PeopleRelationshipGetManyScenarioHelpers.InsertAuthEdgeAsync(
            harness,
            ClaimEducationOrganizationId,
            AuthorizedSchoolId
        );

        await RunMatrixAsync(harness, StudentAcademicRecordResourcesEndpoint, seeded);
    }

    public static async Task It_agrees_on_the_descriptor_candidate_set_under_no_further_authorization(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedAcademicSubjectDescriptorsAsync(harness, MatrixAccessibility.All);

        await RunMatrixAsync(harness, AcademicSubjectDescriptorsEndpoint, seeded);
    }

    public static async Task It_agrees_on_the_descriptor_candidate_set_under_namespace_authorization(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedAcademicSubjectDescriptorsAsync(harness, MatrixAccessibility.Namespace);

        await RunMatrixAsync(harness, AcademicSubjectDescriptorsEndpoint, seeded);
    }

    /// <summary>
    /// The descriptor carrier read through a custom view. The descriptor read path plans custom-view checks
    /// into the authorization spec that both the page relation and the boundary relation are compiled from,
    /// so this is the descriptor row where the two surfaces could disagree for an authorization reason.
    /// The one field a descriptor strategy could authorize on, the namespace, is seeded identically across
    /// the collection; only the code value the view selects on, and the short description mirroring it,
    /// vary.
    /// </summary>
    public static async Task It_agrees_on_the_descriptor_candidate_set_under_view_based_authorization(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedAcademicSubjectDescriptorsAsync(harness, MatrixAccessibility.Identity);
        await CreateDescriptorMatrixCustomViewAsync(harness);

        await RunMatrixAsync(harness, AcademicSubjectDescriptorsEndpoint, seeded);
    }

    /// <summary>
    /// Authorization, not an empty collection, is what leaves nothing to page: the documents are seeded
    /// and then every one of them is filtered out because the caller reaches no education organization.
    /// </summary>
    public static async Task It_returns_no_candidates_when_authorization_admits_none(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedNamespaceResourcesAsync(harness, MatrixAccessibility.None);

        await RunEmptyCandidateSetAsync(harness, NamespaceResourcesEndpoint, seeded);
    }

    public static async Task It_returns_no_descriptor_candidates_when_authorization_admits_none(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedAcademicSubjectDescriptorsAsync(harness, MatrixAccessibility.None);

        await RunEmptyCandidateSetAsync(harness, AcademicSubjectDescriptorsEndpoint, seeded);
    }

    private static async Task RunMatrixAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        SeededMatrix seeded
    )
    {
        // A row whose accessible set could not fill two partitions would satisfy every assertion below
        // without ever crossing a boundary.
        seeded
            .AccessibleIds.Should()
            .HaveCountGreaterThan(
                HostMaximumPageSize * 5,
                "the accessible set must exceed the minimum partition size for the boundaries to be crossed"
            );

        int accessibleCount = seeded.AccessibleIds.Count;
        int maximumWalkedPages = MaximumWalkedPages(accessibleCount);

        // Compiled from the same candidate relation in traditional mode, so this is a cross-check rather
        // than an independent oracle - and it is the most sensitive one available for a candidate relation
        // that started returning a document more than once.
        int totalCount = await ReadTotalCountAsync(harness, collectionEndpoint);
        totalCount
            .Should()
            .Be(accessibleCount, "a total count over the same candidate relation counts each document once");

        var walked = await WalkAsync(
            harness,
            collectionEndpoint,
            PageTokenCodec.Encode(CursorRange.From(1)),
            maximumWalkedPages
        );
        AssertResolvesTheAccessibleSet(walked, seeded, "the cursor walk");

        IReadOnlyList<long> accessibleDocumentIds = await ReadDocumentIdsAsync(harness, seeded.AccessibleIds);
        IReadOnlyList<long> inaccessibleDocumentIds = await ReadDocumentIdsAsync(
            harness,
            seeded.InaccessibleIds
        );

        var pageTokens = await ReadPageTokensAsync(
            harness,
            $"{collectionEndpoint}/partitions?number={RequestedPartitionCount}"
        );

        pageTokens
            .Should()
            .HaveCountGreaterThan(
                1,
                "the accessible set is large enough for several partitions, so the assertions below run "
                    + "across separate ranges rather than inside one"
            );
        pageTokens
            .Should()
            .HaveCountLessThanOrEqualTo(
                RequestedPartitionCount,
                "the requested count is an upper bound the response may not exceed"
            );

        var ranges = DecodeRanges(pageTokens);

        for (var index = 0; index < ranges.Count - 1; index++)
        {
            ranges[index]
                .InclusiveMinimum.Should()
                .BeLessThan(ranges[index + 1].InclusiveMinimum, "partition starts ascend");
            (ranges[index].InclusiveMaximum + 1)
                .Should()
                .Be(
                    ranges[index + 1].InclusiveMinimum,
                    "consecutive partitions abut, so no candidate falls between two of them"
                );
        }

        ranges[^1].InclusiveMaximum.Should().Be(long.MaxValue, "the final partition is unbounded above");

        foreach (CursorRange range in ranges)
        {
            accessibleDocumentIds
                .Should()
                .Contain(
                    range.InclusiveMinimum,
                    "a partition starts at a candidate the caller may actually read"
                );
            inaccessibleDocumentIds
                .Should()
                .NotContain(
                    range.InclusiveMinimum,
                    "a starting identifier taken from an inaccessible row would disclose it"
                );
        }

        List<List<string>> partitionWalks = [];
        foreach (string partitionToken in pageTokens)
        {
            partitionWalks.Add(
                await WalkAsync(harness, collectionEndpoint, partitionToken, maximumWalkedPages)
            );
        }

        for (var first = 0; first < partitionWalks.Count - 1; first++)
        {
            for (int second = first + 1; second < partitionWalks.Count; second++)
            {
                partitionWalks[first]
                    .Should()
                    .NotIntersectWith(
                        partitionWalks[second],
                        "partitions are disjoint, so no document belongs to two of them"
                    );
            }
        }

        List<string> partitionUnion = [.. partitionWalks.SelectMany(static walk => walk)];
        AssertResolvesTheAccessibleSet(partitionUnion, seeded, "the union of the partition walks");
        partitionUnion
            .Should()
            .BeEquivalentTo(walked, "both surfaces resolve the same accessible candidate set");

        await AssertAWidenedRangeStaysInsideTheAccessibleSetAsync(
            harness,
            collectionEndpoint,
            seeded,
            ranges[0],
            maximumWalkedPages
        );

        await AssertMatchNothingRangesAsync(
            harness,
            collectionEndpoint,
            accessibleDocumentIds,
            inaccessibleDocumentIds
        );

        await AssertFiltersApplyToBothSurfacesAsync(harness, collectionEndpoint, seeded);
    }

    /// <summary>
    /// A widened range is not a match-nothing range: it names every identifier the store could hold, and
    /// authorization alone is what keeps the inaccessible documents out of it. One page could therefore
    /// look clean while an interleaved inaccessible document waited on a later page, so the forged token
    /// is walked to its terminal empty response and compared against the whole accessible set.
    /// </summary>
    private static async Task AssertAWidenedRangeStaysInsideTheAccessibleSetAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        SeededMatrix seeded,
        CursorRange firstPartitionRange,
        int maximumWalkedPages
    )
    {
        string widenedToken = PageTokenCodec.Encode(new CursorRange(long.MinValue, long.MaxValue));

        // Widening an emitted partition token produces this exact token, so the walk below is the walk of a
        // widened token the endpoint really handed out rather than of an unrelated one, and a second walk
        // would be the same request twice.
        PageTokenCodec
            .Encode(
                firstPartitionRange with
                {
                    InclusiveMinimum = long.MinValue,
                    InclusiveMaximum = long.MaxValue,
                }
            )
            .Should()
            .Be(widenedToken);

        var widenedWalk = await WalkAsync(harness, collectionEndpoint, widenedToken, maximumWalkedPages);

        AssertResolvesTheAccessibleSet(widenedWalk, seeded, "a maximally widened forged token");
    }

    private static async Task AssertMatchNothingRangesAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        IReadOnlyList<long> accessibleDocumentIds,
        IReadOnlyList<long> inaccessibleDocumentIds
    )
    {
        // Under a strategy that authorizes everything there is no inaccessible row to aim at, so the probe
        // aims past the seed instead. The range still selects nothing, and the disclosure it would expose
        // under a filtering strategy is covered by the rows that have one.
        long probeDocumentId =
            inaccessibleDocumentIds.Count > 0
                ? inaccessibleDocumentIds[0]
                : accessibleDocumentIds.Max() + 1_000_000;

        await AssertSelectsNothingAsync(
            harness,
            collectionEndpoint,
            new CursorRange(probeDocumentId, probeDocumentId),
            "a range narrowed onto a single row the caller may not read"
        );

        await AssertSelectsNothingAsync(
            harness,
            collectionEndpoint,
            new CursorRange(accessibleDocumentIds.Max(), accessibleDocumentIds.Min()),
            "an inverted range"
        );

        await AssertSelectsNothingAsync(
            harness,
            collectionEndpoint,
            new CursorRange(long.MaxValue, long.MaxValue),
            "the highest representable range"
        );

        await AssertSelectsNothingAsync(
            harness,
            collectionEndpoint,
            new CursorRange(long.MinValue, long.MinValue),
            "the lowest representable range"
        );
    }

    /// <summary>
    /// The same filter is applied to the boundary request and to every page request, so a boundary set
    /// calculated over a differently filtered candidate relation than the pages it describes would be
    /// caught. The token count is what detects it: an unfiltered boundary calculation still delivers only
    /// the matching document once each range is filtered, but it does not produce a single boundary.
    /// </summary>
    private static async Task AssertFiltersApplyToBothSurfacesAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        SeededMatrix seeded
    )
    {
        string filteredCollection = $"{collectionEndpoint}?{seeded.FilterQuery}";

        var filteredTokens = await ReadPageTokensAsync(
            harness,
            $"{collectionEndpoint}/partitions?{seeded.FilterQuery}&number={RequestedPartitionCount}"
        );

        filteredTokens
            .Should()
            .HaveCount(
                1,
                "the filter leaves a single candidate, so the boundaries are calculated over one row"
            );

        var filteredPartitionWalk = await WalkAsync(
            harness,
            filteredCollection,
            filteredTokens[0],
            MaximumWalkedPages(1)
        );
        filteredPartitionWalk.Should().Equal(seeded.FilterMatchedId);

        var filteredCursorWalk = await WalkAsync(
            harness,
            filteredCollection,
            PageTokenCodec.Encode(CursorRange.From(1)),
            MaximumWalkedPages(1)
        );
        filteredCursorWalk.Should().Equal(seeded.FilterMatchedId);
    }

    private static async Task RunEmptyCandidateSetAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        SeededMatrix seeded
    )
    {
        seeded
            .AccessibleIds.Should()
            .BeEmpty("this row's principal is configured to reach none of what it seeded");
        seeded
            .InaccessibleIds.Should()
            .HaveCount(
                SeededDocumentCount,
                "the collection really holds documents, so an empty result is authorization's doing rather "
                    + "than an empty database"
            );

        int totalCount = await ReadTotalCountAsync(harness, collectionEndpoint);
        totalCount.Should().Be(0);

        var pageTokens = await ReadPageTokensAsync(
            harness,
            $"{collectionEndpoint}/partitions?number={RequestedPartitionCount}"
        );
        pageTokens.Should().BeEmpty("a candidate set with no rows has no boundaries");

        var (pageIds, nextPageToken) = await ReadPageAsync(
            harness,
            collectionEndpoint,
            PageTokenCodec.Encode(CursorRange.From(1))
        );

        pageIds.Should().BeEmpty();
        nextPageToken.Should().BeNull("there is nothing for a continuation to resume after");
    }

    private static void AssertResolvesTheAccessibleSet(
        List<string> returnedIds,
        SeededMatrix seeded,
        string because
    )
    {
        // Asserted on the list, before any set conversion: a candidate relation that returned a document
        // twice must fail here rather than being normalized away by the comparison that follows.
        returnedIds.Should().OnlyHaveUniqueItems($"{because} must not return a document more than once");
        returnedIds
            .Should()
            .BeEquivalentTo(
                seeded.AccessibleIds,
                $"{because} must resolve exactly the accessible candidate set"
            );

        if (seeded.InaccessibleIds.Count > 0)
        {
            returnedIds
                .Should()
                .NotIntersectWith(
                    seeded.InaccessibleIds,
                    $"{because} must not disclose a document the caller may not read"
                );
        }
    }

    /// <summary>
    /// Walks from the supplied token to the terminal empty response, returning every identifier in the
    /// order it arrived. The result is a list rather than a set so a repeated document survives to the
    /// caller's assertions.
    /// </summary>
    private static async Task<List<string>> WalkAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        string entryToken,
        int maximumWalkedPages
    )
    {
        List<string> returnedIds = [];
        string? pageToken = entryToken;

        for (var page = 0; page < maximumWalkedPages; page++)
        {
            var (pageIds, nextPageToken) = await ReadPageAsync(harness, collectionEndpoint, pageToken!);

            pageIds
                .Should()
                .HaveCountLessThanOrEqualTo(HostMaximumPageSize, "a page cannot exceed its page size");
            returnedIds.AddRange(pageIds);

            if (nextPageToken is null)
            {
                pageIds.Should().BeEmpty("the page that ends a walk selects nothing");
                return returnedIds;
            }

            pageToken = nextPageToken;
        }

        throw new InvalidOperationException(
            $"A cursor walk of '{collectionEndpoint}' did not terminate within {maximumWalkedPages} pages."
        );
    }

    private static async Task AssertSelectsNothingAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        CursorRange range,
        string because
    )
    {
        var (pageIds, nextPageToken) = await ReadPageAsync(
            harness,
            collectionEndpoint,
            PageTokenCodec.Encode(range)
        );

        pageIds.Should().BeEmpty($"{because} selects nothing");
        nextPageToken.Should().BeNull($"{because} has nothing for a continuation to resume after");
    }

    private static async Task<(List<string> PageIds, string? NextPageToken)> ReadPageAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        string pageToken
    )
    {
        char separator = QuerySeparator(collectionEndpoint);
        string requestUri =
            $"{collectionEndpoint}{separator}pageToken={Uri.EscapeDataString(pageToken)}"
            + $"&pageSize={HostMaximumPageSize}";

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        List<string> pageIds =
        [
            .. JsonNode.Parse(body)!.AsArray().Select(static document => document!["id"]!.GetValue<string>()),
        ];

        if (!response.Headers.TryGetValues("Next-Page-Token", out IEnumerable<string>? headerValues))
        {
            return (pageIds, null);
        }

        return (pageIds, headerValues.Single());
    }

    private static async Task<IReadOnlyList<string>> ReadPageTokensAsync(
        ApiIntegrationHarness harness,
        string requestUri
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return
        [
            .. JsonNode.Parse(body)!["pageTokens"]!
                .AsArray()
                .Select(static pageToken => pageToken!.GetValue<string>()),
        ];
    }

    private static async Task<int> ReadTotalCountAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint
    )
    {
        char separator = QuerySeparator(collectionEndpoint);

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{collectionEndpoint}{separator}limit=1&totalCount=true"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response
            .Headers.TryGetValues("Total-Count", out IEnumerable<string>? totalCountValues)
            .Should()
            .BeTrue("totalCount=true must emit the Total-Count header");

        return int.Parse(totalCountValues!.Single(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Decodes every returned token through the codec that produced it, so a token naming a different
    /// range than it encodes fails here rather than surviving as an opaque string.
    /// </summary>
    private static List<CursorRange> DecodeRanges(IReadOnlyList<string> pageTokens)
    {
        List<CursorRange> ranges = [];

        foreach (string pageToken in pageTokens)
        {
            PageTokenCodec
                .TryDecode(pageToken, out CursorRange? range)
                .Should()
                .BeTrue("an emitted partition token must decode through the codec that produced it");
            ranges.Add(range!);
        }

        return ranges;
    }

    private static char QuerySeparator(string endpoint) =>
        endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
}

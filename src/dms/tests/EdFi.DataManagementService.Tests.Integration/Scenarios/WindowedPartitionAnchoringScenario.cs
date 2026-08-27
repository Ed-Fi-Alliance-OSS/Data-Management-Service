// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using MatrixSupport = EdFi.DataManagementService.Tests.Integration.Scenarios.CursorPartitionAuthorizationMatrixSupport;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The partitions endpoint under a max-bearing change-version window, end to end against the assembled
/// host. The boundaries are ranked, cut, and handed out as <c>ContentVersion</c> values, so what is
/// proven here is that a client given a windowed boundary set can walk every one of its partitions and
/// see the window exactly once.
///
/// <para>
/// Every request is bounded on both sides — <c>minChangeVersion</c> just above the collection's
/// high-water mark before seeding, <c>maxChangeVersion</c> at its mark after — so the candidate set the
/// boundaries are calculated over is exactly what this scenario seeded, and the expected result is an
/// exact set rather than a superset. Documents are also written <em>after</em> the window closes: the
/// last range is unbounded above, so those are what tell a range that is genuinely clipped by the
/// window from one that merely looks bounded because nothing followed it.
/// </para>
///
/// <para>
/// One row seeds a partly inaccessible window instead, because the relation the boundaries are cut over
/// is the <em>authorized</em> candidate relation and the <c>ContentVersion</c> anchor changed what that
/// relation projects. Its principal and fixture come from the cursor/partition authorization matrix,
/// whose rows hold the <c>DocumentId</c> anchor fixed; this is the same claim under the other anchor.
/// </para>
/// </summary>
internal static class WindowedPartitionAnchoringScenario
{
    private const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string MergeItemsPartitionsEndpoint = $"{MergeItemsEndpoint}/partitions";
    private const string DescriptorEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    private const string DescriptorPartitionsEndpoint = $"{DescriptorEndpoint}/partitions";
    private const string AvailableChangeVersionsEndpoint = "/changeQueries/v1/availableChangeVersions";
    private const string StandardJsonContentType = "application/json";

    /// <summary>
    /// The maximum page size the fixtures binding this scenario must configure their host with.
    /// </summary>
    /// <remarks>
    /// The mandatory minimum partition size is <c>MaximumPageSize * 5</c>, so at the deployed value of
    /// 500 no window this scenario could seed over HTTP would ever be cut into more than one partition
    /// and every boundary assertion below would pass without a single cut being made. Lowering it to two
    /// puts the minimum at ten candidate rows, which the seed clears.
    /// </remarks>
    internal const int HostMaximumPageSize = 2;

    /// <summary>
    /// Enough documents inside the window that the requested partition count is actually reachable at
    /// the minimum partition size the page size above implies.
    /// </summary>
    private const int SeededDocumentCount = 25;

    /// <summary>
    /// Written after the window closes, so their change versions sit above <c>maxChangeVersion</c>. The
    /// last partition's range is unbounded above and would reach them if the window were not applied.
    /// </summary>
    private const int BeyondWindowDocumentCount = 3;

    private const int RequestedPartitionCount = 3;

    /// <summary>
    /// A walk of one partition that failed to advance exhausts this and fails with the pages it did
    /// retrieve rather than running forever.
    /// </summary>
    private const int MaximumWalkedPages = 40;

    public static async Task It_partitions_a_windowed_regular_resource_collection_by_content_version(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedWindowedMergeItemsAsync(harness, "regular");

        await AssertWindowedPartitioningAsync(
            harness,
            MergeItemsEndpoint,
            MergeItemsPartitionsEndpoint,
            seeded
        );
    }

    public static async Task It_partitions_a_windowed_descriptor_collection_by_content_version(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedWindowedDescriptorsAsync(harness, "descriptor");

        await AssertWindowedPartitioningAsync(
            harness,
            DescriptorEndpoint,
            DescriptorPartitionsEndpoint,
            seeded
        );
    }

    /// <summary>
    /// Boundaries are cut over the authorized candidate relation, not over the window. Under a
    /// <c>ContentVersion</c> anchor that relation projects the anchor where a <c>DocumentId</c>-anchored one
    /// projects the id, while the namespace predicate that excludes the inaccessible documents is composed
    /// into the same relation — so this is the combination of anchoring and authorization that neither the
    /// unwindowed authorization matrix nor a windowed page walk reaches.
    /// </summary>
    /// <remarks>
    /// Cutting boundaries before authorization is the defect this catches, and it is invisible to a page
    /// test: a page applies its bounds and its authorization in one statement, while a boundary set is
    /// sized in one statement and walked in another. Boundaries taken over the unauthorized population
    /// would be sized against a larger count and start at rows the caller cannot read, which shows up here
    /// as a partition that returns nothing, a range that begins at an excluded document, or a walk that
    /// discloses one.
    /// <para>
    /// The floor is read before the seed rather than after it, so the reference data
    /// <c>SeedNamespaceResourcesAsync</c> creates first is inside the window too. That costs nothing: the
    /// window is only ever applied to the one collection this scenario partitions, and every document of
    /// that collection the seed creates is a member of the matrix seed. Documents any earlier test left in
    /// it sit below the floor and are excluded, which is what makes the expected result an exact set.
    /// </para>
    /// <para>
    /// No documents are written above the window here. The clipping property the other two scenarios prove
    /// with them is a property of the range, not of the principal, so repeating it under authorization
    /// would seed 28 more documents to re-prove something already held.
    /// </para>
    /// </remarks>
    public static async Task It_partitions_a_windowed_collection_over_the_authorized_candidate_set(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        long floor = await NewestChangeVersionAsync(harness);

        var matrixSeed = await MatrixSupport.SeedNamespaceResourcesAsync(
            harness,
            MatrixSupport.MatrixAccessibility.Namespace
        );

        string window = await WindowSinceAsync(harness, floor);

        matrixSeed
            .InaccessibleIds.Should()
            .NotBeEmpty(
                "a seed the caller can read entirely would satisfy every assertion below without "
                    + "authorization filtering anything"
            );

        await AssertWindowedPartitioningAsync(
            harness,
            MatrixSupport.NamespaceResourcesEndpoint,
            $"{MatrixSupport.NamespaceResourcesEndpoint}/partitions",
            new SeededPartitionWindow(
                matrixSeed.AccessibleIds,
                BeyondWindowIds: [],
                matrixSeed.InaccessibleIds,
                window
            )
        );
    }

    /// <summary>
    /// Every property the windowed boundary set has to hold, asserted against one seed: the tokens are
    /// anchored on <c>ContentVersion</c>, they never exceed the requested count, the ranges they carry
    /// tile the window without overlapping, the last one is unbounded above, each one really begins at a
    /// candidate, and walking all of them delivers the window exactly once and nothing beyond it.
    /// </summary>
    private static async Task AssertWindowedPartitioningAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        string partitionsEndpoint,
        SeededPartitionWindow seeded
    )
    {
        var pageTokens = await ReadPageTokensAsync(
            harness,
            $"{partitionsEndpoint}?number={RequestedPartitionCount}&{seeded.Window}"
        );

        pageTokens
            .Should()
            .HaveCountGreaterThan(
                1,
                "the seeded window clears the minimum partition size, so it really is cut into several "
                    + "partitions and the boundary assertions below run across separate ranges"
            );
        pageTokens
            .Should()
            .HaveCountLessThanOrEqualTo(
                RequestedPartitionCount,
                "the requested count is an upper bound, never a promise"
            );

        var ranges = DecodeWindowedRanges(pageTokens);

        AssertRangesTileTheWindow(ranges);

        var walked = await WalkEveryPartitionAsync(harness, collectionEndpoint, seeded.Window, pageTokens);

        walked
            .PerPartitionCounts.Should()
            .OnlyContain(
                count => count > 0,
                "every boundary is a candidate's own anchor value, so no partition can be empty"
            );
        walked
            .ReturnedIds.Should()
            .BeEquivalentTo(
                seeded.Ids,
                "the partitions cover the whole window and overlap nowhere, so walking all of them "
                    + "delivers each member of it exactly once"
            );
        walked
            .ReturnedIds.Should()
            .NotIntersectWith(
                seeded.BeyondWindowIds,
                "the last range is unbounded above but the request is still clipped by maxChangeVersion"
            );
        walked
            .ReturnedIds.Should()
            .NotIntersectWith(
                seeded.InaccessibleIds,
                "boundaries are ranked and cut over the authorized candidate relation, so a document the "
                    + "caller may not read falls inside no partition"
            );
    }

    /// <summary>
    /// Decodes every partition token, asserting each is anchored on <c>ContentVersion</c>. A token
    /// marked otherwise would be rejected the moment a client replayed it under the window it was issued
    /// for, so this names the token that stopped marking rather than reporting the walk as empty.
    /// </summary>
    private static IReadOnlyList<CursorRange> DecodeWindowedRanges(IReadOnlyList<string> pageTokens)
    {
        List<CursorRange> ranges = [];

        foreach (string pageToken in pageTokens)
        {
            PageTokenCodec
                .TryDecode(pageToken, out CursorRange? range, out PageOrderingMode orderingMode)
                .Should()
                .BeTrue("an emitted partition token must decode through the codec that produced it");
            orderingMode
                .Should()
                .Be(
                    PageOrderingMode.ContentVersion,
                    "a windowed collection is ranked and cut by ContentVersion, so its boundaries are "
                        + "expressed in it"
                );

            ranges.Add(range!);
        }

        return ranges;
    }

    /// <summary>
    /// The ranges are contiguous, ascending, and non-overlapping, and the last is unbounded above.
    /// Asserted on the decoded boundaries rather than only on the documents a walk reaches: boundaries
    /// that overlapped would still deliver every document once if the walk deduplicated, and boundaries
    /// that left a gap would be indistinguishable from a window that simply held fewer rows.
    /// </summary>
    private static void AssertRangesTileTheWindow(IReadOnlyList<CursorRange> ranges)
    {
        for (var index = 1; index < ranges.Count; index++)
        {
            ranges[index]
                .InclusiveMinimum.Should()
                .Be(
                    ranges[index - 1].InclusiveMaximum + 1,
                    "each range ends one below the next one's start, so the ranges neither overlap nor "
                        + "leave a value between them"
                );
        }

        ranges[^1]
            .InclusiveMaximum.Should()
            .Be(
                long.MaxValue,
                "the last range stays open above so a document written after the boundaries were "
                    + "calculated is still reachable"
            );
    }

    /// <summary>
    /// Walks every partition of a windowed boundary set, repeating the window on every request because
    /// that is the only form in which a windowed token is replayable.
    /// </summary>
    private static async Task<PartitionWalk> WalkEveryPartitionAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        string window,
        IReadOnlyList<string> pageTokens
    )
    {
        HashSet<string> returnedIds = [];
        List<int> perPartitionCounts = [];

        foreach (string partitionToken in pageTokens)
        {
            string? pageToken = partitionToken;
            var partitionCount = 0;

            for (var page = 0; page < MaximumWalkedPages && pageToken is not null; page++)
            {
                using HttpResponseMessage response = await harness.HttpClient.GetAsync(
                    $"{collectionEndpoint}?pageToken={Uri.EscapeDataString(pageToken)}"
                        + $"&pageSize={HostMaximumPageSize}&{window}"
                );
                string body = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.OK, body);

                foreach (JsonNode? document in JsonNode.Parse(body)!.AsArray())
                {
                    returnedIds
                        .Add(document!["id"]!.GetValue<string>())
                        .Should()
                        .BeTrue("partitions are disjoint, so no document may be returned twice");
                    partitionCount++;
                }

                pageToken = response.Headers.TryGetValues("Next-Page-Token", out var nextPageTokenValues)
                    ? nextPageTokenValues.Single()
                    : null;
            }

            pageToken
                .Should()
                .BeNull(
                    $"a walk of one partition of '{collectionEndpoint}' must terminate within "
                        + $"{MaximumWalkedPages} pages"
                );

            perPartitionCounts.Add(partitionCount);
        }

        return new PartitionWalk(returnedIds, perPartitionCounts);
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

    /// <summary>
    /// The collection's change-version high-water mark, read from the endpoint that publishes it rather
    /// than from the database, so the window these requests carry is one a client could have asked for.
    /// </summary>
    private static async Task<long> NewestChangeVersionAsync(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            AvailableChangeVersionsEndpoint
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return JsonNode.Parse(body)!["newestChangeVersion"]!.GetValue<long>();
    }

    private static async Task<string> WindowSinceAsync(ApiIntegrationHarness harness, long floor)
    {
        long ceiling = await NewestChangeVersionAsync(harness);

        ceiling
            .Should()
            .BeGreaterThan(floor, "the seed must have advanced the change version for a window to hold it");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"minChangeVersion={floor + 1}&maxChangeVersion={ceiling}"
        );
    }

    private static async Task<SeededPartitionWindow> SeedWindowedMergeItemsAsync(
        ApiIntegrationHarness harness,
        string scenario
    )
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        // The merge item requires a resolvable descriptor reference, so the reference target is created
        // before the documents that point at it — and before the window opens, so its own change version
        // cannot land inside the window a merge-item partitioning is asserted against. It doubles as the
        // fence that makes the floor below a change version that was really assigned rather than the
        // value the next write will take.
        string descriptorNamespace =
            $"uri://ed-fi.org/SchoolTypeDescriptor/WindowedPartition/{scenario}/{suffix}";
        string descriptorCodeValue = $"WindowedPartition-{scenario}-{suffix}-ref";
        await CreateAsync(
            harness,
            DescriptorEndpoint,
            new JsonObject
            {
                ["namespace"] = descriptorNamespace,
                ["codeValue"] = descriptorCodeValue,
                ["shortDescription"] = $"WindowedPartition {scenario} {suffix} reference",
            }
        );

        long floor = await NewestChangeVersionAsync(harness);

        async Task<string> CreateMergeItemAsync(int index) =>
            await CreateAsync(
                harness,
                MergeItemsEndpoint,
                new JsonObject
                {
                    ["profileRootOnlyMergeItemId"] = UniqueIdentity(suffix, index),
                    ["displayName"] = $"WindowedPartition {scenario} {suffix} {index}",
                    ["primarySchoolTypeDescriptor"] = $"{descriptorNamespace}#{descriptorCodeValue}",
                }
            );

        List<string> seededIds = [];

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            seededIds.Add(await CreateMergeItemAsync(index));
        }

        string window = await WindowSinceAsync(harness, floor);

        List<string> beyondWindowIds = [];

        for (var index = 0; index < BeyondWindowDocumentCount; index++)
        {
            beyondWindowIds.Add(await CreateMergeItemAsync(SeededDocumentCount + index));
        }

        return new SeededPartitionWindow(seededIds, beyondWindowIds, InaccessibleIds: [], window);
    }

    private static async Task<SeededPartitionWindow> SeedWindowedDescriptorsAsync(
        ApiIntegrationHarness harness,
        string scenario
    )
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string descriptorNamespace =
            $"uri://ed-fi.org/SchoolTypeDescriptor/WindowedPartition/{scenario}/{suffix}";

        async Task<string> CreateDescriptorAsync(string label) =>
            await CreateAsync(
                harness,
                DescriptorEndpoint,
                new JsonObject
                {
                    ["namespace"] = descriptorNamespace,
                    ["codeValue"] = $"WindowedPartition-{scenario}-{suffix}-{label}",
                    ["shortDescription"] = $"WindowedPartition {scenario} {suffix} {label}",
                }
            );

        // One write before the floor is read, for the same reason the merge-item seed creates its
        // reference first: the floor has to be a change version that was really assigned.
        await CreateDescriptorAsync("fence");

        long floor = await NewestChangeVersionAsync(harness);

        List<string> seededIds = [];

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            seededIds.Add(await CreateDescriptorAsync(index.ToString(CultureInfo.InvariantCulture)));
        }

        string window = await WindowSinceAsync(harness, floor);

        List<string> beyondWindowIds = [];

        for (var index = 0; index < BeyondWindowDocumentCount; index++)
        {
            beyondWindowIds.Add(
                await CreateDescriptorAsync($"beyond-{index.ToString(CultureInfo.InvariantCulture)}")
            );
        }

        return new SeededPartitionWindow(seededIds, beyondWindowIds, InaccessibleIds: [], window);
    }

    /// <summary>
    /// A per-run identity that stays inside Int32, because the merge item's identity is an integer and a
    /// collision with a sibling scenario's seed would answer 200 on an update instead of 201.
    /// </summary>
    private static int UniqueIdentity(string suffix, int index) =>
        1_394_500 + Math.Abs(suffix.GetHashCode(StringComparison.Ordinal) % 100_000) * 100 + index;

    private static async Task<string> CreateAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {endpoint} body: {body}");
        response.Headers.Location.Should().NotBeNull($"POST {endpoint} must return a Location header");

        Uri location = response.Headers.Location!;
        string locationPath = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;

        return locationPath[(locationPath.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// The documents inside the window, the documents written after it closed, the documents inside it the
    /// caller may not read, and the window itself.
    /// </summary>
    /// <param name="Ids">
    /// The documents a walk of every partition must deliver, exactly once each. For an authorized seed this
    /// is the accessible subset of the window rather than the whole of it.
    /// </param>
    /// <param name="InaccessibleIds">
    /// Documents inside the window that authorization excludes. Empty for a seed whose whole window is
    /// readable, which is what makes the disclosure assertion a no-op there rather than a rule that only
    /// some seeds are held to.
    /// </param>
    private sealed record SeededPartitionWindow(
        IReadOnlyList<string> Ids,
        IReadOnlyList<string> BeyondWindowIds,
        IReadOnlyList<string> InaccessibleIds,
        string Window
    );

    private sealed record PartitionWalk(HashSet<string> ReturnedIds, IReadOnlyList<int> PerPartitionCounts);
}

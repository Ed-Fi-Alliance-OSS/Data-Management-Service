// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Seeding and page reading shared by the public cursor and partition contract scenarios: the
/// collections they page over, how a document gets into one through the same HTTP pipeline the pages
/// are served from, and what one page response carries.
/// </summary>
/// <remarks>
/// The regular and descriptor collections come from the descriptor-runtime core ApiSchema, which the
/// descriptor-runtime and cursor-partition-contract fixtures both load; the extension collection exists
/// only in the latter, so a scenario reaching for it must bind that fixture. Every document is created
/// over HTTP rather than inserted, so a page can only return what the write path really stored.
/// </remarks>
internal static class CursorContractSupport
{
    internal const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    internal const string MergeItemsPartitionsEndpoint = $"{MergeItemsEndpoint}/partitions";
    internal const string DescriptorEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    internal const string DescriptorPartitionsEndpoint = $"{DescriptorEndpoint}/partitions";

    /// <summary>
    /// The standalone extension resource the <c>CursorPartitionContract</c> fixture declares. Reachable
    /// only from that fixture; the descriptor-runtime fixture does not load the extension project.
    /// </summary>
    internal const string ExtensionItemsEndpoint = "/data/cursorpartitionext/partitionContractItems";
    internal const string ExtensionItemsPartitionsEndpoint = $"{ExtensionItemsEndpoint}/partitions";

    internal const string AvailableChangeVersionsEndpoint = "/changeQueries/v1/availableChangeVersions";

    internal const string NextPageTokenHeaderName = "Next-Page-Token";
    internal const string TotalCountHeaderName = "Total-Count";

    private const string StandardJsonContentType = "application/json";

    /// <summary>
    /// Identities are handed out from one counter so two seeds in the same process cannot collide on a
    /// merge item's integer identity, which would answer 200 on an update instead of 201.
    /// </summary>
    private static int _nextMergeItemIdentity = 1_390_000;

    /// <summary>
    /// The same counter discipline for the extension resource's integer identity, kept separate so the
    /// two collections cannot interfere with each other's identities.
    /// </summary>
    private static int _nextExtensionItemIdentity = 1_390_000;

    /// <summary>
    /// A token covering every identity a fixture can reach, encoded by the codec that decodes it, so a
    /// walk can be entered without first reading a continuation out of a response.
    /// </summary>
    /// <remarks>
    /// Decodable by construction rather than by a transcription of the transport encoding happening to
    /// stay in step with it. The encoding is unpadded base64url, so the value needs no escaping when
    /// it is placed in a query string.
    /// <para>
    /// Anchored on <c>DocumentId</c> because the scenarios that enter a walk with this token carry no
    /// change-version window at all, and that shape resolves that anchor on every data source -
    /// routing alone must not change the order an unfiltered collection is walked in. A token whose
    /// marker disagrees with the anchor its request resolves is rejected, so the two have to be
    /// chosen together.
    /// </para>
    /// </remarks>
    internal static string EntryPageToken { get; } =
        PageTokenCodec.Encode(CursorRange.From(1), PageOrderingMode.DocumentId);

    /// <summary>
    /// One page as a client sees it: the document ids in the body, the continuation offered, and the
    /// total-count header when one was requested.
    /// </summary>
    internal sealed record PageResponse(
        IReadOnlyList<string> DocumentIds,
        string? NextPageToken,
        string? TotalCount
    );

    /// <summary>
    /// Reads one page, asserting only that it succeeded. The continuation is round-tripped through the
    /// codec, so a token that decoded to a different range than it names would be caught here rather
    /// than surviving as an opaque string a later request quietly ignores.
    /// </summary>
    internal static async Task<PageResponse> ReadPageAsync(ApiIntegrationHarness harness, string requestUri)
    {
        ArgumentNullException.ThrowIfNull(harness);

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        List<string> documentIds =
        [
            .. JsonNode.Parse(body)!.AsArray().Select(static document => document!["id"]!.GetValue<string>()),
        ];

        string? totalCount = response.Headers.TryGetValues(TotalCountHeaderName, out var totalCountValues)
            ? totalCountValues.Single()
            : null;

        if (!response.Headers.TryGetValues(NextPageTokenHeaderName, out var nextPageTokenValues))
        {
            return new PageResponse(documentIds, NextPageToken: null, totalCount);
        }

        string nextPageToken = nextPageTokenValues.Single();

        // The decoded anchor is discarded: this reader serves every collection the contract scenarios
        // page over, and what it asserts about a continuation is the shape of its range, not which
        // column that range is expressed in. A scenario that cares about the anchor asserts it itself.
        PageTokenCodec
            .TryDecode(nextPageToken, out var range, out _)
            .Should()
            .BeTrue("an emitted continuation must decode through the codec that produced it");
        range!
            .InclusiveMinimum.Should()
            .BePositive("a continuation resumes after the keys the page just selected");

        return new PageResponse(documentIds, nextPageToken, totalCount);
    }

    /// <summary>
    /// Reads a partitions response, asserting only that it succeeded, and returns the tokens it handed
    /// out in the order the response listed them.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ReadPageTokensAsync(
        ApiIntegrationHarness harness,
        string requestUri
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

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
    /// Decodes returned partition tokens and asserts the identity intervals they name are themselves
    /// valid, contiguous, and non-overlapping, in the order the response listed them.
    /// </summary>
    /// <remarks>
    /// Disjointness of the documents a walk returns is a weaker claim than disjointness of the ranges
    /// that produced them: two overlapping intervals whose overlap happens to contain only sparse,
    /// deleted, or inaccessible identities return disjoint documents and would satisfy the union and
    /// exact-once assertions while still handing a client ranges that could double-count a document
    /// created later. Reading the intervals out of the tokens is what closes that.
    /// <para>
    /// The contract is that each token covers its starting identity through one less than the next
    /// starting identity, and the last is unbounded above. Asserting the exact adjacency rather than
    /// mere non-overlap also rules out a gap between two ranges, which no set of returned documents
    /// could reveal.
    /// </para>
    /// <para>
    /// One copy, shared by every scenario that consumes a partitions response. These are the response's
    /// invariants rather than any one scenario's expectations, so a second copy could only drift from
    /// this one and weaken whichever surface held the weaker copy.
    /// </para>
    /// </remarks>
    /// <param name="pageTokens">The tokens the partitions response returned, in response order.</param>
    /// <param name="context">
    /// How a failure should name what was being consumed, such as <c>"case 'cursor-precedence-01'"</c>.
    /// </param>
    internal static void AssertTokenRangesTileTheIdentitySpace(
        IReadOnlyList<string> pageTokens,
        string context
    )
    {
        ArgumentNullException.ThrowIfNull(pageTokens);

        List<CursorRange> ranges = [];

        foreach (string pageToken in pageTokens)
        {
            PageTokenCodec
                .TryDecode(pageToken, out CursorRange? range, out _)
                .Should()
                .BeTrue(
                    "{0}: a token the partitions response handed out must decode through the codec",
                    context
                );
            ranges.Add(range!);
        }

        ranges
            .Should()
            .NotBeEmpty("{0}: a partitions response over a non-empty collection names a range", context);
        ranges[0]
            .InclusiveMinimum.Should()
            .BePositive("{0}: the first partition starts at a real identity", context);

        for (var index = 0; index < ranges.Count; index++)
        {
            ranges[index]
                .InclusiveMaximum.Should()
                .BeGreaterThanOrEqualTo(
                    ranges[index].InclusiveMinimum,
                    "{0}: partition {1} must name a range that can match something",
                    context,
                    index
                );

            if (index + 1 < ranges.Count)
            {
                ranges[index]
                    .InclusiveMaximum.Should()
                    .Be(
                        ranges[index + 1].InclusiveMinimum - 1,
                        "{0}: partition {1} must end exactly where partition {2} begins, leaving neither "
                            + "a gap nor an overlap",
                        context,
                        index,
                        index + 1
                    );
            }
        }

        ranges[^1]
            .InclusiveMaximum.Should()
            .Be(
                long.MaxValue,
                "{0}: the final partition is unbounded above, so a document created during the walk "
                    + "cannot fall outside every range",
                context
            );
    }

    /// <summary>
    /// Asserts the walked partitions hold every expected document exactly once and nothing else, and
    /// that no two of them hold the same document.
    /// </summary>
    /// <remarks>
    /// One copy for the same reason the range assertions are one copy: this is the tiling claim itself,
    /// and a surface asserting a weaker version of it would report coverage it had not shown.
    /// </remarks>
    /// <param name="walkedPartitions">Each partition's documents, kept separate for the disjointness assertion.</param>
    /// <param name="expectedIds">The document ids the seed says the partitions must together hold.</param>
    /// <param name="context">How a failure should name what was being consumed.</param>
    internal static void AssertPartitionsCoverExactly(
        IReadOnlyList<IReadOnlyList<string>> walkedPartitions,
        IReadOnlyCollection<string> expectedIds,
        string context
    )
    {
        ArgumentNullException.ThrowIfNull(walkedPartitions);
        ArgumentNullException.ThrowIfNull(expectedIds);

        for (var partition = 0; partition < walkedPartitions.Count; partition++)
        {
            walkedPartitions[partition]
                .Should()
                .OnlyHaveUniqueItems(
                    "{0}: partition {1} must not return a document twice",
                    context,
                    partition
                );

            for (var other = partition + 1; other < walkedPartitions.Count; other++)
            {
                walkedPartitions[partition]
                    .Should()
                    .NotIntersectWith(
                        walkedPartitions[other],
                        "{0}: partitions {1} and {2} cover disjoint ranges",
                        context,
                        partition,
                        other
                    );
            }
        }

        walkedPartitions
            .SelectMany(static partition => partition)
            .Should()
            .BeEquivalentTo(
                expectedIds,
                "{0}: the partitions together cover every expected member exactly once and nothing else",
                context
            );
    }

    /// <summary>
    /// Validates a query suffix against the one thing its callers cannot see: that it is appended to an
    /// already-started query string and must therefore open with its own separator.
    /// </summary>
    /// <remarks>
    /// A suffix missing the separator does not fail the request. It fuses onto the preceding value --
    /// <c>pageSize=2label=x</c> -- so the walk silently issues a different query than the one the caller
    /// named, and the coverage assertion that follows measures the wrong candidate set.
    /// </remarks>
    internal static void ValidateQuerySuffix(
        string querySuffix,
        [CallerArgumentExpression(nameof(querySuffix))] string? parameterName = null
    )
    {
        ArgumentNullException.ThrowIfNull(querySuffix, parameterName);

        if (querySuffix.Length > 0 && querySuffix[0] != '&')
        {
            throw new ArgumentException(
                "A query suffix is appended to an existing query string and must begin with '&', but "
                    + $"'{querySuffix}' does not.",
                parameterName
            );
        }
    }

    /// <summary>
    /// Walks one range to its terminal empty page and returns every document id it yielded.
    /// </summary>
    /// <remarks>
    /// The walk only ever follows a continuation the host handed it, so a partition token the handler,
    /// the codec, request validation, and page selection did not agree on would yield nothing here
    /// rather than quietly resolving to a different range.
    /// <para>
    /// The walk ends only on a request that both offered no continuation and returned nothing. A
    /// non-empty page without a continuation is a failure rather than a terminus: the contract emits a
    /// continuation whenever page selection returned a non-empty keyset, so a walk that stopped on a
    /// page still holding documents would have silently truncated the range it was asked to cover.
    /// </para>
    /// <para>
    /// <paramref name="querySuffix"/> is appended to every page request, which is how a filtered or
    /// change-version-bounded walk repeats the identical query on each page. The token stores neither,
    /// so a walk that dropped the suffix after its first request would widen its own candidate set.
    /// </para>
    /// </remarks>
    internal static async Task<IReadOnlyList<string>> WalkFromTokenAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string pageToken,
        int pageSize,
        string querySuffix = "",
        int maximumWalkedPages = 200
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(pageToken);
        ValidateQuerySuffix(querySuffix);

        List<string> documentIds = [];
        string? nextPageToken = pageToken;

        for (var page = 0; page < maximumWalkedPages; page++)
        {
            var pageResponse = await ReadPageAsync(
                harness,
                $"{endpoint}?pageToken={Uri.EscapeDataString(nextPageToken!)}&pageSize={pageSize}{querySuffix}"
            );

            documentIds.AddRange(pageResponse.DocumentIds);

            if (pageResponse.NextPageToken is null)
            {
                pageResponse
                    .DocumentIds.Should()
                    .BeEmpty(
                        "a walk ends on the request that selected nothing, so a page that still "
                            + "returned documents must have offered a continuation"
                    );
                return documentIds;
            }

            nextPageToken = pageResponse.NextPageToken;
        }

        throw new InvalidOperationException(
            $"A walk of '{endpoint}' did not terminate within {maximumWalkedPages} pages."
        );
    }

    /// <summary>
    /// Reads the newest live change version the host reports, for use as a change-version window bound.
    /// </summary>
    /// <remarks>
    /// Read from the published change-queries endpoint rather than from the leased database, so the
    /// bound a walk repeats is a value a client could have obtained for itself.
    /// </remarks>
    internal static async Task<long> ReadNewestChangeVersionAsync(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            AvailableChangeVersionsEndpoint
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return JsonNode.Parse(body)!["newestChangeVersion"]!.GetValue<long>();
    }

    /// <summary>
    /// Seeds <paramref name="count"/> regular-resource documents and returns their ids in creation
    /// order, which is also ascending identity order because each is created after the one before it.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> SeedMergeItemsAsync(
        ApiIntegrationHarness harness,
        string scenario,
        int count
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        string suffix = Guid.NewGuid().ToString("N")[..8];

        // The merge item requires a resolvable descriptor reference, so the reference target is created
        // through the same pipeline before the documents that point at it.
        string descriptorNamespace = $"uri://ed-fi.org/SchoolTypeDescriptor/Contract/{scenario}/{suffix}";
        string descriptorCodeValue = $"Contract-{scenario}-{suffix}-ref";
        await CreateAsync(
            harness,
            DescriptorEndpoint,
            new JsonObject
            {
                ["namespace"] = descriptorNamespace,
                ["codeValue"] = descriptorCodeValue,
                ["shortDescription"] = $"Contract {scenario} {suffix} reference",
            }
        );

        List<string> seededIds = [];

        for (var index = 0; index < count; index++)
        {
            var payload = new JsonObject
            {
                ["profileRootOnlyMergeItemId"] = Interlocked.Increment(ref _nextMergeItemIdentity),
                ["displayName"] = $"Contract {scenario} {suffix} {index}",
                ["primarySchoolTypeDescriptor"] = $"{descriptorNamespace}#{descriptorCodeValue}",
            };

            seededIds.Add(await CreateAsync(harness, MergeItemsEndpoint, payload));
        }

        return seededIds;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> descriptors under one per-run namespace and returns their ids in
    /// creation order together with the code values that identify them.
    /// </summary>
    internal static async Task<SeededDescriptors> SeedDescriptorsAsync(
        ApiIntegrationHarness harness,
        string scenario,
        int count
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string descriptorNamespace = $"uri://ed-fi.org/SchoolTypeDescriptor/Contract/{scenario}/{suffix}";

        List<string> seededIds = [];
        List<string> codeValues = [];

        for (var index = 0; index < count; index++)
        {
            string codeValue = $"Contract-{scenario}-{suffix}-{index}";
            codeValues.Add(codeValue);

            seededIds.Add(
                await CreateAsync(
                    harness,
                    DescriptorEndpoint,
                    new JsonObject
                    {
                        ["namespace"] = descriptorNamespace,
                        ["codeValue"] = codeValue,
                        ["shortDescription"] = $"Contract {scenario} {suffix} {index}",
                    }
                )
            );
        }

        return new SeededDescriptors(seededIds, codeValues, descriptorNamespace);
    }

    /// <summary>Descriptors a seed created, and the values a filter can select them by.</summary>
    internal sealed record SeededDescriptors(
        IReadOnlyList<string> Ids,
        IReadOnlyList<string> CodeValues,
        string Namespace
    );

    /// <summary>
    /// Seeds <paramref name="count"/> extension-resource documents, giving each the label and number the
    /// callbacks return for its index, and returns their ids in creation order.
    /// </summary>
    /// <remarks>
    /// The label and number are supplied per document rather than fixed, because the filtered walks
    /// need a seed whose matching and non-matching members are interleaved: a filter dropped partway
    /// through a walk must pull in a non-matching document rather than land in an untouched tail.
    /// </remarks>
    internal static async Task<IReadOnlyList<SeededExtensionItem>> SeedExtensionItemsAsync(
        ApiIntegrationHarness harness,
        int count,
        Func<int, string> labelFor,
        Func<int, int> numberFor
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(labelFor);
        ArgumentNullException.ThrowIfNull(numberFor);

        List<SeededExtensionItem> seeded = [];

        for (var index = 0; index < count; index++)
        {
            int identity = Interlocked.Increment(ref _nextExtensionItemIdentity);
            string label = labelFor(index);
            int number = numberFor(index);

            var payload = new JsonObject
            {
                ["partitionContractItemId"] = identity,
                ["label"] = label,
                ["number"] = number,
            };

            seeded.Add(
                new SeededExtensionItem(
                    await CreateAsync(harness, ExtensionItemsEndpoint, payload),
                    identity,
                    label,
                    number
                )
            );
        }

        return seeded;
    }

    /// <summary>One seeded extension document, with the values needed to update it in place.</summary>
    internal sealed record SeededExtensionItem(string Id, int Identity, string Label, int Number);

    /// <summary>
    /// Updates one extension document's label, which raises its change version.
    /// </summary>
    /// <remarks>
    /// The label really changes, because an update that changed nothing would be answered as a no-op
    /// and would not move the document's change version — which is the whole point of calling this.
    /// The identity and number are resent unchanged, so the update cannot move the document into or out
    /// of a partition range or a number filter.
    /// </remarks>
    internal static async Task UpdateExtensionItemLabelAsync(
        ApiIntegrationHarness harness,
        SeededExtensionItem item,
        string updatedLabel
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(item);

        var payload = new JsonObject
        {
            ["id"] = item.Id,
            ["partitionContractItemId"] = item.Identity,
            ["label"] = updatedLabel,
            ["number"] = item.Number,
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PutAsync(
            $"{ExtensionItemsEndpoint}/{item.Id}",
            content
        );
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent, $"PUT {ExtensionItemsEndpoint}/{item.Id} body: {body}");
    }

    /// <summary>
    /// Deletes one document through the public endpoint.
    /// </summary>
    internal static async Task DeleteAsync(ApiIntegrationHarness harness, string endpoint, string id)
    {
        ArgumentNullException.ThrowIfNull(harness);

        using HttpResponseMessage response = await harness.HttpClient.DeleteAsync($"{endpoint}/{id}");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"DELETE {endpoint}/{id} body: {body}");
    }

    /// <summary>
    /// Creates one document and returns the id from its <c>Location</c> header.
    /// </summary>
    internal static async Task<string> CreateAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(payload);

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {endpoint} body: {body}");
        response.Headers.Location.Should().NotBeNull($"POST {endpoint} must return a Location header");

        Uri location = response.Headers.Location!;
        string locationPath = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;

        return locationPath[(locationPath.LastIndexOf('/') + 1)..];
    }
}

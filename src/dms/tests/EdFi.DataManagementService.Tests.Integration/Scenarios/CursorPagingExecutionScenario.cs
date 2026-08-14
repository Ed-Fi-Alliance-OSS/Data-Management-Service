// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// What a client actually receives from a cursor walk against the assembled host: pages served over
/// documents it created through the same HTTP pipeline, and a <c>Next-Page-Token</c> it can replay
/// without knowing anything about the identities behind it.
///
/// <para>
/// Handler tests can show the header a <c>QuerySuccess</c> produces, and provider tests can show the
/// keyset a page selects, but neither shows that a token this host emitted is a token this host
/// accepts. These walks only ever follow the header they were handed, so a token the frontend, Core
/// validation, the codec, and page selection did not agree on would fail to advance.
/// </para>
///
/// <para>
/// Each walk tolerates documents other scenarios sharing this fixture may have left behind: it asserts
/// that its own seeded documents each arrive exactly once and that no document arrives twice, which
/// holds regardless of what else the collection contains.
/// </para>
/// </summary>
internal static class CursorPagingExecutionScenario
{
    private const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string DescriptorEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    private const string StandardJsonContentType = "application/json";
    private const string NextPageTokenHeaderName = "Next-Page-Token";

    /// <summary>
    /// Above any change version this fixture can reach, so the window includes every seeded document
    /// and the only thing under test is what a max-bearing window does to the continuation.
    /// </summary>
    private const long UnreachableMaxChangeVersion = 999_999_999L;

    /// <summary>
    /// Enough documents that a page size of two cannot return them all, so the walk must genuinely
    /// continue rather than terminate on its first page.
    /// </summary>
    private const int SeededDocumentCount = 5;

    /// <summary>
    /// The walk cannot loop forever on a broken continuation: a token that failed to advance would
    /// exhaust this and fail with the pages it did retrieve rather than hanging.
    /// </summary>
    private const int MaximumWalkedPages = 25;

    public static async Task It_walks_a_regular_resource_collection_by_cursor(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await SeedMergeItemsAsync(harness, "cursor-walk");

        var walk = await WalkFromFirstPageAsync(harness, MergeItemsEndpoint, pageSize: 2);

        walk.Pages.Should().BeGreaterThan(1, "a page size of two cannot deliver five documents at once");
        walk.ReturnedIds.Should().OnlyHaveUniqueItems("a cursor walk must not return a document twice");
        walk.ReturnedIds.Should().Contain(seededIds);
    }

    public static async Task It_walks_a_descriptor_collection_by_cursor(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await SeedDescriptorsAsync(harness, "cursor-walk");

        var walk = await WalkFromFirstPageAsync(harness, DescriptorEndpoint, pageSize: 2);

        walk.Pages.Should().BeGreaterThan(1);
        walk.ReturnedIds.Should().OnlyHaveUniqueItems();
        walk.ReturnedIds.Should().Contain(seededIds);
    }

    /// <summary>
    /// A traditional <c>limit</c> response carries a continuation too, which is what lets a client enter
    /// a cursor walk without a separate call. The token is replayed here, so the assertion is that the
    /// walk it starts really continues rather than merely that a header was present.
    /// </summary>
    public static async Task It_enters_a_cursor_walk_from_a_traditional_page(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await SeedMergeItemsAsync(harness, "traditional-entry");

        var (firstPageIds, firstPageToken) = await ReadPageAsync(harness, $"{MergeItemsEndpoint}?limit=2");

        firstPageIds.Should().HaveCount(2);
        firstPageToken.Should().NotBeNull("a traditional page that selected keys can begin a cursor walk");

        HashSet<string> returnedIds = [.. firstPageIds];
        var continued = await ContinueWalkAsync(harness, MergeItemsEndpoint, firstPageToken!, returnedIds);

        continued.Should().BeGreaterThan(0, "the token from a traditional page must advance the walk");
        returnedIds.Should().Contain(seededIds);
    }

    /// <summary>
    /// A traditional page over a max-bearing change-version window is ordered by <c>ContentVersion</c>,
    /// so the highest <c>DocumentId</c> it selected does not describe where the page ended and cannot
    /// anchor a continuation. The page is still served; only the header is withheld.
    /// </summary>
    public static async Task It_withholds_a_continuation_from_a_windowed_traditional_page(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        await SeedMergeItemsAsync(harness, "windowed-suppression");

        var (windowedIds, windowedToken) = await ReadPageAsync(
            harness,
            $"{MergeItemsEndpoint}?limit=2&maxChangeVersion={UnreachableMaxChangeVersion}"
        );

        windowedIds.Should().NotBeEmpty("the window includes every seeded document");
        windowedToken.Should().BeNull();

        // The same request without the window does continue, so the suppression above is the window's
        // effect rather than an empty selection or a missing feature.
        var (_, unwindowedToken) = await ReadPageAsync(harness, $"{MergeItemsEndpoint}?limit=2");
        unwindowedToken.Should().NotBeNull();
    }

    private static async Task<CursorWalk> WalkFromFirstPageAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        int pageSize
    )
    {
        // Encoded by the codec that decodes it, so the entry token is decodable by construction rather
        // than by a transcription of the transport encoding happening to stay in step with it.
        string entryToken = PageTokenCodec.Encode(CursorRange.From(1));

        HashSet<string> returnedIds = [];
        var pages = 0;
        string? pageToken = entryToken;

        while (pageToken is not null && pages < MaximumWalkedPages)
        {
            var (pageIds, nextPageToken) = await ReadPageAsync(
                harness,
                $"{endpoint}?pageToken={Uri.EscapeDataString(pageToken)}&pageSize={pageSize}"
            );

            pages++;
            pageIds.Should().HaveCountLessThanOrEqualTo(pageSize, "a page cannot exceed its page size");

            foreach (string id in pageIds)
            {
                returnedIds.Add(id).Should().BeTrue($"document '{id}' was returned more than once");
            }

            if (nextPageToken is null)
            {
                // The walk ends by being told nothing follows, and the terminal request is the one that
                // selected nothing at all.
                pageIds.Should().BeEmpty("the page that ends a walk selects nothing");
                return new CursorWalk(pages, returnedIds);
            }

            pageToken = nextPageToken;
        }

        throw new InvalidOperationException(
            $"A cursor walk of '{endpoint}' did not terminate within {MaximumWalkedPages} pages."
        );
    }

    private static async Task<int> ContinueWalkAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string pageToken,
        HashSet<string> returnedIds
    )
    {
        var continued = 0;
        string? nextPageToken = pageToken;

        while (nextPageToken is not null && continued < MaximumWalkedPages)
        {
            var (pageIds, followingToken) = await ReadPageAsync(
                harness,
                $"{endpoint}?pageToken={Uri.EscapeDataString(nextPageToken)}&pageSize=2"
            );

            foreach (string id in pageIds)
            {
                returnedIds.Add(id).Should().BeTrue($"document '{id}' was returned more than once");
            }

            continued += pageIds.Count;
            nextPageToken = followingToken;
        }

        return continued;
    }

    /// <summary>
    /// Reads one page, returning the document ids it contains and the continuation it offers. The
    /// continuation is round-tripped through the codec so a token that decoded to a different range
    /// than it names would be caught here rather than surviving as an opaque string.
    /// </summary>
    private static async Task<(List<string> PageIds, string? NextPageToken)> ReadPageAsync(
        ApiIntegrationHarness harness,
        string requestUri
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        List<string> pageIds =
        [
            .. JsonNode.Parse(body)!.AsArray().Select(static document => document!["id"]!.GetValue<string>()),
        ];

        if (!response.Headers.TryGetValues(NextPageTokenHeaderName, out var headerValues))
        {
            return (pageIds, null);
        }

        string nextPageToken = headerValues.Single();
        PageTokenCodec
            .TryDecode(nextPageToken, out var range)
            .Should()
            .BeTrue("an emitted continuation must decode through the codec that produced it");
        range!.InclusiveMinimum.Should().BePositive("a continuation resumes after the keys just selected");

        return (pageIds, nextPageToken);
    }

    private static async Task<string[]> SeedMergeItemsAsync(ApiIntegrationHarness harness, string scenario)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        // The merge item requires a resolvable descriptor reference, so the reference target is created
        // through the same pipeline before the documents that point at it.
        string descriptorNamespace = $"uri://ed-fi.org/SchoolTypeDescriptor/CursorPaging/{scenario}/{suffix}";
        string descriptorCodeValue = $"CursorPaging-{scenario}-{suffix}-ref";
        await CreateAsync(
            harness,
            DescriptorEndpoint,
            new JsonObject
            {
                ["namespace"] = descriptorNamespace,
                ["codeValue"] = descriptorCodeValue,
                ["shortDescription"] = $"CursorPaging {scenario} {suffix} reference",
            }
        );

        List<string> seededIds = [];

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            var payload = new JsonObject
            {
                ["profileRootOnlyMergeItemId"] = UniqueIdentity(suffix, index),
                ["displayName"] = $"CursorPaging {scenario} {suffix} {index}",
                ["primarySchoolTypeDescriptor"] = $"{descriptorNamespace}#{descriptorCodeValue}",
            };

            seededIds.Add(await CreateAsync(harness, MergeItemsEndpoint, payload));
        }

        return [.. seededIds];
    }

    private static async Task<string[]> SeedDescriptorsAsync(ApiIntegrationHarness harness, string scenario)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        List<string> seededIds = [];

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            var payload = new JsonObject
            {
                ["namespace"] = $"uri://ed-fi.org/SchoolTypeDescriptor/CursorPaging/{scenario}/{suffix}",
                ["codeValue"] = $"CursorPaging-{scenario}-{suffix}-{index}",
                ["shortDescription"] = $"CursorPaging {scenario} {suffix} {index}",
            };

            seededIds.Add(await CreateAsync(harness, DescriptorEndpoint, payload));
        }

        return [.. seededIds];
    }

    /// <summary>
    /// A per-run identity that stays inside Int32, because the merge item's identity is an integer and a
    /// collision with a sibling scenario's seed would answer 200 on an update instead of 201.
    /// </summary>
    private static int UniqueIdentity(string suffix, int index) =>
        1_386_000 + Math.Abs(suffix.GetHashCode(StringComparison.Ordinal) % 100_000) * 10 + index;

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

    private sealed record CursorWalk(int Pages, HashSet<string> ReturnedIds);
}

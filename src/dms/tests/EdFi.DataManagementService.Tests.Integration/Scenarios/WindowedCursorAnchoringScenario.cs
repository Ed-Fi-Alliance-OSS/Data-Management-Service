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
using static EdFi.DataManagementService.Tests.Integration.Scenarios.CursorPartitionAuthorizationMatrixSupport;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// A cursor walk over a max-bearing change-version window, end to end against the assembled host. The
/// bounds, the continuation the walk follows, and the anchor stamped on it are all
/// <c>ContentVersion</c>, so what is proven here is that a client handed a windowed token can walk the
/// window to exhaustion and see every member of it exactly once.
///
/// <para>
/// Every walk is bounded on both sides — <c>minChangeVersion</c> just above the collection's high-water
/// mark before seeding, <c>maxChangeVersion</c> at its mark after — which makes the expected result an
/// exact set rather than a superset. The window is still max-bearing, so it still resolves the
/// <c>ContentVersion</c> anchor, and the fixture may hold anything a sibling scenario left behind
/// without any of it reaching these assertions.
/// </para>
///
/// <para>
/// A max-bearing window is a monotonic-escape window: an update pushes a row past the maximum and out
/// of the window entirely, rather than moving it past the anchor while it remains eligible. That is the
/// property the anchor rests on, and the mid-walk mutation cases below are what exercise it against a
/// real database rather than against a described one. The min-only case is its counterpart: an
/// open-ended window has no escape, so it keeps the <c>DocumentId</c> anchor, and the same mid-walk
/// update that is safe here would return a row twice there.
/// </para>
/// </summary>
internal static class WindowedCursorAnchoringScenario
{
    private const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string DescriptorEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    private const string AvailableChangeVersionsEndpoint = "/changeQueries/v1/availableChangeVersions";
    private const string StandardJsonContentType = "application/json";
    private const string NextPageTokenHeaderName = "Next-Page-Token";

    /// <summary>
    /// Enough documents that a page size of two cannot deliver them at once, and enough that a mid-walk
    /// mutation can target a document the walk has not reached yet while leaving others behind it.
    /// </summary>
    private const int SeededDocumentCount = 5;

    /// <summary>
    /// A walk that failed to advance exhausts this and fails with the pages it did retrieve rather than
    /// running forever.
    /// </summary>
    private const int MaximumWalkedPages = 40;

    public static async Task It_walks_a_windowed_regular_resource_collection_exactly_once(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedWindowedMergeItemsAsync(harness, "regular-walk");

        var walk = await WalkWindowAsync(harness, MergeItemsEndpoint, seeded.Window, pageSize: 2);

        walk.Pages.Should().BeGreaterThan(1, "a page size of two cannot deliver five documents at once");
        walk.ReturnedIds.Should().BeEquivalentTo(seeded.Ids);
    }

    public static async Task It_walks_a_windowed_descriptor_collection_exactly_once(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedWindowedDescriptorsAsync(harness, "descriptor-walk");

        var walk = await WalkWindowAsync(harness, DescriptorEndpoint, seeded.Window, pageSize: 2);

        walk.Pages.Should().BeGreaterThan(1, "a page size of two cannot deliver five descriptors at once");
        walk.ReturnedIds.Should().BeEquivalentTo(seeded.Ids);
    }

    /// <summary>
    /// The windowed walk resolves the authorized candidate set, not the whole window. The anchor changes
    /// which column the walk is bounded on; it must not change which rows the walk is allowed to see.
    /// </summary>
    /// <remarks>
    /// The inaccessible documents are interleaved through the seed rather than clustered at one end, so
    /// they fall inside the window's interior and a page boundary lands between them. A walk that
    /// applied its bounds before authorization would return them; one that skipped a page containing
    /// only inaccessible rows would lose the accessible rows after it.
    /// </remarks>
    public static async Task It_excludes_unauthorized_documents_from_a_windowed_walk(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        long floor = await NewestChangeVersionAsync(harness);
        var seeded = await SeedNamespaceResourcesAsync(harness, MatrixAccessibility.Namespace);
        string window = await WindowSinceAsync(harness, floor);

        var walk = await WalkWindowAsync(harness, NamespaceResourcesEndpoint, window, pageSize: 2);

        walk.ReturnedIds.Should().BeEquivalentTo(seeded.AccessibleIds);
        walk.ReturnedIds.Should()
            .NotIntersectWith(
                seeded.InaccessibleIds,
                "a windowed walk resolves the authorized candidate set, not the window"
            );
    }

    /// <summary>
    /// A document updated mid-walk leaves the window instead of moving inside it. Its
    /// <c>ContentVersion</c> rises past <c>maxChangeVersion</c>, so it stops qualifying altogether — and
    /// nothing else shifts, because every other row keeps the version the walk ordered it by.
    /// </summary>
    /// <remarks>
    /// The updated document is the last one seeded, so it carries the highest <c>ContentVersion</c> in
    /// the window and the walk has provably not reached it when the update lands. Under a
    /// <c>DocumentId</c> anchor this same update would leave the row eligible and behind the anchor,
    /// which is why a min-only window keeps that anchor.
    /// </remarks>
    public static async Task It_drops_a_document_updated_past_the_window_maximum_mid_walk(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedWindowedMergeItemsAsync(harness, "escape");
        string escapingId = seeded.Ids[^1];

        var walk = await WalkWindowAsync(
            harness,
            MergeItemsEndpoint,
            seeded.Window,
            pageSize: 2,
            afterFirstPage: () => UpdateMergeItemAsync(harness, seeded, escapingId)
        );

        walk.ReturnedIds.Should()
            .NotContain(
                escapingId,
                "an update pushes the row past the window maximum, so it leaves the window entirely"
            );
        walk.ReturnedIds.Should()
            .BeEquivalentTo(
                seeded.Ids.Where(id => id != escapingId),
                "no other row moves, so the rest of the window is still walked exactly once"
            );
    }

    /// <summary>
    /// Deleting every document a later page would have covered ends those documents' membership in the
    /// window without ending the walk: the pages after them still arrive, exactly once each.
    /// </summary>
    /// <remarks>
    /// This is the deletion a client can actually cause between two requests of a walk. The narrower
    /// case — a delete committing between selection and hydration of one page — cannot be arranged over
    /// HTTP and is covered where the two are separable, in the hydration executor's provider tests.
    /// </remarks>
    public static async Task It_continues_past_a_page_whose_documents_were_deleted_mid_walk(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedWindowedMergeItemsAsync(harness, "deleted-page");

        // The two documents a page size of two would deliver second, so the whole of one page is
        // removed rather than part of one.
        string[] deletedIds = [seeded.Ids[2], seeded.Ids[3]];

        var walk = await WalkWindowAsync(
            harness,
            MergeItemsEndpoint,
            seeded.Window,
            pageSize: 2,
            afterFirstPage: () => DeleteAllAsync(harness, MergeItemsEndpoint, deletedIds)
        );

        walk.ReturnedIds.Should()
            .BeEquivalentTo(
                seeded.Ids.Except(deletedIds),
                "the walk must reach the documents after a deleted page rather than stopping on it"
            );
    }

    /// <summary>
    /// A min-only window keeps the <c>DocumentId</c> anchor, and that is what makes a mid-walk update
    /// safe there. The window is open above, so an update leaves the row eligible; anchored on
    /// <c>ContentVersion</c> the row would move from behind the walk to ahead of it and be returned a
    /// second time, while its <c>DocumentId</c> does not move at all.
    /// </summary>
    /// <remarks>
    /// The updated document is the first one seeded, so the walk has already returned it when the update
    /// lands. That is the arrangement the duplicate hazard needs: a row behind the anchor acquiring the
    /// highest change version in the window. Updating a row the walk had not reached yet would prove
    /// nothing, because such a row is ahead of either anchor before and after the write. Every
    /// continuation is checked for the <c>d</c> marker, which is what proves the anchor rather than
    /// inferring it from the walk having worked.
    /// </remarks>
    public static async Task It_keeps_the_document_id_anchor_for_a_min_only_walk(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedWindowedMergeItemsAsync(harness, "min-only");
        string updatedId = seeded.Ids[0];

        var walk = await WalkWindowAsync(
            harness,
            MergeItemsEndpoint,
            seeded.MinOnlyWindow,
            pageSize: 2,
            afterFirstPage: () => UpdateMergeItemAsync(harness, seeded, updatedId),
            anchor: PageOrderingMode.DocumentId
        );

        walk.ReturnedIds.Should()
            .BeEquivalentTo(
                seeded.Ids,
                "an update inside an open-ended window leaves the row eligible, and a DocumentId anchor "
                    + "does not let it move from behind the walk to ahead of it"
            );
    }

    /// <summary>
    /// A windowed <c>limit</c>/<c>offset</c> response hands out a continuation, and that continuation
    /// really enters the walk: the traditional page and the pages that follow it tile the window exactly
    /// once between them, with no row lost at the join and none delivered twice.
    /// </summary>
    /// <remarks>
    /// This is the seam the two halves of windowed anchoring meet at, and only here. A traditional page
    /// over a max-bearing window is ordered by <c>ContentVersion</c>, so its continuation has to be
    /// stamped and bounded in that column — a token anchored on the page's highest <c>DocumentId</c>
    /// would look identical to a client and skip every row with a smaller id and a later version.
    /// Entering a walk this way is also newly possible: before <c>ContentVersion</c> anchoring, a
    /// windowed traditional page was ordered by a column its token could not express and was served with
    /// no continuation at all.
    /// <para>
    /// The entry page is deliberately not the whole window, so the join is a real one: five documents
    /// and a limit of two leave three rows for the cursor pages to deliver.
    /// </para>
    /// </remarks>
    public static async Task It_enters_a_windowed_walk_from_a_traditional_page(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedWindowedMergeItemsAsync(harness, "traditional-entry");

        // A plain limit/offset request: no pageToken, no pageSize. Read through the same reader the walk
        // uses, so the continuation it carries is held to the same marker rule every walked page is.
        var (entryPageIds, entryPageToken) = await ReadWindowedPageAsync(
            harness,
            $"{MergeItemsEndpoint}?{seeded.Window}&limit=2",
            PageOrderingMode.ContentVersion
        );

        entryPageIds
            .Should()
            .HaveCount(2, "a limit of two over a five-document window returns a partial first page");
        entryPageToken
            .Should()
            .NotBeNull(
                "a windowed traditional page selects keys, so it can hand out a continuation anchored on "
                    + "the ContentVersion it was ordered by"
            );

        var walk = await WalkWindowAsync(
            harness,
            MergeItemsEndpoint,
            seeded.Window,
            pageSize: 2,
            entryPageToken: entryPageToken
        );

        walk.ReturnedIds.Should()
            .NotIntersectWith(
                entryPageIds,
                "the continuation starts after the traditional page rather than replaying it"
            );
        walk.ReturnedIds.Concat(entryPageIds)
            .Should()
            .BeEquivalentTo(
                seeded.Ids,
                "the traditional page and the walk it started tile the window exactly once between them"
            );
    }

    /// <summary>
    /// A partition token is walked by the same cursor path a page token is, so it carries the same
    /// anchor marker and the same replay rule: a windowed token replayed without its window is answered
    /// with the standard invalid-token response rather than with bounds read against the wrong column.
    /// </summary>
    public static async Task It_rejects_a_windowed_partition_token_replayed_without_the_window(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedWindowedMergeItemsAsync(harness, "partition-marker");

        var pageTokens = await ReadPageTokensAsync(
            harness,
            $"{MergeItemsEndpoint}/partitions?number=2&{seeded.Window}"
        );

        pageTokens.Should().NotBeEmpty("the seeded window holds candidates to partition");

        foreach (string pageToken in pageTokens)
        {
            PageTokenCodec
                .TryDecode(pageToken, out _, out PageOrderingMode orderingMode)
                .Should()
                .BeTrue("an emitted partition token must decode through the codec that produced it");
            orderingMode
                .Should()
                .Be(
                    PageOrderingMode.ContentVersion,
                    "a windowed partition's boundaries are calculated over ContentVersion"
                );
        }

        string windowedToken = pageTokens[0];

        // The control first: the same token, replayed under the window it was issued for, is served.
        // Without it the rejection below could come from anything about the token rather than from the
        // window the request dropped.
        using (
            HttpResponseMessage accepted = await harness.HttpClient.GetAsync(
                $"{MergeItemsEndpoint}?pageToken={Uri.EscapeDataString(windowedToken)}&pageSize=2&{seeded.Window}"
            )
        )
        {
            accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        }

        using HttpResponseMessage rejected = await harness.HttpClient.GetAsync(
            $"{MergeItemsEndpoint}?pageToken={Uri.EscapeDataString(windowedToken)}&pageSize=2"
        );

        await AssertInvalidPageTokenAsync(rejected);
    }

    /// <summary>
    /// The standard invalid-token answer, asserted whole: a marker that disagrees with the request's
    /// window is reported exactly as a malformed token is, and tells the client nothing more than that
    /// the token cannot be replayed.
    /// </summary>
    private static async Task AssertInvalidPageTokenAsync(HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);

        JsonNode body = JsonNode.Parse(content)!;

        body["detail"]!.GetValue<string>().Should().Be("Parameters supplied to the request were invalid.");
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter-validation-failed");
        body["title"]!.GetValue<string>().Should().Be("Parameter Validation Failed");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["validationErrors"]!.AsObject().Should().BeEmpty();
        body["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal("The page token provided was invalid.");
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
    /// Walks a window from an entry token to exhaustion, following only the continuations the host hands
    /// back.
    /// </summary>
    /// <param name="afterFirstPage">
    /// Mutations applied once the walk is genuinely in progress — after a page has been served and
    /// before its continuation is replayed — which is the only place a mid-walk write can be observed
    /// from outside the host.
    /// </param>
    /// <param name="anchor">
    /// The anchor <paramref name="window" /> resolves. Stated by the caller rather than derived here,
    /// because it is what the walk is asserting: a window that resolved the other anchor would reject
    /// this walk's entry token instead of silently walking the wrong column.
    /// </param>
    /// <param name="entryPageToken">
    /// The token to enter on, when the walk is being entered from a token the host itself issued rather
    /// than from one covering the whole window. Omitted by every walk that means to cover the window,
    /// which synthesizes an entry token instead.
    /// </param>
    private static async Task<WindowedWalk> WalkWindowAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string window,
        int pageSize,
        Func<Task>? afterFirstPage = null,
        PageOrderingMode anchor = PageOrderingMode.ContentVersion,
        string? entryPageToken = null
    )
    {
        // Encoded by the codec that decodes it, and stamped with the anchor this window resolves, so the
        // entry token is one the host accepts by construction rather than by transcription.
        string pageToken = entryPageToken ?? PageTokenCodec.Encode(CursorRange.From(1), anchor);

        HashSet<string> returnedIds = [];
        var pages = 0;

        while (pages < MaximumWalkedPages)
        {
            var (pageIds, nextPageToken) = await ReadWindowedPageAsync(
                harness,
                $"{endpoint}?pageToken={Uri.EscapeDataString(pageToken)}&pageSize={pageSize}&{window}",
                anchor
            );

            pages++;
            pageIds.Should().HaveCountLessThanOrEqualTo(pageSize, "a page cannot exceed its page size");

            foreach (string id in pageIds)
            {
                returnedIds.Add(id).Should().BeTrue($"document '{id}' was returned more than once");
            }

            if (nextPageToken is null)
            {
                pageIds.Should().BeEmpty("the page that ends a walk selects nothing");
                return new WindowedWalk(pages, returnedIds);
            }

            if (pages == 1 && afterFirstPage is not null)
            {
                await afterFirstPage();
            }

            pageToken = nextPageToken;
        }

        throw new InvalidOperationException(
            $"A windowed cursor walk of '{endpoint}' did not terminate within {MaximumWalkedPages} pages."
        );
    }

    /// <summary>
    /// Reads one page of a windowed walk, asserting that any continuation it carries is anchored the way
    /// the request's window resolves. A token marked otherwise would be rejected on replay, so checking
    /// every page names the page that stopped marking rather than reporting the walk as stalled.
    /// </summary>
    private static async Task<(List<string> PageIds, string? NextPageToken)> ReadWindowedPageAsync(
        ApiIntegrationHarness harness,
        string requestUri,
        PageOrderingMode anchor
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
            .TryDecode(nextPageToken, out _, out PageOrderingMode orderingMode)
            .Should()
            .BeTrue("an emitted continuation must decode through the codec that produced it");
        orderingMode
            .Should()
            .Be(anchor, "a page's continuation is expressed in the column the page was selected by");

        return (pageIds, nextPageToken);
    }

    /// <summary>
    /// The collection's change-version high-water mark, read from the endpoint that publishes it rather
    /// than from the database, so the window a walk is given is one a client could have asked for.
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

    /// <summary>
    /// A window that starts just above <paramref name="floor" /> and ends at the current high-water mark,
    /// so it holds exactly what was written between the two readings. Max-bearing, so it resolves the
    /// <c>ContentVersion</c> anchor, and bounded below, so nothing another scenario seeded can reach the
    /// assertions.
    /// </summary>
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

    private static async Task<SeededWindow> SeedWindowedMergeItemsAsync(
        ApiIntegrationHarness harness,
        string scenario
    )
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        // The merge item requires a resolvable descriptor reference, so the reference target is created
        // before the documents that point at it — and before the window opens, so the descriptor's own
        // change version cannot land inside a window that a merge-item walk is asserted against. It
        // doubles as the fence that makes the floor below a change version that was really assigned
        // rather than the value the next write will take.
        string descriptorNamespace =
            $"uri://ed-fi.org/SchoolTypeDescriptor/WindowedCursor/{scenario}/{suffix}";
        string descriptorCodeValue = $"WindowedCursor-{scenario}-{suffix}-ref";
        await CreateAsync(
            harness,
            DescriptorEndpoint,
            new JsonObject
            {
                ["namespace"] = descriptorNamespace,
                ["codeValue"] = descriptorCodeValue,
                ["shortDescription"] = $"WindowedCursor {scenario} {suffix} reference",
            }
        );

        long floor = await NewestChangeVersionAsync(harness);

        List<string> seededIds = [];
        Dictionary<string, JsonObject> payloadsById = [];

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            var payload = new JsonObject
            {
                ["profileRootOnlyMergeItemId"] = UniqueIdentity(suffix, index),
                ["displayName"] = $"WindowedCursor {scenario} {suffix} {index}",
                ["primarySchoolTypeDescriptor"] = $"{descriptorNamespace}#{descriptorCodeValue}",
            };

            string documentId = await CreateAsync(harness, MergeItemsEndpoint, payload);
            seededIds.Add(documentId);
            payloadsById.Add(documentId, payload);
        }

        return new SeededWindow(seededIds, payloadsById, floor, await WindowSinceAsync(harness, floor));
    }

    private static async Task<SeededWindow> SeedWindowedDescriptorsAsync(
        ApiIntegrationHarness harness,
        string scenario
    )
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        // One write before the floor is read, so the floor is a change version that was really assigned
        // rather than the value the very next write will take. The fence sits below the window and is
        // therefore never part of what the walk is asserted against.
        await CreateAsync(
            harness,
            DescriptorEndpoint,
            new JsonObject
            {
                ["namespace"] = $"uri://ed-fi.org/SchoolTypeDescriptor/WindowedCursor/{scenario}/{suffix}",
                ["codeValue"] = $"WindowedCursor-{scenario}-{suffix}-fence",
                ["shortDescription"] = $"WindowedCursor {scenario} {suffix} fence",
            }
        );

        long floor = await NewestChangeVersionAsync(harness);

        List<string> seededIds = [];
        Dictionary<string, JsonObject> payloadsById = [];

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            var payload = new JsonObject
            {
                ["namespace"] = $"uri://ed-fi.org/SchoolTypeDescriptor/WindowedCursor/{scenario}/{suffix}",
                ["codeValue"] = $"WindowedCursor-{scenario}-{suffix}-{index}",
                ["shortDescription"] = $"WindowedCursor {scenario} {suffix} {index}",
            };

            string documentId = await CreateAsync(harness, DescriptorEndpoint, payload);
            seededIds.Add(documentId);
            payloadsById.Add(documentId, payload);
        }

        return new SeededWindow(seededIds, payloadsById, floor, await WindowSinceAsync(harness, floor));
    }

    /// <summary>
    /// A per-run identity that stays inside Int32, because the merge item's identity is an integer and a
    /// collision with a sibling scenario's seed would answer 200 on an update instead of 201.
    /// </summary>
    private static int UniqueIdentity(string suffix, int index) =>
        1_394_000 + Math.Abs(suffix.GetHashCode(StringComparison.Ordinal) % 100_000) * 10 + index;

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
    /// Rewrites one seeded document through the same pipeline that created it, changing a non-identity
    /// field so the write is a content change and raises the document's <c>ContentVersion</c>.
    /// </summary>
    private static async Task UpdateMergeItemAsync(
        ApiIntegrationHarness harness,
        SeededWindow seeded,
        string documentId
    )
    {
        JsonObject payload = seeded.PayloadOf(documentId).DeepClone().AsObject();
        payload["id"] = documentId;
        payload["displayName"] = $"{payload["displayName"]!.GetValue<string>()} updated";

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PutAsync(
            $"{MergeItemsEndpoint}/{documentId}",
            content
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"PUT body: {body}");
    }

    private static async Task DeleteAllAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        IEnumerable<string> documentIds
    )
    {
        foreach (string documentId in documentIds)
        {
            using HttpResponseMessage response = await harness.HttpClient.DeleteAsync(
                $"{endpoint}/{documentId}"
            );
            string body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"DELETE {documentId} body: {body}");
        }
    }

    /// <summary>
    /// The documents a walk is asserted against and the window that holds exactly them. The payloads are
    /// kept so a mid-walk update can rewrite a document without reading it back, which would otherwise
    /// be an extra request inside the window it is asserting on.
    /// </summary>
    private sealed record SeededWindow(
        IReadOnlyList<string> Ids,
        IReadOnlyDictionary<string, JsonObject> PayloadsById,
        long Floor,
        string Window
    )
    {
        /// <summary>
        /// The same seed bounded only from below. Nothing is written after the seed except by the walk's
        /// own mutations, so this holds the same documents the two-sided window does — while resolving
        /// the other anchor, which is the whole point of reading it.
        /// </summary>
        public string MinOnlyWindow =>
            string.Create(CultureInfo.InvariantCulture, $"minChangeVersion={Floor + 1}");

        public JsonObject PayloadOf(string documentId) => PayloadsById[documentId];
    }

    private sealed record WindowedWalk(int Pages, HashSet<string> ReturnedIds);
}

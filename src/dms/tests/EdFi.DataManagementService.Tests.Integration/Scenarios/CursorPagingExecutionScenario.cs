// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Telemetry;
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

    /// <summary>
    /// The database commands one cursor page is allowed to cost.
    /// </summary>
    /// <remarks>
    /// An absolute literal from the design rather than a figure captured from a run: a baseline captured
    /// from this same build would carry whatever extra command the instrumentation added, so both sides
    /// would move together and the assertion could never fail for the reason it exists. The design fixes
    /// it instead — a cursor page uses the existing single-command page-keyset architecture and adds no
    /// database command, transaction, or roundtrip. The fixtures binding this scenario grant
    /// <c>NoFurtherAuthorizationRequired</c>, so the design's one documented exception — a view-based
    /// authorization strategy whose custom-view validation probe runs as a second command — does not
    /// apply, and raising this number is a visible, deliberate edit in review.
    /// </remarks>
    private const int CursorPageDatabaseCommands = 1;

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

    /// <summary>
    /// What the collection-paging metric reports for a real cursor read, and what that read is allowed to
    /// cost. The provider dimension is proven from a live connection rather than from a dialect literal,
    /// which is the only place it can be: nothing in Core can tell which engine actually answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both resource kinds are read, because the seam that carries the single command differs between
    /// them — a regular-resource page is a hydration while a descriptor page is a command-executor
    /// command — and the quantity the design constrains is the total, not the split. Asserting the split
    /// would pass on one kind and fail on the other for a reason that is not a defect.
    /// </para>
    /// <para>
    /// The command counters are snapshotted immediately around each asserted request, so the seeding
    /// traffic above is excluded. That is request isolation, not a baseline: the expected value is the
    /// design literal and is never read from a run.
    /// </para>
    /// </remarks>
    public static async Task It_emits_bounded_telemetry_across_a_cursor_walk(
        ApiIntegrationHarness harness,
        string expectedProvider
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProvider);

        ApiIntegrationQueryRecorder recorder =
            harness.QueryRecorder
            ?? throw new InvalidOperationException(
                "This scenario counts database commands and requires CaptureQueryPlans."
            );

        await SeedMergeItemsAsync(harness, "telemetry");
        await SeedDescriptorsAsync(harness, "telemetry");

        // Encoded by the codec that decodes it, so the entry token is decodable by construction.
        string entryToken = PageTokenCodec.Encode(CursorRange.From(1));
        string tokenParameter = $"pageToken={Uri.EscapeDataString(entryToken)}&pageSize=2";

        using var metrics = CollectionPagingMetricCollector.Start();

        // A regular-resource cursor page, asserted on the raw response so the instrumentation is shown to
        // change nothing a client can observe: same status, same media type, a continuation, and no
        // Total-Count on a request that asked for no count.
        metrics.Clear();
        int commandsBefore = recorder.DatabaseCommands;

        using (
            HttpResponseMessage response = await harness.HttpClient.GetAsync(
                $"{MergeItemsEndpoint}?{tokenParameter}"
            )
        )
        {
            string body = await response.Content.ReadAsStringAsync();
            int databaseCommands = recorder.DatabaseCommands - commandsBefore;

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            response.Content.Headers.ContentType!.MediaType.Should().Be(StandardJsonContentType);
            response.Headers.Contains("Total-Count").Should().BeFalse();
            response.Headers.GetValues(NextPageTokenHeaderName).Should().ContainSingle();
            JsonNode.Parse(body)!.AsArray().Should().HaveCount(2);

            databaseCommands
                .Should()
                .Be(
                    CursorPageDatabaseCommands,
                    "a cursor page adds no database command, and the metric describing it must not "
                        + "be answered with an extra query"
                );
        }

        metrics.AssertSinglePage(
            expectedProvider,
            CollectionPagingTelemetryLabel.CursorPagingMode,
            CollectionPagingTelemetryLabel.PageCommandCategory,
            CollectionPagingTelemetryLabel.SuccessOutcome,
            expectedRequestedPageSize: 2,
            expectedReturnedPageSize: 2
        );

        // The descriptor page: the same one command, carried by the other seam.
        metrics.Clear();
        commandsBefore = recorder.DatabaseCommands;

        var descriptorPage = await ReadPageAsync(harness, $"{DescriptorEndpoint}?{tokenParameter}");

        (recorder.DatabaseCommands - commandsBefore).Should().Be(CursorPageDatabaseCommands);
        descriptorPage.PageIds.Should().HaveCount(2);
        metrics.AssertSinglePage(
            expectedProvider,
            CollectionPagingTelemetryLabel.CursorPagingMode,
            CollectionPagingTelemetryLabel.PageCommandCategory,
            CollectionPagingTelemetryLabel.SuccessOutcome,
            expectedRequestedPageSize: 2,
            expectedReturnedPageSize: 2
        );

        // Walking to the end. Each page is classified from what the response itself did with the
        // continuation, so the page that ends the walk is the one — and the only one — reported as
        // terminal, and no page of the walk costs more than one command however deep it is.
        string? pageToken = entryToken;
        CollectionPagingMeasurement? lastPage = null;
        var walkedPages = 0;

        while (pageToken is not null && walkedPages < MaximumWalkedPages)
        {
            metrics.Clear();
            commandsBefore = recorder.DatabaseCommands;

            var walked = await ReadPageAsync(
                harness,
                $"{MergeItemsEndpoint}?pageToken={Uri.EscapeDataString(pageToken)}&pageSize=2"
            );

            (recorder.DatabaseCommands - commandsBefore).Should().Be(CursorPageDatabaseCommands);

            walkedPages++;
            lastPage = metrics.AssertSinglePage(
                expectedProvider,
                CollectionPagingTelemetryLabel.CursorPagingMode,
                CollectionPagingTelemetryLabel.PageCommandCategory,
                walked.NextPageToken is null
                    ? CollectionPagingTelemetryLabel.TerminalPageOutcome
                    : CollectionPagingTelemetryLabel.SuccessOutcome,
                expectedRequestedPageSize: 2,
                expectedReturnedPageSize: walked.PageIds.Count
            );

            pageToken = walked.NextPageToken;
        }

        pageToken
            .Should()
            .BeNull($"a cursor walk of '{MergeItemsEndpoint}' must end within {MaximumWalkedPages} pages");
        lastPage!
            .Outcome.Should()
            .Be(
                CollectionPagingTelemetryLabel.TerminalPageOutcome,
                "a walk that ran out of documents ended, and the metric must say so rather than "
                    + "reporting one more served page"
            );
    }

    /// <summary>
    /// The one outcome whose name is a claim about database work. <c>early_empty</c> reports that the API
    /// answered without issuing a selection command, and this is where that claim is measured rather than
    /// stated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A descriptor <c>id</c> filter carrying something that is not a UUID cannot match any row, and the
    /// API determines that from the value alone. So the page is answered with no candidate selection at
    /// all, which is what makes zero the expected count here where every other telemetry case in this
    /// suite costs exactly one. A later change that answered this short-circuit with a real command — an
    /// added probe, a reordered authorization check — would keep every other assertion in this suite
    /// green while making the outcome name false, and this is the only case that would fail.
    /// </para>
    /// <para>
    /// The collection is seeded first even though the filter matches none of it. Without the seed the
    /// empty page and the zero count would both be statements about an empty database rather than about
    /// the short-circuit, which is the property under test.
    /// </para>
    /// <para>
    /// The descriptor endpoint carries this case because it is the one this fixture's ApiSchema gives an
    /// <c>id</c> query field to. The regular resource here declares no query fields at all, so the same
    /// request against it would be refused as an unknown field and never reach a selection decision.
    /// </para>
    /// </remarks>
    public static async Task It_records_an_early_empty_without_a_database_command(
        ApiIntegrationHarness harness,
        string expectedProvider
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProvider);

        ApiIntegrationQueryRecorder recorder =
            harness.QueryRecorder
            ?? throw new InvalidOperationException(
                "This scenario counts database commands and requires CaptureQueryPlans."
            );

        await SeedDescriptorsAsync(harness, "early-empty");

        // Encoded by the codec that decodes it, so the entry token is decodable by construction.
        string entryToken = PageTokenCodec.Encode(CursorRange.From(1));

        using var metrics = CollectionPagingMetricCollector.Start();

        metrics.Clear();
        int commandsBefore = recorder.DatabaseCommands;

        using (
            HttpResponseMessage response = await harness.HttpClient.GetAsync(
                $"{DescriptorEndpoint}?pageToken={Uri.EscapeDataString(entryToken)}&pageSize=2"
                    + "&id=not-a-uuid"
            )
        )
        {
            string body = await response.Content.ReadAsStringAsync();
            int databaseCommands = recorder.DatabaseCommands - commandsBefore;

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            response.Content.Headers.ContentType!.MediaType.Should().Be(StandardJsonContentType);
            JsonNode.Parse(body)!.AsArray().Should().BeEmpty();

            // Nothing was selected, so there is no key to anchor a continuation on and the walk ends
            // here. A short-circuit that offered a continuation would send a client back for a page the
            // API already knows cannot exist.
            response.Headers.Contains(NextPageTokenHeaderName).Should().BeFalse();

            databaseCommands
                .Should()
                .Be(
                    0,
                    "early_empty reports that no selection command was issued, so any count above zero "
                        + "would make the outcome name false"
                );
        }

        metrics.AssertSinglePage(
            expectedProvider,
            CollectionPagingTelemetryLabel.CursorPagingMode,
            CollectionPagingTelemetryLabel.NoCommandCategory,
            CollectionPagingTelemetryLabel.EarlyEmptyOutcome,
            expectedRequestedPageSize: 2,
            expectedReturnedPageSize: 0
        );
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

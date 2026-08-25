// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Telemetry;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// What a client actually receives from the partitions endpoint on the assembled host, and what it can
/// do with it.
///
/// <para>
/// Middleware tests can show the body a validation fault produces and provider tests can show the
/// identifiers a boundary statement selects, but neither shows that a token this host emitted is a
/// token this host accepts. Every walk below only ever follows a token the partitions response handed
/// it, so a token the handler, the codec, request validation, and page selection did not agree on
/// would return nothing and fail.
/// </para>
///
/// <para>
/// Each test leases its own database, so a walk sees only what it seeded. The assertions are still
/// written as containment and uniqueness rather than exact equality, because the partition count a seed
/// produces depends on the configured sizing, and pinning it would make the sizing rule an assumption of
/// every walk instead of the subject of the one test that asserts it.
/// </para>
/// </summary>
internal static class PartitionEndpointScenario
{
    private const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string MergeItemsPartitionsEndpoint = $"{MergeItemsEndpoint}/partitions";
    private const string DescriptorEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    private const string DescriptorPartitionsEndpoint = $"{DescriptorEndpoint}/partitions";
    private const string StandardJsonContentType = "application/json";

    /// <summary>
    /// The maximum page size the fixtures binding this scenario must configure their host with.
    /// </summary>
    /// <remarks>
    /// The mandatory minimum partition size is <c>MaximumPageSize * 5</c>, so at the deployed value of
    /// 500 no collection this scenario could seed over HTTP would ever be cut into more than one
    /// partition, and every multi-partition assertion below would pass vacuously. Lowering the page size
    /// to two puts the minimum at ten rows, which a seed of
    /// <see cref="SeededDocumentCount" /> documents clears. The wrappers read this constant rather than
    /// restating it, so the sizing this scenario depends on cannot drift from the host it runs against.
    /// </remarks>
    internal const int HostMaximumPageSize = 2;

    /// <summary>
    /// Enough documents to exceed the minimum partition size that <see cref="HostMaximumPageSize" />
    /// implies, so a request for three partitions really produces three and the disjointness assertion
    /// runs across separate ranges rather than inside one.
    /// </summary>
    private const int SeededDocumentCount = 25;

    /// <summary>
    /// The database commands one <c>/partitions</c> request is allowed to cost.
    /// </summary>
    /// <remarks>
    /// An absolute literal from the design rather than a figure captured from a run: a baseline captured
    /// from this same build would carry whatever extra command the instrumentation added, so both sides
    /// would move together and the assertion could never fail for the reason it exists. The design fixes
    /// it instead — the endpoint performs exactly one database command for its boundary selection,
    /// returns identifiers only, and hydrates nothing. The fixtures binding this scenario grant
    /// <c>NoFurtherAuthorizationRequired</c>, so the design's one documented exception — a view-based
    /// authorization strategy whose custom-view validation probe runs first as a second command — does
    /// not apply, and raising this number is a visible, deliberate edit in review.
    /// </remarks>
    private const int PartitionsDatabaseCommands = 1;

    /// <summary>
    /// The partition count the telemetry cases request. Stated rather than omitted so the requested-count
    /// measurement is a property of the request instead of a property of the host's configured default.
    /// </summary>
    private const int RequestedPartitionCount = 3;

    public static async Task It_covers_a_regular_resource_collection_across_its_partitions(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await SeedMergeItemsAsync(harness, "coverage");

        var pageTokens = await ReadPageTokensAsync(harness, $"{MergeItemsPartitionsEndpoint}?number=3");

        pageTokens
            .Should()
            .HaveCountGreaterThan(
                1,
                "the seed exceeds the minimum partition size, so the collection really is cut into "
                    + "several partitions and the disjointness assertion below runs across separate ranges"
            );

        var returnedIds = await WalkEveryPartitionAsync(harness, MergeItemsEndpoint, pageTokens);

        returnedIds
            .Should()
            .Contain(seededIds, "every seeded document belongs to exactly one of the partitions");
    }

    public static async Task It_covers_a_descriptor_collection_across_its_partitions(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedDescriptorsAsync(harness, "coverage");

        var pageTokens = await ReadPageTokensAsync(harness, $"{DescriptorPartitionsEndpoint}?number=3");

        pageTokens.Should().HaveCountGreaterThan(1);

        var returnedIds = await WalkEveryPartitionAsync(harness, DescriptorEndpoint, pageTokens);

        returnedIds.Should().Contain(seeded.Ids);
    }

    /// <summary>
    /// The count a client asks for is an upper bound, not a promise: a collection cannot be cut into
    /// more partitions than it has rows, and the configured minimum partition size reduces it further.
    /// </summary>
    public static async Task It_never_returns_more_partitions_than_requested(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        await SeedMergeItemsAsync(harness, "count-bound");

        var pageTokens = await ReadPageTokensAsync(harness, $"{MergeItemsPartitionsEndpoint}?number=2");

        pageTokens
            .Should()
            .HaveCountGreaterThan(
                1,
                "the seed is large enough for the requested count to be reachable, so the bound below "
                    + "is not satisfied merely by there being one partition"
            );
        pageTokens.Should().HaveCountLessThanOrEqualTo(2);
    }

    /// <summary>
    /// A resource-property filter narrows the candidate set the boundaries are calculated over, exactly
    /// as it narrows a page. Walking the partitions of a filtered request must therefore reach the
    /// matching document and nothing else this scenario seeded.
    /// </summary>
    public static async Task It_partitions_only_the_filtered_candidate_set(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await SeedDescriptorsAsync(harness, "filtered");
        string filter = $"codeValue={Uri.EscapeDataString(seeded.CodeValues[0])}";

        var pageTokens = await ReadPageTokensAsync(harness, $"{DescriptorPartitionsEndpoint}?{filter}");

        // The boundaries themselves, not just the documents the walk reaches. Boundaries calculated
        // over the unfiltered collection would still deliver only the matching document once the walk
        // applies the filter to every range, so the containment assertions below cannot tell a filtered
        // candidate set from an unfiltered one. The count can: one candidate is one boundary.
        pageTokens
            .Should()
            .HaveCount(
                1,
                "the filter leaves a single candidate, so the boundaries are calculated over one row"
            );

        var returnedIds = await WalkEveryPartitionAsync(
            harness,
            $"{DescriptorEndpoint}?{filter}",
            pageTokens
        );

        returnedIds.Should().Contain(seeded.Ids[0]);
        returnedIds
            .Should()
            .NotContain(seeded.Ids.Skip(1), "the filter excludes every other seeded descriptor");
    }

    /// <summary>
    /// A boundary set is not a page: it carries no total count and no successor, and it is served as
    /// plain JSON whatever profile might apply to the collection it describes.
    /// </summary>
    public static async Task It_serves_plain_json_without_paging_headers(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        await SeedMergeItemsAsync(harness, "headers");

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(MergeItemsPartitionsEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be(StandardJsonContentType);
        response.Headers.Contains("Total-Count").Should().BeFalse();
        response.Headers.Contains("Next-Page-Token").Should().BeFalse();
        JsonNode.Parse(body)!.AsObject().Should().ContainKey("pageTokens");
    }

    /// <summary>
    /// An empty candidate set is a partitionable one: it simply has no boundaries. The response shape
    /// does not change, so a client can walk zero tokens without special-casing anything.
    /// </summary>
    /// <remarks>
    /// The collection is seeded first even though the filter matches none of it. Without the seed the
    /// leased database might hold no descriptors at all, and the empty token array would be a statement
    /// about the database rather than about the filter — which is the property under test.
    /// </remarks>
    public static async Task It_returns_an_empty_token_array_for_a_filter_matching_nothing(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        await SeedDescriptorsAsync(harness, "empty-filter");

        var pageTokens = await ReadPageTokensAsync(
            harness,
            $"{DescriptorPartitionsEndpoint}?codeValue=Partitions-none-{Guid.NewGuid():N}"
        );

        pageTokens.Should().BeEmpty();
    }

    /// <summary>
    /// The partitions route is read-only. A write method reaches the pipeline dispatch selected for it
    /// and is refused there with the Allow set this route really serves, which names GET alone rather
    /// than the collection's methods.
    /// </summary>
    public static async Task It_refuses_write_methods_with_a_get_only_allow_header(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var postContent = new StringContent("{}", Encoding.UTF8, StandardJsonContentType);
        using var putContent = new StringContent("{}", Encoding.UTF8, StandardJsonContentType);

        await AssertMethodNotAllowedAsync(
            await harness.HttpClient.PostAsync(MergeItemsPartitionsEndpoint, postContent)
        );
        await AssertMethodNotAllowedAsync(
            await harness.HttpClient.PutAsync(MergeItemsPartitionsEndpoint, putContent)
        );
        await AssertMethodNotAllowedAsync(await harness.HttpClient.DeleteAsync(MergeItemsPartitionsEndpoint));
    }

    /// <summary>
    /// The paging parameters belong to the collection read, so a client that confused the two endpoints
    /// is told which parameter does not apply rather than being told it is an unknown query field.
    /// </summary>
    public static async Task It_refuses_a_reserved_paging_parameter(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{MergeItemsPartitionsEndpoint}?limit=5"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal("The 'limit' parameter is not supported by the partitions endpoint.");
    }

    public static async Task It_refuses_a_partition_count_outside_the_supported_range(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{MergeItemsPartitionsEndpoint}?number=0"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal("Number of partitions must be between 1 and 200.");
    }

    /// <summary>
    /// Activating the partitions route does not widen what a third path segment may be. An unrecognized
    /// one is still the mistyped-document-id answer, and a fourth segment is still no route at all.
    /// </summary>
    public static async Task It_leaves_the_neighbouring_route_shapes_unchanged(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        using HttpResponseMessage unknownSegment = await harness.HttpClient.GetAsync(
            $"{MergeItemsEndpoint}/notauuid"
        );
        string unknownSegmentBody = await unknownSegment.Content.ReadAsStringAsync();

        unknownSegment.StatusCode.Should().Be(HttpStatusCode.BadRequest, unknownSegmentBody);
        JsonNode.Parse(unknownSegmentBody)!["validationErrors"]!.AsObject().Should().ContainKey("$.id");

        using HttpResponseMessage fourthSegment = await harness.HttpClient.GetAsync(
            $"{MergeItemsPartitionsEndpoint}/extra"
        );

        fourthSegment.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// What the collection-paging metric reports for a real boundary request, and what that request is
    /// allowed to cost. The provider dimension is proven from a live connection rather than from a
    /// dialect literal, which is the only place it can be: nothing in Core can tell which engine actually
    /// answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both resource kinds are read, because the seam that carries the single command differs between
    /// them — a descriptor boundary command goes through the command executor while a regular-resource
    /// one does not hydrate at all — and the quantity the design constrains is the total, not the split.
    /// On this endpoint the hydration count is always zero, which is exactly why counting hydrations
    /// alone would prove nothing here.
    /// </para>
    /// <para>
    /// The command counters are snapshotted immediately around each asserted request, so the seeding
    /// traffic above is excluded. That is request isolation, not a baseline: the expected value is the
    /// design literal and is never read from a run.
    /// </para>
    /// </remarks>
    public static async Task It_emits_bounded_telemetry_for_partition_requests(
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

        using var metrics = CollectionPagingMetricCollector.Start();

        // A boundary request over a regular resource, asserted on the raw response so the instrumentation
        // is shown to change nothing a client can observe: a boundary set is still plain JSON with no
        // paging headers.
        metrics.Clear();
        int commandsBefore = recorder.DatabaseCommands;

        using (
            HttpResponseMessage response = await harness.HttpClient.GetAsync(
                $"{MergeItemsPartitionsEndpoint}?number={RequestedPartitionCount}"
            )
        )
        {
            string body = await response.Content.ReadAsStringAsync();
            int databaseCommands = recorder.DatabaseCommands - commandsBefore;

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            response.Content.Headers.ContentType!.MediaType.Should().Be(StandardJsonContentType);
            response.Headers.Contains("Total-Count").Should().BeFalse();
            response.Headers.Contains("Next-Page-Token").Should().BeFalse();

            int returnedPartitionCount = JsonNode.Parse(body)!["pageTokens"]!.AsArray().Count;
            returnedPartitionCount
                .Should()
                .BeGreaterThan(
                    1,
                    "the seed exceeds the minimum partition size, so the returned-count measurement "
                        + "below describes a real multi-partition plan"
                );

            databaseCommands
                .Should()
                .Be(
                    PartitionsDatabaseCommands,
                    "the partitions endpoint performs exactly one boundary command and hydrates "
                        + "nothing, and the metric describing it must not be answered with an extra query"
                );

            metrics.AssertSinglePartitions(
                expectedProvider,
                CollectionPagingTelemetryLabel.BoundaryCommandCategory,
                CollectionPagingTelemetryLabel.SuccessOutcome,
                RequestedPartitionCount,
                returnedPartitionCount
            );
        }

        // A candidate set a filter emptied is a boundary command that ran and found nothing, not a
        // selection that was skipped: the command is still issued, so the outcome is success with a
        // returned count of zero rather than early_empty.
        metrics.Clear();
        commandsBefore = recorder.DatabaseCommands;

        var emptyPageTokens = await ReadPageTokensAsync(
            harness,
            $"{DescriptorPartitionsEndpoint}?number={RequestedPartitionCount}"
                + $"&codeValue=Partitions-telemetry-none-{Guid.NewGuid():N}"
        );

        (recorder.DatabaseCommands - commandsBefore).Should().Be(PartitionsDatabaseCommands);
        emptyPageTokens.Should().BeEmpty();
        metrics.AssertSinglePartitions(
            expectedProvider,
            CollectionPagingTelemetryLabel.BoundaryCommandCategory,
            CollectionPagingTelemetryLabel.SuccessOutcome,
            RequestedPartitionCount,
            expectedReturnedPartitionCount: 0
        );
    }

    /// <summary>
    /// The one outcome whose name is a claim about database work. <c>early_empty</c> reports that the API
    /// answered without issuing a boundary command, and this is where the partitions side of that claim is
    /// measured rather than stated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The GET-many twin of this case lives in <see cref="CursorPagingExecutionScenario" />. Both are
    /// needed because the two short-circuits are separate code — a partitions request prepares through its
    /// own path and answers with its own result type — and only the collection read's was measured. A
    /// regression in this one reports the request as an executed boundary command instead: <c>success</c>
    /// with <c>command_category=boundary</c>, claiming database work that never happened and filing a
    /// duration under the boundary shape. Every other assertion in this suite stays green through that,
    /// because they all describe requests where the command really did run — including the emptied
    /// candidate set above, which is this case's executed twin and is exactly what makes the distinction
    /// worth measuring instead of asserting from a handed-in result in a unit test.
    /// </para>
    /// <para>
    /// A descriptor <c>id</c> filter carrying something that is not a UUID cannot match any row, and the
    /// API determines that from the value alone, so the boundaries are answered with no candidate
    /// selection at all. That is what makes zero the expected count here where every other telemetry case
    /// in this suite costs exactly one.
    /// </para>
    /// <para>
    /// The collection is seeded first even though the filter matches none of it. Without the seed the
    /// empty token array and the zero count would both be statements about an empty database rather than
    /// about the short-circuit, which is the property under test.
    /// </para>
    /// <para>
    /// The descriptor endpoint carries this case because it is the one this fixture's ApiSchema gives an
    /// <c>id</c> query field to. The regular resource here declares no query fields at all, so the same
    /// request against it would be refused as an unknown field and never reach a selection decision.
    /// </para>
    /// </remarks>
    public static async Task It_records_an_early_empty_partition_request_without_a_database_command(
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

        using var metrics = CollectionPagingMetricCollector.Start();

        metrics.Clear();
        int commandsBefore = recorder.DatabaseCommands;

        using (
            HttpResponseMessage response = await harness.HttpClient.GetAsync(
                $"{DescriptorPartitionsEndpoint}?number={RequestedPartitionCount}&id=not-a-uuid"
            )
        )
        {
            string body = await response.Content.ReadAsStringAsync();
            int databaseCommands = recorder.DatabaseCommands - commandsBefore;

            // The response shape does not change for a short-circuit: a client walking zero tokens
            // special-cases nothing, and the boundary set is still plain JSON with no paging headers.
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            response.Content.Headers.ContentType!.MediaType.Should().Be(StandardJsonContentType);
            response.Headers.Contains("Total-Count").Should().BeFalse();
            response.Headers.Contains("Next-Page-Token").Should().BeFalse();
            JsonNode.Parse(body)!["pageTokens"]!.AsArray().Should().BeEmpty();

            databaseCommands
                .Should()
                .Be(
                    0,
                    "early_empty reports that no boundary command was issued, so any count above zero "
                        + "would make the outcome name false"
                );
        }

        metrics.AssertSinglePartitions(
            expectedProvider,
            CollectionPagingTelemetryLabel.NoCommandCategory,
            CollectionPagingTelemetryLabel.EarlyEmptyOutcome,
            RequestedPartitionCount,
            expectedReturnedPartitionCount: 0
        );
    }

    /// <summary>
    /// What a <c>partitions</c> request the API refuses contributes: one count, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The GET-many twin of this case lives in <see cref="CursorPagingExecutionScenario" />. Both are
    /// needed because the two rejections come from separate steps: this pipeline swaps in its own
    /// validating middleware, which owns the partition count and reports the partition mode on every
    /// exit, and a wiring change that left it uncounted would be invisible to the GET-many proof.
    /// </para>
    /// <para>
    /// The provider is why this runs end to end rather than as a unit test. It is read from the resolved
    /// mapping set, and the documentation publishes <c>unknown</c> as a server assembly fault that is not
    /// a bucket to chart — a claim resting entirely on mapping-set resolution running ahead of this step
    /// in the composed pipeline. The middleware's own tests assign that mapping set themselves.
    /// </para>
    /// <para>
    /// No seeding: a rejection is answered before any collection is read, so what the collection holds
    /// cannot change the measurement, and the zero-command assertion says exactly that.
    /// </para>
    /// </remarks>
    public static async Task It_records_a_partition_validation_rejection_without_reaching_the_backend(
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

        using var metrics = CollectionPagingMetricCollector.Start();

        metrics.Clear();
        int commandsBefore = recorder.DatabaseCommands;

        using (
            HttpResponseMessage response = await harness.HttpClient.GetAsync(
                $"{MergeItemsPartitionsEndpoint}?number=0"
            )
        )
        {
            string body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            (recorder.DatabaseCommands - commandsBefore)
                .Should()
                .Be(
                    0,
                    "partition validation answers the request before the handler runs, so a rejection "
                        + "reaches no backend seam at all"
                );
        }

        metrics.AssertSingleValidationRejection(
            expectedProvider,
            CollectionPagingTelemetryLabel.PartitionPagingMode
        );
    }

    private static async Task AssertMethodNotAllowedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, body);
            response
                .Content.Headers.Allow.Should()
                .Equal(
                    new[] { "GET" },
                    "the partitions route serves GET alone, and advertising the collection's set would "
                        + "name the very method being refused"
                );
        }
    }

    /// <summary>
    /// Reads a partitions response and returns its tokens. The response body is the only source of
    /// tokens in this scenario, so nothing here can walk a range the endpoint did not hand out.
    /// </summary>
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
    /// Walks every partition to exhaustion and returns the union of the document ids they delivered,
    /// failing if any document is returned twice across all of them. Partitions are disjoint by
    /// construction, so a duplicate means two ranges overlapped.
    /// </summary>
    private static async Task<HashSet<string>> WalkEveryPartitionAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        IReadOnlyList<string> pageTokens
    )
    {
        char separator = collectionEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        int collectionCount = await ReadTotalCountAsync(harness, collectionEndpoint, separator);
        int maximumWalkedPages = PagesToCover(collectionCount);
        HashSet<string> returnedIds = [];

        foreach (string partitionToken in pageTokens)
        {
            string? pageToken = partitionToken;

            for (var page = 0; page < maximumWalkedPages; page++)
            {
                using HttpResponseMessage response = await harness.HttpClient.GetAsync(
                    $"{collectionEndpoint}{separator}pageToken={Uri.EscapeDataString(pageToken!)}"
                        + $"&pageSize={HostMaximumPageSize}"
                );
                string body = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.OK, body);

                foreach (JsonNode? document in JsonNode.Parse(body)!.AsArray())
                {
                    returnedIds
                        .Add(document!["id"]!.GetValue<string>())
                        .Should()
                        .BeTrue("partitions are disjoint, so no document may be returned twice");
                }

                if (!response.Headers.TryGetValues("Next-Page-Token", out var nextPageTokenValues))
                {
                    pageToken = null;
                    break;
                }

                pageToken = nextPageTokenValues.Single();
            }

            pageToken
                .Should()
                .BeNull(
                    $"a walk of one partition of '{collectionEndpoint}' must terminate, and no partition "
                        + $"holds more than the {collectionCount} documents the collection itself holds"
                );
        }

        return returnedIds;
    }

    /// <summary>
    /// The number of documents the collection this walk covers currently holds, filter included. Read
    /// from the collection rather than from the seed so the bound it feeds tracks whatever the leased
    /// database holds, and so a fixture that starts seeding this collection changes the reported count
    /// instead of turning a walk into a non-terminating one.
    /// </summary>
    private static async Task<int> ReadTotalCountAsync(
        ApiIntegrationHarness harness,
        string collectionEndpoint,
        char separator
    )
    {
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
    /// The pages a walk of one partition may take before it has failed to advance. A partition cannot
    /// hold more rows than the whole collection does, so the pages that cover the collection bound it,
    /// plus one for the empty page a full final page provokes and one for slack against an insert
    /// landing between the count and the walk.
    /// </summary>
    private static int PagesToCover(int documentCount) =>
        ((documentCount + HostMaximumPageSize - 1) / HostMaximumPageSize) + 2;

    private static async Task<string[]> SeedMergeItemsAsync(ApiIntegrationHarness harness, string scenario)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        // The merge item requires a resolvable descriptor reference, so the reference target is created
        // through the same pipeline before the documents that point at it.
        string descriptorNamespace = $"uri://ed-fi.org/SchoolTypeDescriptor/Partitions/{scenario}/{suffix}";
        string descriptorCodeValue = $"Partitions-{scenario}-{suffix}-ref";

        await CreateAsync(
            harness,
            DescriptorEndpoint,
            new JsonObject
            {
                ["namespace"] = descriptorNamespace,
                ["codeValue"] = descriptorCodeValue,
                ["shortDescription"] = $"Partitions {scenario} {suffix} reference",
            }
        );

        List<string> seededIds = [];

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            var payload = new JsonObject
            {
                ["profileRootOnlyMergeItemId"] = UniqueIdentity(suffix, index),
                ["displayName"] = $"Partitions {scenario} {suffix} {index}",
                ["primarySchoolTypeDescriptor"] = $"{descriptorNamespace}#{descriptorCodeValue}",
            };

            seededIds.Add(await CreateAsync(harness, MergeItemsEndpoint, payload));
        }

        return [.. seededIds];
    }

    /// <summary>
    /// A per-run identity inside Int32, because the merge item's identity is an integer and a collision
    /// with a sibling scenario's seed would answer 200 on an update instead of 201.
    /// </summary>
    /// <remarks>
    /// The stride between runs is wider than <see cref="SeededDocumentCount" />, so a run's indices stay
    /// inside the slot its suffix selected. A stride narrower than the seed would let one run's later
    /// indices spill into the next slot and reintroduce the collision this exists to avoid.
    /// </remarks>
    private static int UniqueIdentity(string suffix, int index) =>
        1_387_000 + Math.Abs(suffix.GetHashCode(StringComparison.Ordinal) % 100_000) * 1_000 + index;

    /// <summary>
    /// The seeded descriptors' identities and their code values. The code values are returned because
    /// they are what the filtered cases narrow on, and re-deriving them at the assertion site would let
    /// the filter and the seed drift apart.
    /// </summary>
    private sealed record SeededDescriptors(string[] Ids, string[] CodeValues);

    private static async Task<SeededDescriptors> SeedDescriptorsAsync(
        ApiIntegrationHarness harness,
        string scenario
    )
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        List<string> seededIds = [];
        List<string> codeValues = [];

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            string codeValue = $"Partitions-{scenario}-{suffix}-{index}";
            var payload = new JsonObject
            {
                ["namespace"] = $"uri://ed-fi.org/SchoolTypeDescriptor/Partitions/{scenario}/{suffix}",
                ["codeValue"] = codeValue,
                ["shortDescription"] = $"Partitions {scenario} {suffix} {index}",
            };

            seededIds.Add(await CreateAsync(harness, DescriptorEndpoint, payload));
            codeValues.Add(codeValue);
        }

        return new SeededDescriptors([.. seededIds], [.. codeValues]);
    }

    /// <summary>
    /// Creates a document through the same HTTP pipeline the partitions request uses and returns its
    /// identity, taken from the Location header the create response carries.
    /// </summary>
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
}

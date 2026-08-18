// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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
    /// A walk inside one partition cannot loop forever on a token that fails to advance: it exhausts
    /// this and fails with the pages it did retrieve rather than hanging.
    /// </summary>
    private const int MaximumWalkedPages = 25;

    public static async Task It_covers_a_regular_resource_collection_across_its_partitions(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await SeedMergeItemsAsync(harness, "coverage");

        var pageTokens = await ReadPageTokensAsync(
            harness,
            $"{MergeItemsPartitionsEndpoint}?partitionCount=3"
        );

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

        var pageTokens = await ReadPageTokensAsync(
            harness,
            $"{DescriptorPartitionsEndpoint}?partitionCount=3"
        );

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

        var pageTokens = await ReadPageTokensAsync(
            harness,
            $"{MergeItemsPartitionsEndpoint}?partitionCount=2"
        );

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
    /// An empty collection is a partitionable one: it simply has no boundaries. The response shape does
    /// not change, so a client can walk zero tokens without special-casing anything.
    /// </summary>
    public static async Task It_returns_an_empty_token_array_for_a_filter_matching_nothing(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

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
            $"{MergeItemsPartitionsEndpoint}?partitionCount=0"
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
        HashSet<string> returnedIds = [];

        foreach (string partitionToken in pageTokens)
        {
            string? pageToken = partitionToken;

            for (var page = 0; page < MaximumWalkedPages; page++)
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

            pageToken.Should().BeNull($"a walk of one partition of '{collectionEndpoint}' must terminate");
        }

        return returnedIds;
    }

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
    private static int UniqueIdentity(string suffix, int index) =>
        1_387_000 + Math.Abs(suffix.GetHashCode(StringComparison.Ordinal) % 100_000) * 10 + index;

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

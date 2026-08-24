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
    /// </remarks>
    internal static string EntryPageToken { get; } = PageTokenCodec.Encode(CursorRange.From(1));

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
        PageTokenCodec
            .TryDecode(nextPageToken, out var range)
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
        ArgumentNullException.ThrowIfNull(querySuffix);

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
    internal static async Task<IReadOnlyList<string>> SeedExtensionItemsAsync(
        ApiIntegrationHarness harness,
        int count,
        Func<int, string> labelFor,
        Func<int, int> numberFor
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(labelFor);
        ArgumentNullException.ThrowIfNull(numberFor);

        List<string> seededIds = [];

        for (var index = 0; index < count; index++)
        {
            var payload = new JsonObject
            {
                ["partitionContractItemId"] = Interlocked.Increment(ref _nextExtensionItemIdentity),
                ["label"] = labelFor(index),
                ["number"] = numberFor(index),
            };

            seededIds.Add(await CreateAsync(harness, ExtensionItemsEndpoint, payload));
        }

        return seededIds;
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

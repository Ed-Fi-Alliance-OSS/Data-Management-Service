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
/// The collections belong to the descriptor-runtime fixture, whose ApiSchema declares both a regular
/// resource and a descriptor. Every document is created over HTTP rather than inserted, so a page can
/// only return what the write path really stored.
/// </remarks>
internal static class CursorContractSupport
{
    internal const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    internal const string DescriptorEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    internal const string NextPageTokenHeaderName = "Next-Page-Token";
    internal const string TotalCountHeaderName = "Total-Count";

    private const string StandardJsonContentType = "application/json";

    /// <summary>
    /// Identities are handed out from one counter so two seeds in the same process cannot collide on a
    /// merge item's integer identity, which would answer 200 on an update instead of 201.
    /// </summary>
    private static int _nextMergeItemIdentity = 1_390_000;

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

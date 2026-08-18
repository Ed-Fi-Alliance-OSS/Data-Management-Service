// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// How the partitions endpoint answers a profiled request, asserted against the collection GET it is a
/// sibling of.
///
/// <para>
/// The partitions pipeline composes the same <c>ProfileResolutionMiddleware</c> the collection read
/// does, so the design requires the two to answer a profile identically. Composition alone does not
/// prove that: the two pipelines are built separately, and a step present in one and absent from the
/// other would only show up as a different HTTP answer. These requests compare the two answers directly.
/// </para>
///
/// <para>
/// The success side matters as much as the refusal. A boundary set carries tokens rather than documents,
/// so no readable profile can shape it, and the response stays <c>application/json</c> rather than
/// acquiring a profile media type.
/// </para>
/// </summary>
internal static class PartitionProfileScenario
{
    private const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string MergeItemsPartitionsEndpoint = $"{MergeItemsEndpoint}/partitions";
    private const string StandardJsonContentType = "application/json";
    private const string ProblemJsonContentType = "application/problem+json";

    /// <summary>
    /// Names the write-only profile with the readable usage, which is how a client explicitly asks to
    /// read through a profile. The profile exists and the resource is in it, so resolution gets as far
    /// as finding that the resource has no readable content type.
    /// </summary>
    private const string WriteOnlyReadableContentType =
        "application/vnd.ed-fi.profilerootonlymergeitem.profilerootonlymergeitem-writeonly.readable+json";

    private const string ProfileMethodUsageErrorType = "urn:ed-fi:api:profile:method-usage";

    /// <summary>
    /// The profile the fixtures binding this scenario must assign to the requesting application.
    /// </summary>
    /// <remarks>
    /// Assignment is what puts a request that names no profile onto the implicit-selection path. With no
    /// assignment at all, <c>CachedProfileService</c> answers from its no-profiles-assigned exit and the
    /// selection rule this scenario exists to cover — that a GET keeps only profiles with a readable
    /// content type — is never reached. The wrappers read this constant rather than restating the name,
    /// so the assignment cannot drift from the profile the assertions describe.
    /// </remarks>
    internal const string AssignedWriteOnlyProfileName = "ProfileRootOnlyMergeItem-WriteOnly";

    /// <summary>
    /// An explicitly requested profile that cannot read this resource refuses the partitions request
    /// exactly as it refuses the collection read: same status, same error type, same body. Asserting the
    /// two responses against each other rather than against a transcribed expectation is what makes this
    /// a statement about agreement rather than about one endpoint's wording.
    /// </summary>
    public static async Task It_refuses_a_write_only_profile_exactly_as_the_collection_get_does(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        (HttpStatusCode collectionStatus, string collectionBody) = await GetProfiledAsync(
            harness,
            MergeItemsEndpoint
        );
        (HttpStatusCode partitionsStatus, string partitionsBody) = await GetProfiledAsync(
            harness,
            MergeItemsPartitionsEndpoint
        );

        collectionStatus
            .Should()
            .Be(
                HttpStatusCode.MethodNotAllowed,
                $"the collection read establishes the contract being matched. Body: {collectionBody}"
            );
        partitionsStatus.Should().Be(collectionStatus, partitionsBody);

        JsonNode collection = JsonNode.Parse(collectionBody)!;
        JsonNode partitions = JsonNode.Parse(partitionsBody)!;

        collection["type"]!.GetValue<string>().Should().Be(ProfileMethodUsageErrorType);
        partitions["type"]!.GetValue<string>().Should().Be(collection["type"]!.GetValue<string>());
        partitions["title"]!.GetValue<string>().Should().Be(collection["title"]!.GetValue<string>());
        partitions["detail"]!.GetValue<string>().Should().Be(collection["detail"]!.GetValue<string>());
        ErrorsOf(partitions).Should().Equal(ErrorsOf(collection));
    }

    /// <summary>
    /// The application is assigned the write-only profile and the request names none, so implicit
    /// selection runs and finds no readable profile applies to a GET. The partitions response is served
    /// unfiltered, as plain JSON.
    /// </summary>
    /// <remarks>
    /// The assignment is the point. Without it the request would be answered by the
    /// no-profiles-assigned exit, and a regression that treated an implicitly assigned write-only
    /// profile as applicable to a GET — answering 405 — would leave this test green.
    /// </remarks>
    public static async Task It_serves_partitions_unfiltered_when_an_assigned_profile_is_not_readable(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        await SeedMergeItemsAsync(harness);

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(MergeItemsPartitionsEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response
            .Content.Headers.ContentType!.MediaType.Should()
            .Be(
                StandardJsonContentType,
                "a boundary set carries tokens rather than documents, so no readable profile can shape it"
            );
        JsonNode.Parse(body)!["pageTokens"]!
            .AsArray()
            .Should()
            .NotBeEmpty("the seeded documents have at least one partition");
    }

    private static IReadOnlyList<string> ErrorsOf(JsonNode body) =>
        [.. body["errors"]!.AsArray().Select(static error => error!.GetValue<string>())];

    private static async Task<(HttpStatusCode Status, string Body)> GetProfiledAsync(
        ApiIntegrationHarness harness,
        string requestUri
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(WriteOnlyReadableContentType));

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .Content.Headers.ContentType!.MediaType.Should()
            .Be(ProblemJsonContentType, "a profile refusal is a ProblemDetails response");

        return (response.StatusCode, body);
    }

    /// <summary>
    /// Seeds a few documents so the unfiltered partitions request has something to describe. The POST
    /// names no profile, so the assigned write-only profile is implicitly applied to it; its
    /// <c>IncludeAll</c> write content type restricts only a nested <c>profileScope</c> object the seed
    /// payload does not carry, so the stored documents are unchanged. The count is deliberately small:
    /// this scenario is about profile handling, and the multi-partition sizing is asserted by
    /// <see cref="PartitionEndpointScenario" />.
    ///
    /// <para>
    /// The identity is a per-run slot inside Int32, because the merge item's identity is an integer and
    /// a collision with a sibling scenario's seed would answer 200 on an update instead of 201. The
    /// stride is wider than the seed so a run's indices stay inside the slot its suffix selected, and
    /// the base is offset from the one <see cref="PartitionEndpointScenario" /> uses so the two
    /// scenarios cannot land on the same slot.
    /// </para>
    /// </summary>
    private static async Task SeedMergeItemsAsync(ApiIntegrationHarness harness)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        for (var index = 0; index < 3; index++)
        {
            var payload = new JsonObject
            {
                ["profileRootOnlyMergeItemId"] =
                    1_387_500
                    + Math.Abs(suffix.GetHashCode(StringComparison.Ordinal) % 100_000) * 1_000
                    + index,
                ["displayName"] = $"Partition profile {suffix} {index}",
            };

            using var content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                StandardJsonContentType
            );
            using HttpResponseMessage response = await harness.HttpClient.PostAsync(
                MergeItemsEndpoint,
                content
            );
            string body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST seed body: {body}");
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

/// <summary>
/// Cursor walks and partition walks performed through a routed, tenant-scoped path.
/// </summary>
/// <remarks>
/// What this covers is isolation, not sizing: every request keeps the tenant segment and both route
/// qualifiers, and each context must return only the descriptor seeded in it. One partition per context
/// is the expected shape here, because the route-context deployment ships the ordinary maximum page
/// size and the default partition count; the multi-partition proof lives in the dedicated sizing lane.
/// </remarks>
[Binding]
public class CursorPartitionRouteContextStepDefinitions(InstanceManagementContext context)
{
    private const string NextPageTokenHeader = "Next-Page-Token";

    /// <summary>
    /// A walk that failed to advance would exhaust this and fail with what it had retrieved rather
    /// than paging forever.
    /// </summary>
    private const int MaximumWalkedPages = 25;

    /// <summary>
    /// The deployment's partition count, which is also the upper bound on the tokens a routed
    /// partitions response may hand out.
    /// </summary>
    private const int DefaultPartitionCount = 10;

    private readonly List<string> _walkedCodeValues = [];

    /// <summary>
    /// Enters a cursor walk the way a client does - an ordinary page first, then the continuation it
    /// hands back - with every request carrying the tenant segment and route qualifiers.
    /// </summary>
    [When("a routed cursor walk is made for instance {string} resource {string}")]
    public async Task WhenARoutedCursorWalkIsMade(string instanceRoute, string resource)
    {
        (string districtId, string schoolYear) = SplitRoute(instanceRoute);
        DmsApiClient client = RequireClient();

        _walkedCodeValues.Clear();

        await WalkToTerminalPageAsync(client, districtId, schoolYear, resource, "limit=1");
    }

    /// <summary>
    /// Requests the routed partitions sibling and walks every token it hands out to its own terminal
    /// empty page.
    /// </summary>
    [When("the routed partitions are walked for instance {string} resource {string}")]
    public async Task WhenTheRoutedPartitionsAreWalked(string instanceRoute, string resource)
    {
        (string districtId, string schoolYear) = SplitRoute(instanceRoute);
        DmsApiClient client = RequireClient();

        _walkedCodeValues.Clear();

        HttpResponseMessage partitionsResponse = await client.GetPartitionsAsync(
            districtId,
            schoolYear,
            resource
        );
        string partitionsBody = await partitionsResponse.Content.ReadAsStringAsync();

        partitionsResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, $"routed partitions request failed: {partitionsBody}");

        using JsonDocument document = JsonDocument.Parse(partitionsBody);
        string[] pageTokens =
        [
            .. document
                .RootElement.GetProperty("pageTokens")
                .EnumerateArray()
                .Select(token => token.GetString()!),
        ];

        pageTokens
            .Should()
            .NotBeEmpty("a routed collection holding a document yields at least one partition");
        pageTokens
            .Should()
            .HaveCountLessThanOrEqualTo(
                DefaultPartitionCount,
                "the configured partition count is an upper bound the response never exceeds"
            );

        foreach (string pageToken in pageTokens)
        {
            await WalkToTerminalPageAsync(
                client,
                districtId,
                schoolYear,
                resource,
                $"pageToken={Uri.EscapeDataString(pageToken)}&pageSize=1"
            );
        }
    }

    /// <summary>
    /// Asserts the routed walk returned exactly the descriptor seeded in this context, and nothing else.
    /// </summary>
    /// <remarks>
    /// The expected value is written in the feature rather than read back from a response, so a walk
    /// that leaked another tenant's or another route context's descriptor fails naming it.
    /// </remarks>
    [Then("the routed walk returned exactly the code value {string}")]
    public void ThenTheRoutedWalkReturnedExactlyTheCodeValue(string expectedCodeValue)
    {
        _walkedCodeValues.Should().OnlyHaveUniqueItems("a walk must not return the same descriptor twice");
        _walkedCodeValues
            .Should()
            .Equal(
                [expectedCodeValue],
                "this route context holds exactly the descriptor seeded in it; anything else is a "
                    + "tenant or route-qualifier isolation failure"
            );
    }

    /// <summary>
    /// Follows one routed walk from its opening request to the terminal page, accumulating the code
    /// values every page carried.
    /// </summary>
    /// <remarks>
    /// A walk ends on a request that selected nothing and offered no continuation, whether it opened
    /// on an ordinary collection query or on a partition token, so both entry points assert the same
    /// terminating shape.
    /// </remarks>
    private async Task WalkToTerminalPageAsync(
        DmsApiClient client,
        string districtId,
        string schoolYear,
        string resource,
        string initialQuery
    )
    {
        string query = initialQuery;
        List<string> codeValues;
        string? continuation;
        var pages = 0;

        do
        {
            HttpResponseMessage page = await client.GetResourceAsync(districtId, schoolYear, resource, query);

            (codeValues, continuation) = await ReadPageAsync(page);
            _walkedCodeValues.AddRange(codeValues);
            pages++;

            if (continuation is not null)
            {
                query = $"pageToken={Uri.EscapeDataString(continuation)}&pageSize=1";
            }
        } while (continuation is not null && pages < MaximumWalkedPages);

        continuation.Should().BeNull($"a routed walk must terminate within {MaximumWalkedPages} pages");
        codeValues
            .Should()
            .BeEmpty("a routed walk ends on the request that selected nothing and offered no continuation");
    }

    private DmsApiClient RequireClient()
    {
        context.DmsClient.Should().NotBeNull("the tenant's credentials must be established first");

        return context.DmsClient!;
    }

    private static (string DistrictId, string SchoolYear) SplitRoute(string instanceRoute)
    {
        string[] parts = instanceRoute.Split('/');

        parts.Should().HaveCount(2, "instance route must be in the districtId/schoolYear form");

        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Reads one routed page: the descriptor code values it carries and the continuation it offers.
    /// </summary>
    private static async Task<(List<string> CodeValues, string? Continuation)> ReadPageAsync(
        HttpResponseMessage response
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"routed page failed: {body}");

        using JsonDocument document = JsonDocument.Parse(body);

        List<string> codeValues =
        [
            .. document
                .RootElement.EnumerateArray()
                .Select(element => element.GetProperty("codeValue").GetString()!),
        ];

        codeValues.Should().HaveCountLessThanOrEqualTo(1, "each routed page was asked for one document");

        return response.Headers.TryGetValues(NextPageTokenHeader, out var headerValues)
            ? (codeValues, headerValues.Single())
            : (codeValues, null);
    }
}

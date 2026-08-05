// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// What the HTTP boundary hands Core: the canonical spelling of each parameter name and, for
/// repeated names and case variants, the value that survives. These assert the captured dictionary
/// only; that the surviving value is what drives validation is proven end to end by the API-level
/// integration scenario, because query validation is not reachable without a database.
/// </summary>
[TestFixture]
[NonParallelizable]
public class CursorQueryParameterCanonicalizationTests
{
    private static IFrontendResponse FakeGetResponse()
    {
        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(200);
        A.CallTo(() => response.Body).Returns(new JsonArray());
        A.CallTo(() => response.Headers).Returns(new Dictionary<string, string>());
        A.CallTo(() => response.ContentType).Returns("application/json");
        return response;
    }

    private static async Task<IReadOnlyDictionary<string, string>> CapturedQueryParameters(string requestUri)
    {
        var apiService = A.Fake<IApiService>();
        FrontendRequest? capturedRequest = null;

        A.CallTo(() => apiService.Get(A<FrontendRequest>._))
            .Invokes((FrontendRequest request) => capturedRequest = request)
            .Returns(Task.FromResult(FakeGetResponse()));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddTransient(x => apiService);
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(requestUri);

        response.IsSuccessStatusCode.Should().BeTrue();
        capturedRequest.Should().NotBeNull();

        return capturedRequest!.QueryParameters;
    }

    [TestCase("pageToken")]
    [TestCase("PAGETOKEN")]
    [TestCase("pagetoken")]
    [TestCase("PageToken")]
    public async Task It_canonicalizes_the_page_token_name(string suppliedName)
    {
        var queryParameters = await CapturedQueryParameters($"/data/ed-fi/schools?{suppliedName}=abc");

        queryParameters.Should().ContainKey("pageToken").WhoseValue.Should().Be("abc");
    }

    [TestCase("pageSize")]
    [TestCase("PAGESIZE")]
    [TestCase("pagesize")]
    [TestCase("PageSize")]
    public async Task It_canonicalizes_the_page_size_name(string suppliedName)
    {
        var queryParameters = await CapturedQueryParameters($"/data/ed-fi/schools?{suppliedName}=5");

        queryParameters.Should().ContainKey("pageSize").WhoseValue.Should().Be("5");
    }

    [Test]
    public async Task It_keeps_the_last_value_across_case_variants_of_the_page_token()
    {
        var queryParameters = await CapturedQueryParameters(
            "/data/ed-fi/schools?pageToken=first&PAGETOKEN=last"
        );

        queryParameters.Should().ContainKey("pageToken").WhoseValue.Should().Be("last");
    }

    [Test]
    public async Task It_keeps_the_last_value_across_repeated_exact_page_size_names()
    {
        var queryParameters = await CapturedQueryParameters(
            "/data/ed-fi/schools?pageSize=1&pageSize=2&pageSize=3"
        );

        queryParameters.Should().ContainKey("pageSize").WhoseValue.Should().Be("3");
    }

    [Test]
    public async Task It_keeps_the_last_value_across_mixed_case_variants_of_the_page_size()
    {
        var queryParameters = await CapturedQueryParameters(
            "/data/ed-fi/schools?pageSize=1&PAGESIZE=2&PageSize=3"
        );

        queryParameters.Should().ContainKey("pageSize").WhoseValue.Should().Be("3");
    }

    [Test]
    public async Task It_collapses_case_variants_without_throwing()
    {
        var queryParameters = await CapturedQueryParameters(
            "/data/ed-fi/schools?pageToken=a&PAGETOKEN=b&pageSize=1&PAGESIZE=2&limit=3&LIMIT=4"
        );

        queryParameters.Should().ContainKey("pageToken").WhoseValue.Should().Be("b");
        queryParameters.Should().ContainKey("pageSize").WhoseValue.Should().Be("2");
        queryParameters.Should().ContainKey("limit").WhoseValue.Should().Be("4");
    }

    /// <summary>
    /// The traditional parameters were made case-insensitive deliberately and are locked by the
    /// existing public URL-validation scenarios; this feature must not narrow them.
    /// </summary>
    [TestCase("liMIt", "limit", "2")]
    [TestCase("OfFSeT", "offset", "1")]
    [TestCase("TOTALCOUNT", "totalCount", "true")]
    public async Task It_preserves_the_existing_traditional_canonicalization(
        string suppliedName,
        string canonicalName,
        string value
    )
    {
        var queryParameters = await CapturedQueryParameters($"/data/ed-fi/schools?{suppliedName}={value}");

        queryParameters.Should().ContainKey(canonicalName).WhoseValue.Should().Be(value);
    }

    [Test]
    public async Task It_leaves_an_unrecognized_name_exactly_as_supplied()
    {
        var queryParameters = await CapturedQueryParameters("/data/ed-fi/schools?SchoolId=1");

        queryParameters.Should().ContainKey("SchoolId");
        queryParameters.Should().NotContainKey("schoolId");
    }

    /// <summary>
    /// The partition count is generic enough to collide with a resource query field, so its spelling
    /// is only rewritten where it is a paging control.
    /// </summary>
    [TestCase("NUMBER")]
    [TestCase("Number")]
    public async Task It_leaves_the_partition_count_untouched_on_an_ordinary_collection(string suppliedName)
    {
        var queryParameters = await CapturedQueryParameters($"/data/ed-fi/schools?{suppliedName}=10");

        queryParameters.Should().ContainKey(suppliedName);
        queryParameters.Should().NotContainKey("number");
    }

    [TestCase("partitions", "NUMBER")]
    [TestCase("PARTITIONS", "Number")]
    [TestCase("Partitions", "number")]
    public async Task It_canonicalizes_the_partition_count_on_the_partitions_operation(
        string partitionsSegment,
        string suppliedName
    )
    {
        var queryParameters = await CapturedQueryParameters(
            $"/data/ed-fi/schools/{partitionsSegment}?{suppliedName}=10"
        );

        queryParameters.Should().ContainKey("number").WhoseValue.Should().Be("10");
    }

    [Test]
    public async Task It_keeps_the_last_partition_count_across_case_variants()
    {
        var queryParameters = await CapturedQueryParameters(
            "/data/ed-fi/schools/partitions?number=1&NUMBER=2"
        );

        queryParameters.Should().ContainKey("number").WhoseValue.Should().Be("2");
    }

    [Test]
    public async Task It_does_not_canonicalize_the_partition_count_on_a_by_id_path()
    {
        var queryParameters = await CapturedQueryParameters(
            "/data/ed-fi/schools/11111111-1111-1111-1111-111111111111?NUMBER=10"
        );

        queryParameters.Should().ContainKey("NUMBER");
        queryParameters.Should().NotContainKey("number");
    }
}

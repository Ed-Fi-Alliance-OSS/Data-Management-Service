// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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

    /// <summary>
    /// Issues the request against a host configured with the given route shape and returns the query
    /// parameters the HTTP boundary handed Core. <paramref name="routeQualifierSegments"/> is the
    /// comma-separated setting value naming the qualifier route segments, and
    /// <paramref name="multiTenancy"/> prepends the tenant route segment; the defaults produce the plain
    /// <c>/data/{**dmsPath}</c> route.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> CapturedQueryParameters(
        string requestUri,
        string? routeQualifierSegments = null,
        bool multiTenancy = false
    )
    {
        var apiService = A.Fake<IApiService>();
        FrontendRequest? capturedRequest = null;

        A.CallTo(() => apiService.Get(A<FrontendRequest>._))
            .Invokes((FrontendRequest request) => capturedRequest = request)
            .Returns(Task.FromResult(FakeGetResponse()));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            if (routeQualifierSegments is not null)
            {
                builder.UseSetting("AppSettings:RouteQualifierSegments", routeQualifierSegments);
            }
            if (multiTenancy)
            {
                builder.UseSetting("AppSettings:MultiTenancy", "true");
            }
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
    /// Canonicalizing a name uses the same comparison as the query collection's own comparer, so two
    /// entries the collection holds separately can never collapse onto one canonical key. KELVIN SIGN
    /// is what makes that observable: it is not ordinally case-insensitively equal to <c>k</c>, so a
    /// name spelled with it is a query collection entry of its own, yet lowercasing it yields <c>k</c>
    /// and so would produce the canonical <c>pageToken</c> a second time.
    /// </summary>
    [Test]
    public async Task It_keeps_a_page_token_lookalike_separate_from_the_canonical_name()
    {
        const string LookalikeName = "pageTo\u212Aen";

        var queryParameters = await CapturedQueryParameters(
            $"/data/ed-fi/schools?pageToken=abc&{LookalikeName}=xyz"
        );

        queryParameters.Should().ContainKey("pageToken").WhoseValue.Should().Be("abc");
        queryParameters.Should().ContainKey(LookalikeName).WhoseValue.Should().Be("xyz");
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

    /// <summary>
    /// Recognition must not depend on the server's culture. The Turkish locale lowercases <c>I</c> to
    /// a dotless <c>ı</c>, so a culture-sensitive fold silently stops recognizing any name containing
    /// that letter. Both a cursor name and a traditional name are covered because one fold serves all
    /// of them.
    /// </summary>
    [TestCase("PAGESIZE", "pageSize", "5")]
    [TestCase("LIMIT", "limit", "5")]
    public async Task It_canonicalizes_independently_of_the_server_culture(
        string suppliedName,
        string canonicalName,
        string value
    )
    {
        CultureInfo originalCurrent = CultureInfo.CurrentCulture;
        CultureInfo? originalDefault = CultureInfo.DefaultThreadCurrentCulture;
        CultureInfo turkish = CultureInfo.GetCultureInfo("tr-TR");

        try
        {
            CultureInfo.DefaultThreadCurrentCulture = turkish;
            CultureInfo.CurrentCulture = turkish;

            var queryParameters = await CapturedQueryParameters(
                $"/data/ed-fi/schools?{suppliedName}={value}"
            );

            queryParameters.Should().ContainKey(canonicalName).WhoseValue.Should().Be(value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCurrent;
            CultureInfo.DefaultThreadCurrentCulture = originalDefault;
        }
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

    /// <summary>
    /// Recognizing the partitions operation reads the final segment of the path Core is given, which holds
    /// only the <c>{project}/{resource}[/{segment}]</c> part: the tenant segment and the route qualifier
    /// segments are bound as their own route values and are never part of it. A prefixed request therefore
    /// canonicalizes the partition count exactly as a plain one does, and moving any prefix segment into
    /// that path would stop the operation from being recognized.
    /// </summary>
    [TestCase("/255902/2026/data/ed-fi/schools/partitions", "districtId,schoolYear", false)]
    [TestCase("/tenant1/data/ed-fi/schools/partitions", null, true)]
    [TestCase("/tenant1/255902/2026/data/ed-fi/schools/partitions", "districtId,schoolYear", true)]
    public async Task It_canonicalizes_the_partition_count_on_a_prefixed_partitions_path(
        string path,
        string? routeQualifierSegments,
        bool multiTenancy
    )
    {
        var queryParameters = await CapturedQueryParameters(
            $"{path}?NUMBER=10",
            routeQualifierSegments,
            multiTenancy
        );

        queryParameters.Should().ContainKey("number").WhoseValue.Should().Be("10");
    }

    /// <summary>
    /// The cursor parameters are canonicalized on every request regardless of path, so this covers the
    /// prefix handling itself: the request reaches the same boundary code once routing has bound the
    /// tenant and qualifier segments.
    /// </summary>
    [TestCase("/255902/2026/data/ed-fi/schools", "districtId,schoolYear", false)]
    [TestCase("/tenant1/data/ed-fi/schools", null, true)]
    [TestCase("/tenant1/255902/2026/data/ed-fi/schools", "districtId,schoolYear", true)]
    public async Task It_canonicalizes_the_page_token_on_a_prefixed_path(
        string path,
        string? routeQualifierSegments,
        bool multiTenancy
    )
    {
        var queryParameters = await CapturedQueryParameters(
            $"{path}?PAGETOKEN=abc",
            routeQualifierSegments,
            multiTenancy
        );

        queryParameters.Should().ContainKey("pageToken").WhoseValue.Should().Be("abc");
    }
}

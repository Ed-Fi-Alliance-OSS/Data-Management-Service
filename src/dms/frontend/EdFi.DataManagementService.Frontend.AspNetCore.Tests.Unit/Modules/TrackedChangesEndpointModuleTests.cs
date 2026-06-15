// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
public class TrackedChangesEndpointModuleTests
{
    private static IFrontendResponse FakeCoreGetResponse()
    {
        JsonObject body = new() { ["source"] = "core" };

        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(202);
        A.CallTo(() => response.Body).Returns(body);
        A.CallTo(() => response.Headers).Returns(new Dictionary<string, string>());
        A.CallTo(() => response.ContentType).Returns("application/json");
        return response;
    }

    [TestCase("deletes")]
    [TestCase("keyChanges")]
    public async Task It_routes_tracked_change_endpoint_to_empty_stub_without_core_api(
        string trackedChangeSegment
    )
    {
        var apiService = A.Fake<IApiService>();

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

        var response = await client.GetAsync($"/data/ed-fi/schools/{trackedChangeSegment}");
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(content)!.AsArray();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().BeEmpty();
        A.CallTo(() => apiService.Get(A<FrontendRequest>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_total_count_zero_when_requested()
    {
        var apiService = A.Fake<IApiService>();

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

        var response = await client.GetAsync("/data/ed-fi/schools/deletes?totalCount=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Total-Count").Should().Equal("0");
        A.CallTo(() => apiService.Get(A<FrontendRequest>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_routes_under_tenant_and_route_qualifier_prefix()
    {
        var apiService = A.Fake<IApiService>();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:MultiTenancy"] = "true",
                            ["AppSettings:RouteQualifierSegments"] = "districtId,schoolYear",
                        }
                    );
                }
            );
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddTransient(x => apiService);
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/tenant1/255902/2026/data/ed-fi/schools/keyChanges");
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(content)!.AsArray();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().BeEmpty();
        A.CallTo(() => apiService.Get(A<FrontendRequest>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_leaves_other_data_get_paths_on_the_core_catch_all()
    {
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.Get(A<FrontendRequest>._)).Returns(Task.FromResult(FakeCoreGetResponse()));

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

        var response = await client.GetAsync("/data/ed-fi/schools/not-a-uuid");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        A.CallTo(() => apiService.Get(A<FrontendRequest>._)).MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_builds_route_patterns_with_the_same_prefix_shape_as_core_data_routes()
    {
        TrackedChangesEndpointModule
            .BuildRoutePattern(["districtId", "schoolYear"], multiTenancy: true, "deletes")
            .Should()
            .Be("/{tenant}/{districtId}/{schoolYear}/data/{projectNamespace}/{endpointName}/deletes");
    }
}

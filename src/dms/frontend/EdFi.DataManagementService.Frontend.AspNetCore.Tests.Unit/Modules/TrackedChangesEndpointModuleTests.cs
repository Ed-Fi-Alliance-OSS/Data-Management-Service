// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
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

    private static IFrontendResponse FakeTrackedChangeResponse()
    {
        JsonArray body = [new JsonObject { ["source"] = "core" }];

        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(200);
        A.CallTo(() => response.Body).Returns(body);
        A.CallTo(() => response.Headers).Returns(new Dictionary<string, string> { ["Total-Count"] = "1" });
        A.CallTo(() => response.ContentType).Returns("application/json");
        return response;
    }

    [TestCase("deletes")]
    [TestCase("keyChanges")]
    public async Task It_routes_tracked_change_endpoint_to_core_api(string trackedChangeSegment)
    {
        var apiService = A.Fake<IApiService>();
        FrontendRequest? capturedRequest = null;
        A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._))
            .Invokes((FrontendRequest request) => capturedRequest = request)
            .Returns(Task.FromResult(FakeTrackedChangeResponse()));

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

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/data/ed-fi/schools/{trackedChangeSegment}"
        );
        request.Headers.Add("X-Test-Header", "header-value");

        var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(content)!.AsArray();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().ContainSingle();
        A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._)).MustHaveHappenedOnceExactly();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Path.Should().Be($"/ed-fi/schools/{trackedChangeSegment}");
        capturedRequest.Headers["X-Test-Header"].Should().Be("header-value");
        A.CallTo(() => apiService.Get(A<FrontendRequest>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_passes_total_count_query_to_core_and_returns_core_header()
    {
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._))
            .Returns(Task.FromResult(FakeTrackedChangeResponse()));

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
        response.Headers.GetValues("Total-Count").Should().Equal("1");
        A.CallTo(() =>
                apiService.GetTrackedChanges(
                    A<FrontendRequest>.That.Matches(request =>
                        request.QueryParameters["totalCount"] == "true"
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => apiService.Get(A<FrontendRequest>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_routes_under_tenant_and_route_qualifier_prefix()
    {
        var apiService = A.Fake<IApiService>();
        FrontendRequest? capturedRequest = null;
        A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._))
            .Invokes((FrontendRequest request) => capturedRequest = request)
            .Returns(Task.FromResult(FakeTrackedChangeResponse()));

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
        body.Should().ContainSingle();
        A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._)).MustHaveHappenedOnceExactly();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Path.Should().Be("/ed-fi/schools/keyChanges");
        capturedRequest.Tenant.Should().Be("tenant1");
        capturedRequest
            .RouteQualifiers[new RouteQualifierName("districtId")]
            .Should()
            .Be(new RouteQualifierValue("255902"));
        capturedRequest
            .RouteQualifiers[new RouteQualifierName("schoolYear")]
            .Should()
            .Be(new RouteQualifierValue("2026"));
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
        A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._)).MustNotHaveHappened();
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

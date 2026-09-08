// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
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
public class ChangeQueriesModuleTests
{
    private static IFrontendResponse FakeResponse(long newestChangeVersion)
    {
        JsonObject body = new()
        {
            ["oldestChangeVersion"] = 0L,
            ["newestChangeVersion"] = newestChangeVersion,
        };

        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(200);
        A.CallTo(() => response.Body).Returns(body);
        A.CallTo(() => response.Headers).Returns(new Dictionary<string, string>());
        A.CallTo(() => response.ContentType).Returns("application/json");
        return response;
    }

    [Test]
    public async Task It_routes_available_change_versions_and_returns_the_core_response()
    {
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.GetAvailableChangeVersions(A<FrontendRequest>._))
            .Returns(Task.FromResult(FakeResponse(123L)));

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

        // Act
        var response = await client.GetAsync("/changeQueries/v1/availableChangeVersions");
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!["oldestChangeVersion"]!.GetValue<long>().Should().Be(0L);
        body!["newestChangeVersion"]!.GetValue<long>().Should().Be(123L);

        A.CallTo(() => apiService.GetAvailableChangeVersions(A<FrontendRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_routes_under_a_tenant_prefix_when_multitenancy_is_enabled()
    {
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.GetAvailableChangeVersions(A<FrontendRequest>._))
            .Returns(Task.FromResult(FakeResponse(7L)));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = "true" }
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

        // Act
        var response = await client.GetAsync("/tenant1/changeQueries/v1/availableChangeVersions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        A.CallTo(() => apiService.GetAvailableChangeVersions(A<FrontendRequest>._))
            .MustHaveHappenedOnceExactly();
    }
}

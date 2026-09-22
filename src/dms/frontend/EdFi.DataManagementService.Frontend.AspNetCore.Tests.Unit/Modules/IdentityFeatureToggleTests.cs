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

/// <summary>
/// AppSettings:EnableIdentityManagement gates the identity route surface at mapping time (D1, D10).
/// With the toggle off (the default), every identity route falls through to the same MapFallback
/// 404 every other unmapped route reaches (C1 routes half).
/// </summary>
[TestFixture]
[NonParallelizable]
public class IdentityFeatureToggleTests
{
    [TestFixture]
    public class Given_The_Toggle_Is_Off
    {
        private static WebApplicationFactory<Program> CreateFactory(bool enableIdentityManagement)
        {
            var apiService = A.Fake<IApiService>();

            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:EnableIdentityManagement"] = enableIdentityManagement
                                    ? "true"
                                    : "false",
                            }
                        );
                    }
                );
                builder.ConfigureServices(services =>
                {
                    TestMockHelper.AddEssentialMocks(services);
                    services.AddTransient(_ => apiService);
                });
            });
        }

        private static readonly (HttpMethod Method, string Path)[] IdentityRoutes =
        [
            (HttpMethod.Post, "/identity/v2/identities"),
            (HttpMethod.Get, "/identity/v2/identities/605943412"),
            (HttpMethod.Post, "/identity/v2/identities/find"),
            (HttpMethod.Post, "/identity/v2/identities/search"),
            (HttpMethod.Get, "/identity/v2/identities/results/tok"),
        ];

        private static IEnumerable<TestCaseData> RouteCases() =>
            IdentityRoutes.Select(route =>
                new TestCaseData(route.Method, route.Path).SetName($"{route.Method.Method} {route.Path}")
            );

        [TestCaseSource(nameof(RouteCases))]
        public async Task It_answers_the_fallback_not_found_for_every_identity_route(
            HttpMethod method,
            string path
        )
        {
            await using var factory = CreateFactory(enableIdentityManagement: false);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            }

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            var body = JsonNode.Parse(content);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            body.Should().NotBeNull();
            body!["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_On
    {
        private static IFrontendResponse OkResponse()
        {
            var response = A.Fake<IFrontendResponse>();
            A.CallTo(() => response.StatusCode).Returns(404);
            A.CallTo(() => response.Body)
                .Returns(new JsonObject { ["type"] = "urn:ed-fi:api:identities:operation-not-supported" });
            A.CallTo(() => response.Headers).Returns(new Dictionary<string, string>());
            A.CallTo(() => response.ContentType).Returns("application/problem+json");
            return response;
        }

        private static WebApplicationFactory<Program> CreateFactoryWithFakeApiService(IApiService apiService)
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:EnableIdentityManagement"] = "true",
                            }
                        );
                    }
                );
                builder.ConfigureServices(services =>
                {
                    TestMockHelper.AddEssentialMocks(services);
                    services.AddTransient(_ => apiService);
                });
            });
        }

        [Test]
        public async Task It_maps_the_create_route()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.IdentityCreate(A<FrontendRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(OkResponse()));

            await using var factory = CreateFactoryWithFakeApiService(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            A.CallTo(() => apiService.IdentityCreate(A<FrontendRequest>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_maps_the_get_by_id_route()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.IdentityGetById(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(OkResponse()));

            await using var factory = CreateFactoryWithFakeApiService(apiService);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/identity/v2/identities/605943412");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            A.CallTo(() =>
                    apiService.IdentityGetById(A<FrontendRequest>._, "605943412", A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_maps_the_find_route()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.IdentityFind(A<FrontendRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(OkResponse()));

            await using var factory = CreateFactoryWithFakeApiService(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities/find")
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            A.CallTo(() => apiService.IdentityFind(A<FrontendRequest>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_maps_the_search_route()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.IdentitySearch(A<FrontendRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(OkResponse()));

            await using var factory = CreateFactoryWithFakeApiService(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities/search")
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            A.CallTo(() => apiService.IdentitySearch(A<FrontendRequest>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_maps_the_results_route()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.IdentityResults(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(OkResponse()));

            await using var factory = CreateFactoryWithFakeApiService(apiService);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/identity/v2/identities/results/tok");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            A.CallTo(() => apiService.IdentityResults(A<FrontendRequest>._, "tok", A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }
}

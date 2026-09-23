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

    /// <summary>
    /// The metadata/discovery half of C1: the toggle also gates
    /// <c>/metadata/identity/v2/swagger.json</c>, the "Identity" entry in
    /// <c>/metadata/specifications</c>, and the <c>identity</c> URL in Discovery's <c>urls</c>.
    /// </summary>
    [TestFixture]
    public class Given_The_Metadata_And_Discovery_Surface
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

        [Test]
        public async Task With_the_toggle_off_the_swagger_route_is_not_found()
        {
            await using var factory = CreateFactory(enableIdentityManagement: false);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/metadata/identity/v2/swagger.json");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task With_the_toggle_off_the_specifications_listing_has_no_identity_entry()
        {
            await using var factory = CreateFactory(enableIdentityManagement: false);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/metadata/specifications");
            string content = await response.Content.ReadAsStringAsync();
            JsonArray sections = JsonNode.Parse(content)!.AsArray();

            sections.Any(node => node!["name"]!.GetValue<string>() == "Identity").Should().BeFalse();
        }

        [Test]
        public async Task With_the_toggle_off_discovery_has_no_identity_url()
        {
            await using var factory = CreateFactory(enableIdentityManagement: false);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/");
            string content = await response.Content.ReadAsStringAsync();
            JsonNode json = JsonNode.Parse(content)!;

            json["urls"]!["identity"].Should().BeNull();
        }

        [Test]
        public async Task With_the_toggle_on_the_swagger_route_is_found()
        {
            await using var factory = CreateFactory(enableIdentityManagement: true);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/metadata/identity/v2/swagger.json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task With_the_toggle_on_the_specifications_listing_contains_the_identity_entry()
        {
            await using var factory = CreateFactory(enableIdentityManagement: true);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/metadata/specifications");
            string content = await response.Content.ReadAsStringAsync();
            JsonArray sections = JsonNode.Parse(content)!.AsArray();

            JsonNode? identitySection = sections.SingleOrDefault(node =>
                node!["name"]!.GetValue<string>() == "Identity"
            );

            identitySection.Should().NotBeNull();
            identitySection!["prefix"]!.GetValue<string>().Should().Be("Other");
        }

        [Test]
        public async Task With_the_toggle_on_discovery_has_an_identity_url_ending_in_identity_v2()
        {
            await using var factory = CreateFactory(enableIdentityManagement: true);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/");
            string content = await response.Content.ReadAsStringAsync();
            JsonNode json = JsonNode.Parse(content)!;

            json["urls"]!["identity"]!.GetValue<string>().Should().EndWith("/identity/v2/");
        }
    }
}

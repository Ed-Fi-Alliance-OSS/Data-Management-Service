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

    private static WebApplicationFactory<Program> CreateFactoryWithFakeApiService(IApiService apiService)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:EnableIdentityManagement"] = "true" }
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

    [TestFixture]
    public class Given_The_Toggle_Is_Off
    {
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
    public class Given_The_Toggle_Is_On_And_The_Create_Route
    {
        private IApiService _apiService = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _apiService = A.Fake<IApiService>();
            A.CallTo(() => _apiService.IdentityCreate(A<FrontendRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(OkResponse()));

            _factory = CreateFactoryWithFakeApiService(_apiService);
            _client = _factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };

            _response = await _client.SendAsync(request);
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_the_facade_response()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void It_dispatches_to_identity_create()
        {
            A.CallTo(() => _apiService.IdentityCreate(A<FrontendRequest>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_On_And_The_Get_By_Id_Route
    {
        private IApiService _apiService = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    _apiService.IdentityGetById(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(OkResponse()));

            _factory = CreateFactoryWithFakeApiService(_apiService);
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/identity/v2/identities/605943412");
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_the_facade_response()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void It_dispatches_to_identity_get_by_id_with_the_route_value()
        {
            A.CallTo(() =>
                    _apiService.IdentityGetById(A<FrontendRequest>._, "605943412", A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_On_And_The_Find_Route
    {
        private IApiService _apiService = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _apiService = A.Fake<IApiService>();
            A.CallTo(() => _apiService.IdentityFind(A<FrontendRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(OkResponse()));

            _factory = CreateFactoryWithFakeApiService(_apiService);
            _client = _factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities/find")
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };

            _response = await _client.SendAsync(request);
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_the_facade_response()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void It_dispatches_to_identity_find()
        {
            A.CallTo(() => _apiService.IdentityFind(A<FrontendRequest>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_On_And_The_Search_Route
    {
        private IApiService _apiService = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _apiService = A.Fake<IApiService>();
            A.CallTo(() => _apiService.IdentitySearch(A<FrontendRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(OkResponse()));

            _factory = CreateFactoryWithFakeApiService(_apiService);
            _client = _factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities/search")
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };

            _response = await _client.SendAsync(request);
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_the_facade_response()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void It_dispatches_to_identity_search()
        {
            A.CallTo(() => _apiService.IdentitySearch(A<FrontendRequest>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_On_And_The_Results_Route
    {
        private IApiService _apiService = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    _apiService.IdentityResults(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(OkResponse()));

            _factory = CreateFactoryWithFakeApiService(_apiService);
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/identity/v2/identities/results/tok");
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_the_facade_response()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void It_dispatches_to_identity_results_with_the_route_value()
        {
            A.CallTo(() => _apiService.IdentityResults(A<FrontendRequest>._, "tok", A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    /// <summary>
    /// The metadata/discovery half of C1: the toggle also gates
    /// <c>/metadata/identity/v2/swagger.json</c>, the "Identity" entry in
    /// <c>/metadata/specifications</c>, and the <c>identity</c> URL in Discovery's <c>urls</c>.
    /// </summary>
    [TestFixture]
    public class Given_The_Toggle_Is_Off_And_The_Swagger_Route
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(enableIdentityManagement: false);
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/metadata/identity/v2/swagger.json");
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_is_not_found()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_Off_And_The_Specifications_Listing
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonArray _sections = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(enableIdentityManagement: false);
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/metadata/specifications");
            string content = await _response.Content.ReadAsStringAsync();
            _sections = JsonNode.Parse(content)!.AsArray();
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_has_no_identity_entry()
        {
            _sections.Any(node => node!["name"]!.GetValue<string>() == "Identity").Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_Off_And_Discovery
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonNode _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(enableIdentityManagement: false);
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/");
            string content = await _response.Content.ReadAsStringAsync();
            _document = JsonNode.Parse(content)!;
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_has_no_identity_url()
        {
            _document["urls"]!["identity"].Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_On_And_The_Swagger_Route
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(enableIdentityManagement: true);
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/metadata/identity/v2/swagger.json");
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_is_found()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_On_And_The_Specifications_Listing
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonNode? _identitySection;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(enableIdentityManagement: true);
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/metadata/specifications");
            string content = await _response.Content.ReadAsStringAsync();
            JsonArray sections = JsonNode.Parse(content)!.AsArray();

            _identitySection = sections.SingleOrDefault(node =>
                node!["name"]!.GetValue<string>() == "Identity"
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_contains_the_identity_entry()
        {
            _identitySection.Should().NotBeNull();
        }

        [Test]
        public void It_uses_the_other_prefix()
        {
            _identitySection!["prefix"]!.GetValue<string>().Should().Be("Other");
        }
    }

    [TestFixture]
    public class Given_The_Toggle_Is_On_And_Discovery
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonNode _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(enableIdentityManagement: true);
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/");
            string content = await _response.Content.ReadAsStringAsync();
            _document = JsonNode.Parse(content)!;
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_has_an_identity_url_ending_in_identity_v2()
        {
            _document["urls"]!["identity"]!.GetValue<string>().Should().EndWith("/identity/v2/");
        }
    }
}

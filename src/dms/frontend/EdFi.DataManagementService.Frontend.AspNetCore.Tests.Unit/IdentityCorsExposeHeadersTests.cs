// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
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

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// C5: with the toggle on, a request carrying <c>Origin: &lt;Cors:SwaggerUIOrigin&gt;</c> receives
/// <c>Access-Control-Expose-Headers</c> including <c>Location</c> on the async 202 and on the
/// incomplete results 200, exercised through the real <c>UseCors</c> pipeline rather than the
/// policy object directly. A request without an <c>Origin</c> header carries no CORS headers.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IdentityCorsExposeHeadersTests
{
    // The Cors:SwaggerUIOrigin default (appsettings.json), unchanged by the Test environment.
    private const string SwaggerUiOrigin = "http://localhost:8082";
    private const string ResultsLocationPath = "/identity/v2/identities/results/tok";

    private static WebApplicationFactory<Program> CreateFactory(IApiService apiService)
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

    private static IFrontendResponse CannedResponse(int statusCode, string locationHeaderPath)
    {
        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(statusCode);
        A.CallTo(() => response.Body).Returns(new JsonObject { ["status"] = statusCode });
        A.CallTo(() => response.Headers).Returns(new Dictionary<string, string>());
        A.CallTo(() => response.LocationHeaderPath).Returns(locationHeaderPath);
        A.CallTo(() => response.ContentType).Returns("application/json");
        return response;
    }

    [TestFixture]
    public class Given_A_Request_With_Origin_On_The_Async_202
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.IdentityFind(A<FrontendRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(CannedResponse(202, ResultsLocationPath)));

            _factory = CreateFactory(apiService);
            _client = _factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities/find")
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Origin", SwaggerUiOrigin);

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
        public void It_returns_202_accepted()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        [Test]
        public void It_exposes_location_in_access_control_expose_headers()
        {
            _response
                .Headers.GetValues("Access-Control-Expose-Headers")
                .Should()
                .Contain(value => value.Contains("Location"));
        }
    }

    [TestFixture]
    public class Given_A_Request_With_Origin_On_The_Incomplete_Results_200
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.IdentityResults(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(CannedResponse(200, ResultsLocationPath)));

            _factory = CreateFactory(apiService);
            _client = _factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/identity/v2/identities/results/tok");
            request.Headers.Add("Origin", SwaggerUiOrigin);

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
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_exposes_location_in_access_control_expose_headers()
        {
            _response
                .Headers.GetValues("Access-Control-Expose-Headers")
                .Should()
                .Contain(value => value.Contains("Location"));
        }
    }

    [TestFixture]
    public class Given_A_Request_Without_Origin
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.IdentityResults(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(CannedResponse(200, ResultsLocationPath)));

            _factory = CreateFactory(apiService);
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
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_carries_no_access_control_expose_headers()
        {
            _response.Headers.Contains("Access-Control-Expose-Headers").Should().BeFalse();
        }

        [Test]
        public void It_carries_no_access_control_allow_origin()
        {
            _response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Identity;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// C4: every host-generated identity operation response carries exactly one
/// <c>Cache-Control: no-store</c> - the success shapes (200, 202) that
/// <see cref="Infrastructure.SecurityHeadersMiddleware"/> deliberately leaves alone, the failure
/// shapes it already covered (400, 401, 403, 404, 415, 500, 502), and the real global rate
/// limiter's 429. Non-identity surfaces (the identity swagger document, Discovery, a resource
/// route, and the toggle-off fallback) carry no <c>no-store</c> beyond today's behavior.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IdentityNoStoreTests
{
    private static WebApplicationFactory<Program> CreateFactory(
        IApiService apiService,
        string environment = "Test",
        bool enableIdentityManagement = true
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
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

    [TestFixture]
    public class Given_An_Identity_Operation_Response
    {
        private static IFrontendResponse CannedResponse(int statusCode, string? locationHeaderPath = null)
        {
            var response = A.Fake<IFrontendResponse>();
            A.CallTo(() => response.StatusCode).Returns(statusCode);
            A.CallTo(() => response.Body).Returns(new JsonObject { ["status"] = statusCode });
            A.CallTo(() => response.Headers).Returns(new Dictionary<string, string>());
            A.CallTo(() => response.LocationHeaderPath).Returns(locationHeaderPath);
            A.CallTo(() => response.ContentType).Returns("application/problem+json");
            return response;
        }

        private static IEnumerable<TestCaseData> StatusCases()
        {
            yield return new TestCaseData(200, null).SetName("200 (get-by-id success)");
            yield return new TestCaseData(202, "/identity/v2/identities/results/tok").SetName(
                "202 (async accepted)"
            );
            yield return new TestCaseData(400, null).SetName("400 (invalid properties)");
            yield return new TestCaseData(401, null).SetName("401 (unauthenticated / unbound tenant)");
            yield return new TestCaseData(403, null).SetName("403 (forbidden)");
            yield return new TestCaseData(404, null).SetName("404 (tenant absent)");
            yield return new TestCaseData(404, null).SetName("404 (capability unsupported)");
            yield return new TestCaseData(404, null).SetName("404 (identity not-found)");
            yield return new TestCaseData(415, null).SetName("415 (unsupported media type)");
            yield return new TestCaseData(500, null).SetName("500 (provider configuration)");
            yield return new TestCaseData(502, null).SetName("502 (upstream failure)");
        }

        [TestCaseSource(nameof(StatusCases))]
        public async Task It_carries_cache_control_no_store_exactly_once(
            int statusCode,
            string? locationHeaderPath
        )
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.IdentityCreate(A<FrontendRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(CannedResponse(statusCode, locationHeaderPath)));

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            var response = await client.SendAsync(request);

            ((int)response.StatusCode).Should().Be(statusCode);
            response
                .Headers.GetValues("Cache-Control")
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be("no-store");
        }
    }

    [TestFixture]
    public class Given_The_Real_Rate_Limiter_Rejects_A_Second_Identity_Request
    {
        // The TestRateLimit environment permits one request per sixty-second window (see
        // RateLimitTests.cs). The first request here consumes the window against a plain route so
        // that the identity route is never reached at all in this test: the second request, to the
        // identity route, is rejected by the real global limiter before routing dispatches to any
        // endpoint, so IIdentityService is provably never invoked.
        [Test]
        public async Task It_rejects_the_second_request_with_no_store_and_never_invokes_the_provider()
        {
            var identityService = A.Fake<IIdentityService>();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("TestRateLimit");
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
                    services.AddSingleton(identityService);
                });
            });
            using var client = factory.CreateClient();

            using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/health");
            await client.SendAsync(firstRequest);

            using var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            var response = await client.SendAsync(secondRequest);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            response
                .Headers.GetValues("Cache-Control")
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be("no-store");
            body["status"]!.GetValue<int>().Should().Be(429);
            body["type"]!.ToString().Should().Be("urn:ed-fi:api:too-many-requests");

            A.CallTo(identityService).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_Surfaces_Outside_The_Identity_Operation_Routes
    {
        [Test]
        public async Task The_identity_swagger_document_carries_no_added_no_store()
        {
            var apiService = A.Fake<IApiService>();
            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/metadata/identity/v2/swagger.json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.Contains("Cache-Control").Should().BeFalse();
        }

        [Test]
        public async Task Discovery_carries_no_added_no_store()
        {
            var apiService = A.Fake<IApiService>();
            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.Contains("Cache-Control").Should().BeFalse();
        }

        [Test]
        public async Task A_resource_route_200_carries_no_added_no_store()
        {
            var apiService = A.Fake<IApiService>();
            var okResponse = A.Fake<IFrontendResponse>();
            A.CallTo(() => okResponse.StatusCode).Returns(200);
            A.CallTo(() => okResponse.Body).Returns(new JsonObject { ["id"] = "abc" });
            A.CallTo(() => okResponse.Headers).Returns(new Dictionary<string, string>());
            A.CallTo(() => okResponse.ContentType).Returns("application/json");
            A.CallTo(() => apiService.Get(A<FrontendRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(okResponse));

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/data/ed-fi/schools/abc");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.Contains("Cache-Control").Should().BeFalse();
        }

        [Test]
        public async Task The_toggle_off_fallback_carries_the_same_single_no_store_as_today()
        {
            var apiService = A.Fake<IApiService>();
            await using var factory = CreateFactory(apiService, enableIdentityManagement: false);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            response
                .Headers.GetValues("Cache-Control")
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be("no-store");
        }
    }
}

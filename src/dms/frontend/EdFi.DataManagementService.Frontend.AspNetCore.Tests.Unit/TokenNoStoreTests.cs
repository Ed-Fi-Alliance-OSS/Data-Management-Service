// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Every DMS <c>/oauth/token</c> response carries exactly one <c>Cache-Control: no-store</c> and
/// <c>Pragma: no-cache</c> (RFC 6749 section 5.1), including the 2xx shapes
/// <see cref="Infrastructure.SecurityHeadersMiddleware"/> deliberately leaves alone. Non-token
/// surfaces keep their existing behavior.
/// </summary>
[TestFixture]
[NonParallelizable]
public class TokenNoStoreTests
{
    private static HttpRequestMessage TokenRequest(string path, string requestContent, string contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("clientId:clientSecret"));
        request.Headers.Add("Authorization", $"Basic {encodedCredentials}");
        request.Content = new StringContent(requestContent, Encoding.UTF8, contentType);
        return request;
    }

    private static HttpRequestMessage JsonRequest(string path = "/oauth/token") =>
        TokenRequest(path, """{"grant_type":"client_credentials"}""", "application/json");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            return await client.SendAsync(request);
        }
    }

    private static void AssertNoStoreWithPragma(HttpResponseMessage response)
    {
        response.Headers.GetValues("Cache-Control").Should().ContainSingle().Which.Should().Be("no-store");
        response.Headers.GetValues("Pragma").Should().ContainSingle().Which.Should().Be("no-cache");
    }

    /// <summary>
    /// Owns the factory, client and response lifetime. Each scenario states its environment, route
    /// qualifier, manager result and request; derived fixtures only assert.
    /// </summary>
    public abstract class TokenNoStoreScenario
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        protected HttpResponseMessage Response { get; private set; } = null!;

        protected virtual HttpStatusCode ManagerStatus => HttpStatusCode.OK;

        protected virtual string EnvironmentName => "Test";

        protected virtual bool RouteQualified => false;

        protected abstract Task<HttpResponseMessage> SendRequest(HttpClient client);

        [OneTimeSetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(ManagerStatus, EnvironmentName, RouteQualified);
            _client = _factory.CreateClient();
            Response = await SendRequest(_client);
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            Response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        private static WebApplicationFactory<Program> CreateFactory(
            HttpStatusCode managerStatus,
            string environment,
            bool routeQualified
        )
        {
            var oAuthManager = A.Fake<IOAuthManager>();
            A.CallTo(() =>
                    oAuthManager.GetAccessTokenAsync(
                        A<IHttpClientWrapper>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<TokenRequestCredentials>.Ignored,
                        A<string>.Ignored,
                        A<TraceId>.Ignored
                    )
                )
                .ReturnsLazily(() =>
                    new HttpResponseMessage(managerStatus)
                    {
                        Content = new StringContent(
                            """{"token":"fake_access_token","token_type":"bearer","expires_in":300}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    }
                );

            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureServices(services =>
                {
                    TestMockHelper.AddEssentialMocks(services);
                    services.AddTransient(_ => oAuthManager);
                    if (routeQualified)
                    {
                        services.Configure<AppSettings>(opts =>
                        {
                            opts.MultiTenancy = true;
                            opts.RouteQualifierSegments = "schoolYear";
                        });
                    }
                });
            });
        }
    }

    [TestFixture]
    public class Given_A_Successful_Form_Token_Request : TokenNoStoreScenario
    {
        protected override Task<HttpResponseMessage> SendRequest(HttpClient client) =>
            SendAsync(
                client,
                TokenRequest(
                    "/oauth/token",
                    "grant_type=client_credentials",
                    "application/x-www-form-urlencoded"
                )
            );

        [Test]
        public void It_returns_ok()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_is_marked_no_store_with_pragma_no_cache()
        {
            AssertNoStoreWithPragma(Response);
        }
    }

    [TestFixture]
    public class Given_A_Successful_Json_Token_Request : TokenNoStoreScenario
    {
        protected override Task<HttpResponseMessage> SendRequest(HttpClient client) =>
            SendAsync(client, JsonRequest());

        [Test]
        public void It_returns_ok()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_is_marked_no_store_with_pragma_no_cache()
        {
            AssertNoStoreWithPragma(Response);
        }
    }

    [TestFixture]
    public class Given_The_Manager_Returns_Unauthorized : TokenNoStoreScenario
    {
        protected override HttpStatusCode ManagerStatus => HttpStatusCode.Unauthorized;

        protected override Task<HttpResponseMessage> SendRequest(HttpClient client) =>
            SendAsync(client, JsonRequest());

        [Test]
        public void It_returns_unauthorized()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public void It_is_marked_no_store_with_pragma_no_cache()
        {
            AssertNoStoreWithPragma(Response);
        }
    }

    [TestFixture]
    public class Given_A_Route_Qualified_Token_Request : TokenNoStoreScenario
    {
        protected override bool RouteQualified => true;

        protected override Task<HttpResponseMessage> SendRequest(HttpClient client) =>
            SendAsync(client, JsonRequest("/tenant1/2026/oauth/token"));

        [Test]
        public void It_returns_ok()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_is_marked_no_store_with_pragma_no_cache()
        {
            AssertNoStoreWithPragma(Response);
        }
    }

    [TestFixture]
    public class Given_The_Real_Rate_Limiter_Rejects_A_Second_Token_Request : TokenNoStoreScenario
    {
        // The TestRateLimit environment permits one request per sixty-second window (see
        // RateLimitTests.cs). The first request consumes the window against a plain route, so the
        // second, to the token route, is rejected by the real global limiter before dispatch.
        protected override string EnvironmentName => "TestRateLimit";

        protected override async Task<HttpResponseMessage> SendRequest(HttpClient client)
        {
            using var firstResponse = await client.GetAsync("/health");
            return await SendAsync(client, JsonRequest());
        }

        [Test]
        public void It_rejects_the_second_request_with_too_many_requests()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Test]
        public void It_is_marked_no_store_with_pragma_no_cache()
        {
            AssertNoStoreWithPragma(Response);
        }
    }

    [TestFixture]
    public class Given_A_Get_To_The_Token_Endpoint : TokenNoStoreScenario
    {
        protected override Task<HttpResponseMessage> SendRequest(HttpClient client) =>
            client.GetAsync("/oauth/token");

        [Test]
        public void It_returns_method_not_allowed()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }

        [Test]
        public void It_keeps_the_non_2xx_no_store_without_pragma()
        {
            Response
                .Headers.GetValues("Cache-Control")
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be("no-store");
            Response.Headers.Contains("Pragma").Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_A_Get_To_The_Discovery_Root : TokenNoStoreScenario
    {
        protected override Task<HttpResponseMessage> SendRequest(HttpClient client) => client.GetAsync("/");

        [Test]
        public void It_returns_ok()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_carries_neither_cache_control_nor_pragma()
        {
            Response.Headers.Contains("Cache-Control").Should().BeFalse();
            Response.Headers.Contains("Pragma").Should().BeFalse();
        }
    }
}

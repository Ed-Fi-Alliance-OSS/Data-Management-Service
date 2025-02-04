// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class TokenEndpointModuleTests
{
    public static HttpRequestMessage ProxyRequest(string requestContent, string contentType)
    {
        var proxyRequest = new HttpRequestMessage(HttpMethod.Post, "/oauth/token");
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("clientId:clientSecret"));
        proxyRequest.Headers.Add("Authorization", $"Basic {encodedCredentials}");
        proxyRequest!.Content = new StringContent(requestContent, Encoding.UTF8, contentType);
        return proxyRequest;
    }

    [TestFixture]
    public class When_Posting_To_The_Internal_Token_Endpoint_With_Json_Content : TokenEndpointModuleTests
    {
        private JsonNode? _jsonContent;
        private HttpResponseMessage? _response;

        [SetUp]
        public void SetUp()
        {
            // Arrange
            var oAuthManager = A.Fake<IOAuthManager>();
            var json =
                """{"status_code":200, "body":{"token":"fake_access_token","token_type":"bearer","expires_in":300}}""";
            JsonNode _fake_responseJson = JsonNode.Parse(json)!;
            var _fake_response_200 = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_fake_responseJson.ToString(), Encoding.UTF8, "application/json"),
            };

            A.CallTo(
                    () =>
                        oAuthManager.GetAccessTokenAsync(
                            A<IHttpClientWrapper>.Ignored,
                            A<string>.Ignored,
                            A<string>.Ignored,
                            A<string>.Ignored,
                            A<TraceId>.Ignored
                        )
                )
                .Returns(_fake_response_200);

            var claimSetCacheService = A.Fake<IClaimSetCacheService>();
            A.CallTo(() => claimSetCacheService.GetClaimSets()).Returns([]);
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => oAuthManager);
                        collection.AddTransient((x) => claimSetCacheService);
                    }
                );
            });
            using var client = factory.CreateClient();
            var requestContent = """{"grant_type":"client_credentials"}""";
            var proxyRequest = ProxyRequest(requestContent, "application/json");

            // Act
            _response = client.SendAsync(proxyRequest).GetAwaiter().GetResult();
            var content = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _jsonContent = JsonNode.Parse(content) ?? throw new Exception("JSON parsing failed");
        }

        [TearDown]
        public void TearDownAttribute()
        {
            _response?.Dispose();
        }

        [Test]
        public void Then_it_returns_the_upstream_response_code()
        {
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void Then_it_returns_the_upstream_response_body()
        {
            _jsonContent?["access_token"]?.ToString().Should().Be("fake_access_token");
            _jsonContent?["expires_in"]?.Should().Be(300);
            _jsonContent?["token_type"]?.ToString().Should().Be("bearer");
        }
    }

    [TestFixture]
    public class When_Posting_To_The_Internal_Token_Endpoint_With_Form_Content : TokenEndpointModuleTests
    {
        private JsonNode? _jsonContent;
        private HttpResponseMessage? _response;

        [SetUp]
        public void SetUp()
        {
            // Arrange
            var oAuthManager = A.Fake<IOAuthManager>();
            var json =
                """{"status_code":200, "body":{"token":"fake_access_token","token_type":"bearer","expires_in":300}}""";
            JsonNode _fake_responseJson = JsonNode.Parse(json)!;
            var _fake_response_200 = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_fake_responseJson.ToString(), Encoding.UTF8, "application/json"),
            };

            A.CallTo(
                    () =>
                        oAuthManager.GetAccessTokenAsync(
                            A<IHttpClientWrapper>.Ignored,
                            A<string>.Ignored,
                            A<string>.Ignored,
                            A<string>.Ignored,
                            A<TraceId>.Ignored
                        )
                )
                .Returns(_fake_response_200);

            var claimSetCacheService = A.Fake<IClaimSetCacheService>();
            A.CallTo(() => claimSetCacheService.GetClaimSets()).Returns([]);
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => oAuthManager);
                        collection.AddTransient((x) => claimSetCacheService);
                    }
                );
            });
            using var client = factory.CreateClient();
            var requestContent = "grant_type=client_credentials";
            var proxyRequest = ProxyRequest(requestContent, "application/x-www-form-urlencoded");

            // Act
            _response = client.SendAsync(proxyRequest).GetAwaiter().GetResult();
            var content = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _jsonContent = JsonNode.Parse(content) ?? throw new Exception("JSON parsing failed");
        }

        [TearDown]
        public void TearDownAttribute()
        {
            _response?.Dispose();
        }

        [Test]
        public void Then_it_returns_the_upstream_response_code()
        {
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void Then_it_returns_the_upstream_response_body()
        {
            _jsonContent?["access_token"]?.ToString().Should().Be("fake_access_token");
            _jsonContent?["expires_in"]?.Should().Be(300);
            _jsonContent?["token_type"]?.ToString().Should().Be("bearer");
        }
    }
}

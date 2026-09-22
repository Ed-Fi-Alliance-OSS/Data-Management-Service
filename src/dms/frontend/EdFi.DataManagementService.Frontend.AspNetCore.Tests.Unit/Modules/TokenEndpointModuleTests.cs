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
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
#pragma warning disable S1128
using Microsoft.Extensions.Options;
#pragma warning restore S1128
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
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

            A.CallTo(() =>
                    oAuthManager.GetAccessTokenAsync(
                        A<IHttpClientWrapper>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<TraceId>.Ignored
                    )
                )
                .Returns(_fake_response_200);

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => oAuthManager);
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
            _jsonContent!["status_code"]!.GetValue<int>().Should().Be(200);
            _jsonContent["body"]!["token"]!.ToString().Should().Be("fake_access_token");
            _jsonContent["body"]!["expires_in"]!.GetValue<int>().Should().Be(300);
            _jsonContent["body"]!["token_type"]!.ToString().Should().Be("bearer");
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

            A.CallTo(() =>
                    oAuthManager.GetAccessTokenAsync(
                        A<IHttpClientWrapper>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<TraceId>.Ignored
                    )
                )
                .Returns(_fake_response_200);

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => oAuthManager);
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
            _jsonContent!["status_code"]!.GetValue<int>().Should().Be(200);
            _jsonContent["body"]!["token"]!.ToString().Should().Be("fake_access_token");
            _jsonContent["body"]!["expires_in"]!.GetValue<int>().Should().Be(300);
            _jsonContent["body"]!["token_type"]!.ToString().Should().Be("bearer");
        }
    }

    [TestFixture]
    public class When_Posting_To_The_Internal_Token_Endpoint_With_Multi_Tenancy_Enabled
        : TokenEndpointModuleTests
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

            A.CallTo(() =>
                    oAuthManager.GetAccessTokenAsync(
                        A<IHttpClientWrapper>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<TraceId>.Ignored
                    )
                )
                .Returns(_fake_response_200);

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => oAuthManager);
                        collection.Configure<AppSettings>(opts =>
                        {
                            opts.MultiTenancy = true;
                            opts.RouteQualifierSegments = "schoolYear";
                        });
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
            _jsonContent!["status_code"]!.GetValue<int>().Should().Be(200);
            _jsonContent["body"]!["token"]!.ToString().Should().Be("fake_access_token");
            _jsonContent["body"]!["expires_in"]!.GetValue<int>().Should().Be(300);
            _jsonContent["body"]!["token_type"]!.ToString().Should().Be("bearer");
        }
    }

    [TestFixture]
    public class When_Posting_To_Qualified_Internal_Token_Endpoint_With_Json_Content
        : TokenEndpointModuleTests
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

            A.CallTo(() =>
                    oAuthManager.GetAccessTokenAsync(
                        A<IHttpClientWrapper>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<TraceId>.Ignored
                    )
                )
                .Returns(_fake_response_200);

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => oAuthManager);
                        // Configure AppSettings for multitenancy and route qualifiers
                        collection.Configure<AppSettings>(opts =>
                        {
                            opts.MultiTenancy = true;
                            opts.RouteQualifierSegments = "schoolYear";
                        });
                    }
                );
            });
            using var client = factory.CreateClient();
            var requestContent = """{"grant_type":"client_credentials"}""";
            var proxyRequest = ProxyRequest(requestContent, "application/json");
            // Set request URI to qualified path
            proxyRequest.RequestUri = new Uri(client.BaseAddress!, "/tenant1/2026/oauth/token");

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
            _jsonContent!["status_code"]!.GetValue<int>().Should().Be(200);
            _jsonContent["body"]!["token"]!.ToString().Should().Be("fake_access_token");
            _jsonContent["body"]!["expires_in"]!.GetValue<int>().Should().Be(300);
            _jsonContent["body"]!["token_type"]!.ToString().Should().Be("bearer");
        }
    }

    [TestFixture]
    public class When_Posting_To_Qualified_Internal_Token_Endpoint_With_Form_Content
        : TokenEndpointModuleTests
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

            A.CallTo(() =>
                    oAuthManager.GetAccessTokenAsync(
                        A<IHttpClientWrapper>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<TraceId>.Ignored
                    )
                )
                .Returns(_fake_response_200);

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => oAuthManager);
                        // Configure AppSettings for multitenancy and route qualifiers
                        collection.Configure<AppSettings>(opts =>
                        {
                            opts.MultiTenancy = true;
                            opts.RouteQualifierSegments = "schoolYear";
                        });
                    }
                );
            });
            using var client = factory.CreateClient();
            var requestContent = "grant_type=client_credentials";
            var proxyRequest = ProxyRequest(requestContent, "application/x-www-form-urlencoded");
            // Set request URI to qualified path
            proxyRequest.RequestUri = new Uri(client.BaseAddress!, "/tenant1/2026/oauth/token");

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
            _jsonContent!["status_code"]!.GetValue<int>().Should().Be(200);
            _jsonContent["body"]!["token"]!.ToString().Should().Be("fake_access_token");
            _jsonContent["body"]!["expires_in"]!.GetValue<int>().Should().Be(300);
            _jsonContent["body"]!["token_type"]!.ToString().Should().Be("bearer");
        }
    }

    [TestFixture]
    public class When_Posting_To_Qualified_Internal_Token_Endpoint_When_Tenant_Validator_Returns_False
        : TokenEndpointModuleTests
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

            A.CallTo(() =>
                    oAuthManager.GetAccessTokenAsync(
                        A<IHttpClientWrapper>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<TraceId>.Ignored
                    )
                )
                .Returns(_fake_response_200);

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => oAuthManager);
                        collection.AddTransient<ITenantValidator>(_ =>
                        {
                            var tenantValidator = A.Fake<ITenantValidator>();
                            A.CallTo(() => tenantValidator.ValidateTenantAsync(A<string>.Ignored))
                                .Returns(false);
                            return tenantValidator;
                        });
                        // Configure AppSettings for multitenancy and route qualifiers
                        collection.Configure<AppSettings>(opts =>
                        {
                            opts.MultiTenancy = true;
                            opts.RouteQualifierSegments = "schoolYear";
                        });
                    }
                );
            });
            using var client = factory.CreateClient();
            var requestContent = "{\"grant_type\":\"client_credentials\"}";
            var proxyRequest = ProxyRequest(requestContent, "application/json");
            // Set request URI to qualified path
            proxyRequest.RequestUri = new Uri(client.BaseAddress!, "/tenant1/2026/oauth/token");

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
        public void Then_it_still_returns_the_upstream_response_code()
        {
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void Then_it_still_returns_the_upstream_response_body()
        {
            _jsonContent!["status_code"]!.GetValue<int>().Should().Be(200);
            _jsonContent["body"]!["token"]!.ToString().Should().Be("fake_access_token");
            _jsonContent["body"]!["expires_in"]!.GetValue<int>().Should().Be(300);
            _jsonContent["body"]!["token_type"]!.ToString().Should().Be("bearer");
        }
    }

    /// <summary>
    /// Sends a token request through the real endpoint with <see cref="IOAuthManager"/> faked to
    /// return a problem response exactly as <c>OAuthManager.GenerateProblemDetailResponse</c> builds it.
    /// </summary>
    private static HttpResponseMessage SendThroughProxy(HttpStatusCode statusCode, JsonNode problemDetails)
    {
        var oAuthManager = A.Fake<IOAuthManager>();
        A.CallTo(() =>
                oAuthManager.GetAccessTokenAsync(
                    A<IHttpClientWrapper>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<TraceId>.Ignored
                )
            )
            .Returns(
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(
                        problemDetails.ToString(),
                        Encoding.UTF8,
                        "application/problem+json"
                    ),
                }
            );

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddTransient(_ => oAuthManager);
            });
        });
        using var client = factory.CreateClient();

        return client
            .SendAsync(ProxyRequest("""{"grant_type":"client_credentials"}""", "application/json"))
            .GetAwaiter()
            .GetResult();
    }

    [TestFixture]
    public class Given_An_Upstream_Token_Limit_Rejection : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public void SetUp()
        {
            _response = SendThroughProxy(
                HttpStatusCode.TooManyRequests,
                FailureResponse.ForTooManyTokens(
                    new TraceId("token-limit-trace"),
                    [
                        "Too many access tokens have been requested (limit is 5). Access tokens should be reused until they expire.",
                    ]
                )
            );
            _body = JsonNode.Parse(_response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_responds_with_too_many_requests()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Test]
        public void It_keeps_the_problem_json_media_type()
        {
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_passes_the_problem_details_body_through()
        {
            _body["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:security:authentication:too-many-tokens");
        }
    }

    [TestFixture]
    public class Given_An_Upstream_Lock_Contention_Answered_As_Service_Unavailable : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public void SetUp()
        {
            _response = SendThroughProxy(
                HttpStatusCode.ServiceUnavailable,
                FailureResponse.ForServiceUnavailable(new TraceId("lock-contention-trace"))
            );
            _body = JsonNode.Parse(_response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_responds_with_service_unavailable()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        [Test]
        public void It_keeps_the_problem_json_media_type()
        {
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_passes_the_problem_details_body_through()
        {
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:service-unavailable");
        }
    }
}

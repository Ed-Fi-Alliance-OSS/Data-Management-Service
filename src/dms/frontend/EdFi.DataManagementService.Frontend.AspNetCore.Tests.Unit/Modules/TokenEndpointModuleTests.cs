// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
                        A<TokenRequestCredentials>.Ignored,
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
                        A<TokenRequestCredentials>.Ignored,
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

    [TestFixture(false)]
    [TestFixture(true)]
    public class When_Posting_To_The_Unqualified_Internal_Token_Endpoint_With_Route_Contexts
        : TokenEndpointModuleTests
    {
        private readonly bool _multiTenancy;
        private JsonNode? _jsonContent;
        private HttpResponseMessage? _response;

        public When_Posting_To_The_Unqualified_Internal_Token_Endpoint_With_Route_Contexts(bool multiTenancy)
        {
            _multiTenancy = multiTenancy;
        }

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
                        A<TokenRequestCredentials>.Ignored,
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
                            opts.MultiTenancy = _multiTenancy;
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
                        A<TokenRequestCredentials>.Ignored,
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
                        A<TokenRequestCredentials>.Ignored,
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
                        A<TokenRequestCredentials>.Ignored,
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

    [TestFixture]
    public class Given_An_Upstream_Token_Limit_Rejection : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _) = SendThroughRealManager(
                ProxyRequest("grant_type=client_credentials", "application/x-www-form-urlencoded"),
                HttpStatusCode.TooManyRequests,
                """{"type":"urn:ed-fi:management-api:security:authentication:too-many-tokens","status":429,"errors":["Too many access tokens have been requested (limit is 5). Access tokens should be reused until they expire."]}"""
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
        public void It_delivers_the_oauth_json_media_type()
        {
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        }

        [Test]
        public void It_delivers_the_oauth_error_body()
        {
            _body
                .AsObject()
                .Select(member => member.Key)
                .Should()
                .BeEquivalentTo("error", "error_description");
            _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(
                    "Too many access tokens have been requested (limit is 5). Access tokens should be reused until they expire."
                );
            _response.Headers.WwwAuthenticate.Should().BeEmpty();
            _response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle();
        }
    }

    /// <summary>
    /// What the endpoint handed to <see cref="IOAuthManager"/>, captured from one call.
    /// </summary>
    private sealed record ManagerCall(
        string GrantType,
        string? AuthHeader,
        TokenRequestCredentials BodyCredentials
    );

    /// <summary>
    /// Sends <paramref name="request"/> through the real endpoint with <see cref="IOAuthManager"/>
    /// faked to answer 200, and returns the response together with every call the manager received.
    /// When <paramref name="loggerProvider"/> is given it receives every log entry from every category,
    /// framework categories included, at Trace level.
    /// </summary>
    private static (HttpResponseMessage Response, List<ManagerCall> Calls) SendCapturingManagerCalls(
        HttpRequestMessage request,
        string environment = "Test",
        ILoggerProvider? loggerProvider = null,
        string correlationHeader = ""
    )
    {
        List<ManagerCall> calls = [];
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
            .Invokes(
                (
                    IHttpClientWrapper _,
                    string grantType,
                    string? authHeader,
                    TokenRequestCredentials bodyCredentials,
                    string _,
                    TraceId _
                ) => calls.Add(new ManagerCall(grantType, authHeader, bodyCredentials))
            )
            .ReturnsLazily(() =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                }
            );

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // Only appsettings.Test.json carries the settings that startup validation requires, so
            // the Production and Development hosts are given the same ones.
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:AuthenticationService"] = "http://dev-test-auth-service",
                            ["AppSettings:ApiSchema:UseCustomPath"] = "false",
                            ["AppSettings:BypassAuthorization"] = "true",
                            ["AppSettings:CorrelationIdHeader"] = correlationHeader,
                            ["JwtAuthentication:MetadataAddress"] =
                                "http://test-idp/.well-known/openid-configuration",
                            ["JwtAuthentication:RequireHttpsMetadata"] = "false",
                            ["ConfigurationServiceSettings:ClientSecret"] = "test-client-secret",
                            ["ConfigurationServiceSettings:EncryptionKey"] =
                                "TestEncryptionKey123456789012345678901234567890",
                        }
                    )
            );
            if (loggerProvider is not null)
            {
                builder.ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddFilter(null, LogLevel.Trace);
                    logging.AddProvider(loggerProvider);
                });
            }
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddTransient(_ => oAuthManager);
            });
        });
        using var client = factory.CreateClient();

        return (client.SendAsync(request).GetAwaiter().GetResult(), calls);
    }

    /// <summary>
    /// Records the formatted message and the full exception text of every log entry it is given.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyCollection<string> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);

        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => entries.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }

    /// <summary>
    /// The unquoted value starts with <c>t</c>, so System.Text.Json reports it as an invalid literal
    /// and quotes the rest of the value in its exception message.
    /// </summary>
    private const string LeakSentinel = "topSECRETvalue";

    [TestFixture("Production")]
    [TestFixture("Development")]
    public class Given_A_Malformed_Json_Body_With_An_Unquoted_Secret(string environment)
        : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<ManagerCall> _calls = null!;
        private RecordingLoggerProvider _logs = null!;

        [SetUp]
        public void SetUp()
        {
            _logs = new RecordingLoggerProvider();
            (_response, _calls) = SendCapturingManagerCalls(
                UnauthenticatedRequest(
                    $$"""{"grant_type":"client_credentials","client_id":"x","client_secret": {{LeakSentinel}}}""",
                    "application/json"
                ),
                environment,
                _logs
            );
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _logs.Dispose();
        }

        [Test]
        public void It_responds_with_bad_request()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void It_does_not_call_the_manager()
        {
            _calls.Should().BeEmpty();
        }

        [Test]
        public void It_does_not_log_any_of_the_body()
        {
            _logs.Entries.Should().NotBeEmpty("the recording provider must have seen the request");
            _logs.Entries.Should().NotContain(entry => entry.Contains(LeakSentinel));
        }
    }

    [TestFixture("""{"grant_type":"client_credentials","client_id":12345}""", "Production")]
    [TestFixture("""{"grant_type":"client_credentials","client_id":12345}""", "Development")]
    [TestFixture("", "Production")]
    [TestFixture("", "Development")]
    [TestFixture("null", "Production")]
    [TestFixture("null", "Development")]
    public class Given_A_Json_Body_That_Is_Not_A_Token_Request(string requestContent, string environment)
        : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<ManagerCall> _calls = null!;
        private string _responseBody = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _calls) = SendCapturingManagerCalls(
                UnauthenticatedRequest(requestContent, "application/json"),
                environment
            );
            _responseBody = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_responds_with_bad_request()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void It_returns_the_oauth_invalid_request_contract()
        {
            JsonNode body = JsonNode.Parse(_responseBody)!;
            body.AsObject()
                .Select(property => property.Key)
                .Should()
                .BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be("invalid_request");
            body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("The token request body must be a valid JSON object with string-valued fields");
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            _response.Headers.Contains(OAuthErrorResponse.CorrelationHeaderName).Should().BeTrue();
        }

        [Test]
        public void It_does_not_call_the_manager()
        {
            _calls.Should().BeEmpty();
        }
    }

    /// <summary>
    /// A token request with no <c>Authorization</c> header.
    /// </summary>
    private static HttpRequestMessage UnauthenticatedRequest(string requestContent, string contentType) =>
        new(HttpMethod.Post, "/oauth/token")
        {
            Content = new StringContent(requestContent, Encoding.UTF8, contentType),
        };

    [TestFixture(
        "client_id=my-client&client_secret=my-secret&grant_type=client_credentials",
        "application/x-www-form-urlencoded"
    )]
    [TestFixture(
        """{"client_id":"my-client","client_secret":"my-secret","grant_type":"client_credentials"}""",
        "application/json"
    )]
    [TestFixture(
        """{"Client_Id":"my-client","CLIENT_SECRET":"my-secret","Grant_Type":"client_credentials"}""",
        "application/json"
    )]
    [TestFixture(
        "Client_Id=my-client&CLIENT_SECRET=my-secret&Grant_Type=client_credentials",
        "application/x-www-form-urlencoded"
    )]
    public class Given_Body_Credentials_And_No_Authorization_Header(string requestContent, string contentType)
        : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<ManagerCall> _calls = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _calls) = SendCapturingManagerCalls(
                UnauthenticatedRequest(requestContent, contentType)
            );
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_returns_the_manager_response_code()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_passes_the_body_credentials_to_the_manager()
        {
            _calls
                .Should()
                .ContainSingle()
                .Which.BodyCredentials.Should()
                .Be(new TokenRequestCredentials("my-client", "my-secret"));
        }

        [Test]
        public void It_passes_the_grant_type_to_the_manager()
        {
            _calls.Should().ContainSingle().Which.GrantType.Should().Be("client_credentials");
        }

        [Test]
        public void It_tells_the_manager_there_was_no_authorization_header()
        {
            _calls.Should().ContainSingle().Which.AuthHeader.Should().BeNull();
        }
    }

    /// <summary>
    /// Regression guard: the existing header-only request types still reach the manager with no body
    /// credentials, including JSON with a Basic header.
    /// </summary>
    [TestFixture("grant_type=client_credentials", "application/x-www-form-urlencoded")]
    [TestFixture("""{"grant_type":"client_credentials"}""", "application/json")]
    public class Given_A_Basic_Authorization_Header_And_No_Body_Credentials(
        string requestContent,
        string contentType
    ) : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<ManagerCall> _calls = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _calls) = SendCapturingManagerCalls(ProxyRequest(requestContent, contentType));
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_passes_no_body_credentials_to_the_manager()
        {
            _calls.Should().ContainSingle().Which.BodyCredentials.Should().Be(TokenRequestCredentials.None);
        }

        [Test]
        public void It_passes_the_authorization_header_to_the_manager()
        {
            string expected =
                $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("clientId:clientSecret"))}";
            _calls.Should().ContainSingle().Which.AuthHeader.Should().Be(expected);
        }
    }

    /// <summary>
    /// RFC 6749 section 3.2: a parameter sent without a value is treated as omitted, so an empty
    /// body field, form or JSON, reaches the manager as no field at all.
    /// </summary>
    [TestFixture("client_id=&grant_type=client_credentials", "application/x-www-form-urlencoded")]
    [TestFixture("""{"client_id":"","grant_type":"client_credentials"}""", "application/json")]
    [TestFixture(
        "client_id=&client_secret=&grant_type=client_credentials",
        "application/x-www-form-urlencoded"
    )]
    [TestFixture(
        """{"client_id":"","client_secret":"","grant_type":"client_credentials"}""",
        "application/json"
    )]
    public class Given_Empty_Body_Credential_Fields(string requestContent, string contentType)
        : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<ManagerCall> _calls = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _calls) = SendCapturingManagerCalls(ProxyRequest(requestContent, contentType));
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_passes_the_empty_fields_as_absent()
        {
            _calls.Should().ContainSingle().Which.BodyCredentials.Should().Be(TokenRequestCredentials.None);
        }
    }

    /// <summary>
    /// What the real <see cref="OAuthManager"/> sent upstream, read inside the handler before the
    /// request could be disposed.
    /// </summary>
    private sealed record UpstreamCall(string? Authorization, string Body, string[] ForwardedFor);

    /// <summary>
    /// Records every upstream token request and returns the selected provider response.
    /// </summary>
    private sealed class RecordingUpstreamHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public List<UpstreamCall> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls.Add(
                new UpstreamCall(
                    request.Headers.Authorization?.ToString(),
                    request.Content is null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync(cancellationToken),
                    request.Headers.TryGetValues("X-Forwarded-For", out var sources) ? sources.ToArray() : []
                )
            );
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    [TestFixture("198.51.100.1", "198.51.100.1")]
    [TestFixture("198.51.100.2", "198.51.100.2")]
    [TestFixture("::ffff:198.51.100.1", "198.51.100.1")]
    [TestFixture("2001:db8::1", "2001:db8::1")]
    [TestFixture(null, "0.0.0.0")]
    public class Given_a_token_proxy_source(string? remoteIp, string expectedSource)
        : TokenEndpointModuleTests
    {
        private List<UpstreamCall> _calls = [];

        [SetUp]
        public async Task Setup()
        {
            using RecordingUpstreamHandler handler = new();
            IHttpClientFactory clientFactory = A.Fake<IHttpClientFactory>();
            A.CallTo(() => clientFactory.CreateClient(A<string>._))
                .ReturnsLazily(() =>
                {
                    HttpClient client = new(handler, disposeHandler: false);
                    client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.200");
                    return client;
                });
            foreach (string forgedSource in new[] { "203.0.113.1", "203.0.113.2, 203.0.113.3" })
            {
                DefaultHttpContext context = new();
                context.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);
                context.Request.Method = "POST";
                context.Request.ContentType = "application/x-www-form-urlencoded";
                context.Request.Headers["X-Forwarded-For"] = forgedSource;
                context.Request.Body = new MemoryStream(
                    Encoding.UTF8.GetBytes(
                        "client_id=my-client&client_secret=my-secret&grant_type=client_credentials"
                    )
                );
                context.Response.Body = new MemoryStream();
                await AspNetCore.Modules.TokenEndpointModule.HandleFormData(
                    context,
                    Options.Create(
                        new AppSettings
                        {
                            AuthenticationService = "http://cms/connect/token",
                            Datastore = "postgresql",
                            CorrelationIdHeader = "",
                        }
                    ),
                    new OAuthManager(NullLogger<OAuthManager>.Instance),
                    NullLogger<AspNetCore.Modules.TokenEndpointModule>.Instance,
                    clientFactory
                );
            }
            _calls = handler.Calls;
        }

        [Test]
        public void It_replaces_forwarding_values_with_only_the_trusted_connection_source()
        {
            _calls.Should().HaveCount(2);
            foreach (UpstreamCall call in _calls)
            {
                call.ForwardedFor.Should().Equal(expectedSource);
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="request"/> through the real endpoint and the real
    /// <see cref="OAuthManager"/>, with only the upstream HTTP call faked, so a test sees what the
    /// whole chain forwards upstream.
    /// </summary>
    private static (HttpResponseMessage Response, List<UpstreamCall> UpstreamCalls) SendThroughRealManager(
        HttpRequestMessage request,
        HttpStatusCode upstreamStatus = HttpStatusCode.OK,
        string upstreamBody = "{}"
    )
    {
        RecordingUpstreamHandler handler = new(upstreamStatus, upstreamBody);
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>.Ignored))
            .ReturnsLazily(() => new HttpClient(handler, disposeHandler: false));

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddTransient<IOAuthManager, OAuthManager>();
                collection.AddSingleton(httpClientFactory);
            });
        });
        using var client = factory.CreateClient();

        return (client.SendAsync(request).GetAwaiter().GetResult(), handler.Calls);
    }

    [Test]
    public async Task It_relays_manager_owned_token_error_headers()
    {
        var oAuthManager = A.Fake<IOAuthManager>();
        var tokenError = OAuthErrorResponse.Create(
            HttpStatusCode.Unauthorized,
            "invalid_client",
            "Credentials rejected",
            new TraceId("a/b+c")
        );
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
            .Returns(tokenError);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.AddTransient(_ => oAuthManager);
            });
        });
        using var client = factory.CreateClient();
        using var request = ProxyRequest(
            "grant_type=client_credentials",
            "application/x-www-form-urlencoded"
        );

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be(OAuthErrorResponse.BasicChallenge);
        response.Headers.GetValues(OAuthErrorResponse.CorrelationHeaderName).Should().Equal("a%2Fb%2Bc");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [TestCase(HttpStatusCode.Unauthorized, true, 401)]
    [TestCase(HttpStatusCode.Unauthorized, false, 401)]
    [TestCase(HttpStatusCode.BadRequest, true, 401)]
    [TestCase(HttpStatusCode.BadRequest, false, 400)]
    public async Task It_delivers_provider_invalid_client_using_the_original_caller_mechanism(
        HttpStatusCode providerStatus,
        bool useBasic,
        int expectedStatus
    )
    {
        using var request = ProxyRequest(
            useBasic
                ? "grant_type=client_credentials"
                : "grant_type=client_credentials&client_id=client&client_secret=secret",
            "application/x-www-form-urlencoded"
        );
        request.Headers.Remove("Authorization");
        if (useBasic)
        {
            request.Headers.Add("Authorization", "Basic Y2xpZW50OnNlY3JldA==");
        }
        (HttpResponseMessage response, _) = SendThroughRealManager(
            request,
            providerStatus,
            """{"error":"invalid_client","error_description":"Provider rejected credentials"}"""
        );
        using (response)
        {
            ((int)response.StatusCode).Should().Be(expectedStatus);
            response
                .Headers.WwwAuthenticate.ToString()
                .Should()
                .Be(expectedStatus == 401 ? "Basic realm=\"EdFi.DataManagementService\"" : string.Empty);
            response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle();
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            JsonObject body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be("invalid_client");
            body["error_description"]!.GetValue<string>().Should().Be("Provider rejected credentials");
        }
    }

    private static IEnumerable<TestCaseData> TokenCorrelationIds()
    {
        yield return new TestCaseData("percent%plus+slash/ñ", "percent%plus+slash/ñ");
        // Attribute arguments are encoded in metadata; construct the invalid UTF-16 at runtime.
        yield return new TestCaseData("before" + (char)0xd800 + "after", "beforeafter");
        yield return new TestCaseData("emoji-\ud83d\ude00", "emoji-\ud83d\ude00");
    }

    [TestCaseSource(nameof(TokenCorrelationIds))]
    public async Task It_encodes_the_ingested_correlation_id_and_matches_the_request_log(
        string suppliedId,
        string expectedNormalizedId
    )
    {
        using CorrelationIdRecordingLoggerProvider recorder = new();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureLogging(logging => logging.AddProvider(recorder));
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:CorrelationIdHeader"] = "x-token-trace",
                        }
                    )
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.AddTransient<IOAuthManager, OAuthManager>();
            });
        });
        using var client = factory.CreateClient();
        using var request = ProxyRequest(
            "grant_type=client_credentials",
            "application/x-www-form-urlencoded"
        );
        request.Headers.Remove("Authorization");
        // TestServer delivers these values directly to the real ingestion boundary; this also
        // covers invalid UTF-16 normalization without constructing an invalid TraceId ourselves.
        request.Headers.TryAddWithoutValidation("x-token-trace", suppliedId);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string encodedId = response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle().Which;
        encodedId.Should().Be(Uri.EscapeDataString(expectedNormalizedId));
        Uri.UnescapeDataString(encodedId).Should().Be(expectedNormalizedId);
        recorder.LoggedTraceIds.Should().Equal(expectedNormalizedId);
    }

    /// <summary>
    /// A JSON <c>null</c> credential field counts as absent, as the ODS/API treats it as blank, so
    /// it is not a second mechanism beside a Basic header.
    /// </summary>
    [TestFixture]
    public class Given_A_Basic_Authorization_Header_And_A_Json_Null_Client_Id : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<UpstreamCall> _upstreamCalls = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _upstreamCalls) = SendThroughRealManager(
                ProxyRequest("""{"grant_type":"client_credentials","client_id":null}""", "application/json")
            );
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_responds_with_ok()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_forwards_the_callers_basic_header_verbatim()
        {
            string expected =
                $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("clientId:clientSecret"))}";
            _upstreamCalls.Should().ContainSingle().Which.Authorization.Should().Be(expected);
        }

        [Test]
        public void It_sends_only_the_grant_type_upstream()
        {
            _upstreamCalls.Should().ContainSingle().Which.Body.Should().Be("grant_type=client_credentials");
        }
    }

    [TestFixture]
    public class Given_No_Authorization_Header_And_Json_Null_Credentials : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<UpstreamCall> _upstreamCalls = null!;
        private JsonNode _body = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _upstreamCalls) = SendThroughRealManager(
                UnauthenticatedRequest(
                    """{"grant_type":"client_credentials","client_id":null,"client_secret":null}""",
                    "application/json"
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
        public void It_responds_with_unauthorized()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public void It_carries_the_invalid_request_error()
        {
            _body["error"]!.GetValue<string>().Should().Be("invalid_client");
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(OAuthErrorResponse.MissingCredentialsDescription);
        }

        [Test]
        public void It_says_credentials_are_missing()
        {
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(OAuthErrorResponse.MissingCredentialsDescription);
        }

        [Test]
        public void It_sends_nothing_upstream()
        {
            _upstreamCalls.Should().BeEmpty();
        }
    }

    /// <summary>
    /// RFC 6749 section 3.2: empty body fields count as omitted, so beside a Basic header they are
    /// no second mechanism, whichever content type carried them.
    /// </summary>
    [TestFixture(
        "grant_type=client_credentials&client_id=&client_secret=",
        "application/x-www-form-urlencoded"
    )]
    [TestFixture(
        """{"grant_type":"client_credentials","client_id":"","client_secret":""}""",
        "application/json"
    )]
    public class Given_A_Basic_Authorization_Header_And_Empty_Body_Credentials_Through_The_Real_Manager(
        string requestContent,
        string contentType
    ) : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<UpstreamCall> _upstreamCalls = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _upstreamCalls) = SendThroughRealManager(ProxyRequest(requestContent, contentType));
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_responds_with_ok()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_forwards_the_callers_basic_header_verbatim()
        {
            string expected =
                $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("clientId:clientSecret"))}";
            _upstreamCalls.Should().ContainSingle().Which.Authorization.Should().Be(expected);
        }

        [Test]
        public void It_sends_only_the_grant_type_upstream()
        {
            _upstreamCalls.Should().ContainSingle().Which.Body.Should().Be("grant_type=client_credentials");
        }
    }

    /// <summary>
    /// With no header, an empty <c>client_secret</c> leaves a partial pair (400 invalid_request).
    /// Two empty fields leave no credentials (401 invalid_client), for both form and JSON.
    /// </summary>
    [TestFixture(
        "grant_type=client_credentials&client_id=my-client&client_secret=",
        "application/x-www-form-urlencoded"
    )]
    [TestFixture(
        """{"grant_type":"client_credentials","client_id":"my-client","client_secret":""}""",
        "application/json"
    )]
    [TestFixture(
        "grant_type=client_credentials&client_id=&client_secret=",
        "application/x-www-form-urlencoded"
    )]
    [TestFixture(
        """{"grant_type":"client_credentials","client_id":"","client_secret":""}""",
        "application/json"
    )]
    public class Given_No_Authorization_Header_And_Empty_Body_Credentials_Through_The_Real_Manager(
        string requestContent,
        string contentType
    ) : TokenEndpointModuleTests
    {
        private readonly bool _hasPartialCredentials = requestContent.Contains(
            "my-client",
            StringComparison.Ordinal
        );
        private HttpResponseMessage _response = null!;
        private List<UpstreamCall> _upstreamCalls = null!;
        private JsonNode _body = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _upstreamCalls) = SendThroughRealManager(
                UnauthenticatedRequest(requestContent, contentType)
            );
            _body = JsonNode.Parse(_response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_uses_the_status_for_missing_or_partial_credentials()
        {
            _response
                .StatusCode.Should()
                .Be(_hasPartialCredentials ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized);
        }

        [Test]
        public void It_carries_the_matching_oauth_error()
        {
            _body["error"]!
                .GetValue<string>()
                .Should()
                .Be(_hasPartialCredentials ? "invalid_request" : "invalid_client");
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(OAuthErrorResponse.MissingCredentialsDescription);
        }

        [Test]
        public void It_sends_nothing_upstream()
        {
            _upstreamCalls.Should().BeEmpty();
        }
    }

    /// <summary>
    /// A JSON <c>null</c> grant type used to bind straight into a property declared non-nullable,
    /// reach the manager as <c>null</c>, and fail there with a 500. The endpoint now hands the
    /// manager an empty grant type instead.
    /// </summary>
    [TestFixture]
    public class Given_A_Json_Null_Grant_Type : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<ManagerCall> _calls = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _calls) = SendCapturingManagerCalls(
                UnauthenticatedRequest(
                    """{"grant_type":null,"client_id":"my-client","client_secret":"my-secret"}""",
                    "application/json"
                )
            );
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_passes_an_empty_grant_type_to_the_manager()
        {
            _calls.Should().ContainSingle().Which.GrantType.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_A_Json_Null_Grant_Type_Through_The_Real_Manager : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<UpstreamCall> _upstreamCalls = null!;
        private JsonNode _body = null!;

        [SetUp]
        public void SetUp()
        {
            (_response, _upstreamCalls) = SendThroughRealManager(
                UnauthenticatedRequest(
                    """{"grant_type":null,"client_id":"my-client","client_secret":"my-secret"}""",
                    "application/json"
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
        public void It_responds_with_bad_request()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void It_says_the_grant_type_is_unsupported()
        {
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(OAuthErrorResponse.MissingGrantDescription);
            _body["error"]!.GetValue<string>().Should().Be("invalid_request");
        }

        [Test]
        public void It_sends_nothing_upstream()
        {
            _upstreamCalls.Should().BeEmpty();
        }
    }

    /// <summary>
    /// A present but empty <c>Authorization</c> header reaches the manager as an empty string,
    /// distinct from the <c>null</c> an absent header produces, so the manager can reject it as the
    /// ODS/API does (see <c>OAuthManagerTests.Given_An_Empty_Authorization_Header</c>). Kestrel keeps
    /// an empty header value, but the test server's client drops one, so this fixture calls the
    /// handler directly.
    /// </summary>
    [TestFixture]
    public class Given_An_Empty_Authorization_Header_Reaching_The_Form_Handler : TokenEndpointModuleTests
    {
        private List<ManagerCall> _calls = null!;

        [SetUp]
        public async Task SetUp()
        {
            _calls = [];
            var oAuthManager = A.Fake<IOAuthManager>();
            A.CallTo(() =>
                    oAuthManager.GetAccessTokenAsync(
                        A<IHttpClientWrapper>.Ignored,
                        A<string>.Ignored,
                        A<string?>.Ignored,
                        A<TokenRequestCredentials>.Ignored,
                        A<string>.Ignored,
                        A<TraceId>.Ignored
                    )
                )
                .Invokes(
                    (
                        IHttpClientWrapper _,
                        string grantType,
                        string? authHeader,
                        TokenRequestCredentials bodyCredentials,
                        string _,
                        TraceId _
                    ) => _calls.Add(new ManagerCall(grantType, authHeader, bodyCredentials))
                )
                .ReturnsLazily(() =>
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    }
                );

            DefaultHttpContext httpContext = new();
            httpContext.Request.Method = "POST";
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Body = new MemoryStream(
                Encoding.UTF8.GetBytes(
                    "client_id=my-client&client_secret=my-secret&grant_type=client_credentials"
                )
            );
            httpContext.Request.Headers.Authorization = string.Empty;
            httpContext.Response.Body = new MemoryStream();

            await AspNetCore.Modules.TokenEndpointModule.HandleFormData(
                httpContext,
                Options.Create(
                    new AppSettings
                    {
                        AuthenticationService = "http://localhost/connect/token",
                        Datastore = "postgresql",
                        CorrelationIdHeader = "",
                    }
                ),
                oAuthManager,
                NullLogger<AspNetCore.Modules.TokenEndpointModule>.Instance,
                A.Fake<IHttpClientFactory>()
            );
        }

        [Test]
        public void It_passes_an_empty_header_rather_than_no_header()
        {
            _calls.Should().ContainSingle().Which.AuthHeader.Should().NotBeNull().And.BeEmpty();
        }
    }

    /// <summary>
    /// RFC 6749 section 3.2: request parameters must not be included more than once.
    /// </summary>
    [TestFixture("client_id=a&client_id=b&client_secret=s&grant_type=client_credentials")]
    [TestFixture("client_id=a&client_secret=s&client_secret=t&grant_type=client_credentials")]
    [TestFixture("client_id=a&client_secret=s&grant_type=client_credentials&grant_type=client_credentials")]
    [TestFixture("client_id=a&CLIENT_ID=b&client_secret=s&grant_type=client_credentials")]
    [TestFixture("client_id=a&client_secret=s&Client_Secret=t&grant_type=client_credentials")]
    [TestFixture(
        "client_id=a&client_id=b&client_secret=s&grant_type=client_credentials",
        "Bearer some-access-token"
    )]
    public class Given_A_Form_Parameter_Supplied_More_Than_Once(string requestContent, string? authorization)
        : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<ManagerCall> _calls = null!;
        private JsonNode _body = null!;

        public Given_A_Form_Parameter_Supplied_More_Than_Once(string requestContent)
            : this(requestContent, null) { }

        /// <summary>
        /// With a <c>Bearer</c> header, the fixture pins that duplicate detection runs first: the
        /// manager would otherwise answer <c>401 invalid_client</c>.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            HttpRequestMessage request = UnauthenticatedRequest(
                requestContent,
                "application/x-www-form-urlencoded"
            );
            if (authorization is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            (_response, _calls) = SendCapturingManagerCalls(request);
            _body = JsonNode.Parse(_response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_responds_with_bad_request()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void It_answers_with_the_same_content_type_as_the_other_token_problem_responses()
        {
            _response.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
        }

        [Test]
        public void It_says_a_parameter_was_repeated()
        {
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("A request parameter was included more than once");
            _body["error"]!.GetValue<string>().Should().Be("invalid_request");
        }

        [Test]
        public void It_does_not_call_the_manager()
        {
            _calls.Should().BeEmpty();
        }
    }

    [TestFixture(0, "The token request body must be valid form data")]
    [TestFixture(2, "A request parameter was included more than once")]
    [TestFixture(1024, "A request parameter was included more than once")]
    [TestFixture(1025, "The token request body must be valid form data")]
    public class Given_Invalid_Form_Data(int parameterCount, string expectedDescription)
        : TokenEndpointModuleTests
    {
        private HttpResponseMessage _response = null!;
        private List<ManagerCall> _calls = null!;
        private JsonObject _body = null!;
        private RecordingLoggerProvider _recorder = null!;

        [SetUp]
        public void SetUp()
        {
            // At 1024 values parsing succeeds and duplicate validation rejects the request.
            // At 1025 the parser rejects it first; both paths must remain client errors.
            string content =
                parameterCount == 0
                    ? "grant_type=client_credentials&client_id=%00&client_secret=topSECRETvalue"
                    : string.Join('&', Enumerable.Repeat("grant_type=client_credentials", parameterCount));
            using var request = UnauthenticatedRequest(content, "application/x-www-form-urlencoded");
            request.Headers.Add("x-token-trace", "form%trace+id/1");
            _recorder = new RecordingLoggerProvider();
            (_response, _calls) = SendCapturingManagerCalls(
                request,
                loggerProvider: _recorder,
                correlationHeader: "x-token-trace"
            );
            _body = JsonNode
                .Parse(_response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!
                .AsObject();
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _recorder.Dispose();
        }

        [Test]
        public void It_responds_with_bad_request()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void It_delivers_only_the_oauth_error_members_with_a_safe_description()
        {
            _response.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
            _body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            _body["error"]!.GetValue<string>().Should().Be("invalid_request");
            _body["error_description"]!.GetValue<string>().Should().Be(expectedDescription);
            _response.Headers.WwwAuthenticate.Should().BeEmpty();
        }

        [Test]
        public void It_encodes_the_correlation_id_that_is_logged()
        {
            string correlationId = _response
                .Headers.GetValues("X-Correlation-ID")
                .Should()
                .ContainSingle()
                .Which;
            correlationId.Should().Be("form%25trace%2Bid%2F1");
            Uri.UnescapeDataString(correlationId).Should().Be("form%trace+id/1");
            _recorder
                .Entries.Should()
                .Contain(entry => entry.Contains("form%trace+id/1", StringComparison.Ordinal));
        }

        [Test]
        public void It_does_not_log_the_secret_or_a_request_failure()
        {
            _recorder
                .Entries.Should()
                .NotContain(entry => entry.Contains(LeakSentinel, StringComparison.Ordinal));
            _recorder
                .Entries.Should()
                .NotContain(entry => entry.Contains("HttpRequestFailed", StringComparison.Ordinal));
        }

        [Test]
        public void It_does_not_call_the_manager()
        {
            _calls.Should().BeEmpty();
        }
    }

    [TestFixture("io")]
    [TestFixture("cancellation")]
    [TestFixture("body-size")]
    public class Given_A_Form_Read_Failure_That_Is_Not_Invalid_Data(string failure) : TokenEndpointModuleTests
    {
        private Exception _exception = null!;
        private Exception _caught = null!;
        private DefaultHttpContext _context = null!;

        [SetUp]
        public async Task SetUp()
        {
            _exception = failure switch
            {
                "io" => new IOException("Interrupted request body"),
                "cancellation" => new OperationCanceledException(),
                _ => new BadHttpRequestException(
                    "Request body too large",
                    StatusCodes.Status413PayloadTooLarge
                ),
            };
            var formFeature = A.Fake<IFormFeature>();
            A.CallTo(() => formFeature.HasFormContentType).Returns(true);
            A.CallTo(() => formFeature.ReadFormAsync(A<CancellationToken>.Ignored)).ThrowsAsync(_exception);
            _context = new DefaultHttpContext();
            _context.Features.Set(formFeature);
            _context.Response.Body = new MemoryStream();

            try
            {
                await AspNetCore.Modules.TokenEndpointModule.HandleFormData(
                    _context,
                    Options.Create(
                        new AppSettings
                        {
                            AuthenticationService = "http://localhost/connect/token",
                            Datastore = "postgresql",
                            CorrelationIdHeader = "",
                        }
                    ),
                    A.Fake<IOAuthManager>(),
                    NullLogger<AspNetCore.Modules.TokenEndpointModule>.Instance,
                    A.Fake<IHttpClientFactory>()
                );
            }
            catch (Exception ex)
            {
                _caught = ex;
            }
        }

        [TearDown]
        public void TearDown()
        {
            _context.Response.Body.Dispose();
        }

        [Test]
        public void It_leaves_the_failure_for_the_existing_middleware()
        {
            _caught.Should().BeSameAs(_exception);
        }

        [Test]
        public void It_does_not_write_an_invalid_request_response()
        {
            ((MemoryStream)_context.Response.Body).Length.Should().Be(0);
            _context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
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
            (_response, _) = SendThroughRealManager(
                ProxyRequest("grant_type=client_credentials", "application/x-www-form-urlencoded"),
                HttpStatusCode.Conflict,
                "withheld-contention-body"
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
        public void It_delivers_the_oauth_json_media_type()
        {
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        }

        [Test]
        public void It_delivers_the_oauth_error_body()
        {
            _body
                .AsObject()
                .Select(member => member.Key)
                .Should()
                .BeEquivalentTo("error", "error_description");
            _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("The service is temporarily unable to handle the request. Retry the request later.");
            _response.Headers.WwwAuthenticate.Should().BeEmpty();
            _response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle();
        }
    }
}

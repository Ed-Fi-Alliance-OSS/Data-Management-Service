// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

/// <summary>
/// End-to-end verification that framework-generated bodiless 401/403/404/405/415 responses are shaped
/// into the Ed-Fi contract by <c>FrameworkErrorResponseMiddleware</c>, independent of route and
/// authentication scheme, while already-structured errors and success/204 responses are left alone and
/// existing headers (WWW-Authenticate, Allow) are preserved (DMS-1218 INV-25…29). This is a non-fixture
/// container; the runnable fixtures are the nested <c>Given_…</c> classes.
/// </summary>
public class FrameworkErrorResponseTests
{
    private static WebApplicationFactory<Program> CreateFactory(
        bool addTestAuthentication,
        bool multiTenancy = false,
        bool dangerousFlag = false,
        string? requiredServiceRole = null
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:MultiTenancy", multiTenancy ? "true" : "false");
            builder.ConfigureServices(
                (ctx, collection) =>
                {
                    collection.Configure<AppSettings>(options => options.MultiTenancy = multiTenancy);

                    if (addTestAuthentication)
                    {
                        collection.AddTestAuthentication();

                        var identitySettings = ctx
                            .Configuration.GetSection("IdentitySettings")
                            .Get<IdentitySettings>()!;
                        collection.AddAuthorization(options =>
                        {
                            options.AddPolicy(
                                SecurityConstants.ServicePolicy,
                                policy =>
                                    policy.RequireClaim(
                                        identitySettings.RoleClaimType,
                                        requiredServiceRole ?? identitySettings.ConfigServiceRole
                                    )
                            );
                            AuthorizationScopePolicies.Add(options);
                        });

                        collection.AddTransient(_ => A.Fake<IClaimsUploadService>());
                        collection.AddTransient(_ => A.Fake<IClaimsProvider>());
                    }

                    collection.Configure<ClaimsOptions>(options =>
                        options.DangerouslyEnableUnrestrictedClaimsLoading = dangerousFlag
                    );
                }
            );
        });
    }

    private static void AssertShapedContract(
        HttpResponseMessage response,
        JsonObject body,
        HttpStatusCode status,
        string type,
        string title,
        string detail
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body["type"]!.GetValue<string>().Should().Be(type);
        body["title"]!.GetValue<string>().Should().Be(title);
        body["detail"]!.GetValue<string>().Should().Be(detail);
        body["status"]!.GetValue<int>().Should().Be((int)status);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    /// <summary>Production JWT bearer challenge (no token) on a secured endpoint.</summary>
    [TestFixture]
    public class Given_a_production_jwt_challenge
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(addTestAuthentication: false);
            _client = _factory.CreateClient();
            _response = await _client.GetAsync("/management/current-claims");
            _body = await ReadBodyAsync(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_shaped_authentication_contract() =>
            AssertShapedContract(
                _response,
                _body,
                HttpStatusCode.Unauthorized,
                "urn:ed-fi:api:security:authentication",
                "Authentication Failed",
                "Authentication is required to access this resource."
            );

        [Test]
        public void It_preserves_the_www_authenticate_header() =>
            _response.Headers.WwwAuthenticate.Select(header => header.Scheme).Should().Contain("Bearer");
    }

    /// <summary>TestAuthHandler with no scope header fails authentication → scheme-independent 401.</summary>
    [TestFixture]
    public class Given_test_auth_with_a_missing_scope
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(addTestAuthentication: true);
            _client = _factory.CreateClient();
            _response = await _client.GetAsync("/management/current-claims");
            _body = await ReadBodyAsync(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_shaped_authentication_contract() =>
            AssertShapedContract(
                _response,
                _body,
                HttpStatusCode.Unauthorized,
                "urn:ed-fi:api:security:authentication",
                "Authentication Failed",
                "Authentication is required to access this resource."
            );
    }

    /// <summary>TestAuthHandler with a read-only scope on an admin-only endpoint → 403.</summary>
    [TestFixture]
    public class Given_test_auth_with_an_insufficient_scope
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(addTestAuthentication: true);
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.ReadOnlyScope.Name);
            _response = await _client.PostAsync(
                "/management/reload-claims",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );
            _body = await ReadBodyAsync(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_shaped_authorization_contract() =>
            AssertShapedContract(
                _response,
                _body,
                HttpStatusCode.Forbidden,
                "urn:ed-fi:api:security:authorization",
                "Authorization Failed",
                "The authenticated client is not authorized to access this resource."
            );
    }

    /// <summary>An unmatched, health-lookalike route proves there are no route/health exclusions.</summary>
    [TestFixture]
    public class Given_an_unknown_health_lookalike_route
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(addTestAuthentication: false);
            _client = _factory.CreateClient();
            _response = await _client.GetAsync("/healthcheck");
            _body = await ReadBodyAsync(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_shaped_not_found_contract() =>
            AssertShapedContract(
                _response,
                _body,
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "The requested resource could not be found."
            );
    }

    /// <summary>A GET-only route hit with DELETE → routing 405 with an Allow header.</summary>
    [TestFixture]
    public class Given_a_wrong_http_method
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(addTestAuthentication: false);
            _client = _factory.CreateClient();
            _response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/health"));
            _body = await ReadBodyAsync(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_shaped_method_not_allowed_contract() =>
            AssertShapedContract(
                _response,
                _body,
                HttpStatusCode.MethodNotAllowed,
                "urn:ed-fi:api:method-not-allowed",
                "Method Not Allowed",
                "The request construction was invalid."
            );

        [Test]
        public void It_preserves_the_allow_header() =>
            _response.Content.Headers.Allow.Should().Contain("GET");
    }

    /// <summary>An authenticated JSON endpoint receiving text/plain → negotiated 415.</summary>
    [TestFixture]
    public class Given_an_unsupported_media_type
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(addTestAuthentication: true);
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
            _response = await _client.PostAsync(
                "/management/upload-claims",
                new StringContent("plain text", Encoding.UTF8, "text/plain")
            );
            _body = await ReadBodyAsync(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_shaped_unsupported_media_type_contract() =>
            AssertShapedContract(
                _response,
                _body,
                HttpStatusCode.UnsupportedMediaType,
                "urn:ed-fi:api:unsupported-media-type",
                "Unsupported Media Type",
                "The value specified in the 'Content-Type' header is not supported by this host."
            );
    }

    /// <summary>A module that already returns a structured 404 must not be clobbered.</summary>
    [TestFixture]
    public class Given_an_existing_structured_404
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(addTestAuthentication: true, dangerousFlag: false);
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
            _response = await _client.GetAsync("/management/current-claims");
            _body = await ReadBodyAsync(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_404() => _response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        [Test]
        public void It_keeps_the_not_found_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");

        [Test]
        public void It_preserves_the_module_specific_detail() =>
            _body["detail"]!.GetValue<string>().Should().Be("Current claims endpoint is not available.");
    }

    /// <summary>A 200 success (health) is never shaped.</summary>
    [TestFixture]
    public class Given_a_health_success
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(addTestAuthentication: false);
            _client = _factory.CreateClient();
            _response = await _client.GetAsync("/health");
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_is_not_problem_details() =>
            _response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json");

        [Test]
        public void It_keeps_its_body() => _content.Should().NotBeNullOrEmpty();
    }

    /// <summary>A CORS preflight 204 is never shaped and stays empty.</summary>
    [TestFixture]
    public class Given_a_cors_preflight
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(addTestAuthentication: false);
            _client = _factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Options, "/health");
            request.Headers.Add("Origin", "http://localhost:8082");
            request.Headers.Add("Access-Control-Request-Method", "GET");
            _response = await _client.SendAsync(request);
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_204() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_has_an_empty_body() => _content.Should().BeEmpty();
    }
}

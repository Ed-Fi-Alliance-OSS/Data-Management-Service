// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit;

public class EndpointAuthorizationTests
{
    private static WebApplicationFactory<Program> CreateFactory(string identityProvider, bool multiTenancy) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:IdentityProvider", identityProvider);
            builder.UseSetting("AppSettings:MultiTenancy", multiTenancy.ToString());
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSettings:IdentityProvider"] = identityProvider,
                        ["AppSettings:MultiTenancy"] = multiTenancy.ToString(),
                        ["Cors:SwaggerUIOrigin"] = "http://localhost:8082",
                    }
                )
            );
            builder.ConfigureServices(services =>
                services.Configure<AppSettings>(options =>
                {
                    options.IdentityProvider = identityProvider;
                    options.MultiTenancy = multiTenancy;
                })
            );
        });

    private static string NormalizePattern(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return "/";
        }
        return rawText.StartsWith('/') ? rawText : "/" + rawText;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S3398",
        Justification = "Keeps the independent anonymous inventory projection beside shared route normalization."
    )]
    private static string[] AnonymousMethodTemplates(IEnumerable<RouteEndpoint> endpoints) =>
        endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
                methods.Should().NotBeNull();
                methods!.HttpMethods.Should().NotBeEmpty();
                return methods.HttpMethods.Select(method =>
                    $"{method} {NormalizePattern(endpoint.RoutePattern.RawText!)}"
                );
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

    [TestFixture("self-contained", false)]
    [TestFixture("self-contained", true)]
    [TestFixture("keycloak", false)]
    [TestFixture("keycloak", true)]
    public class Given_the_production_endpoint_inventory(string identityProvider, bool multiTenancy)
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private RouteEndpoint[] _endpoints = null!;

        [OneTimeSetUp]
        public void Setup()
        {
            _factory = CreateFactory(identityProvider, multiTenancy);
            _client = _factory.CreateClient();
            _endpoints = _factory
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .ToArray();
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _client.Dispose();
            _factory.Dispose();
        }

        [Test]
        public void It_requires_exclusive_authorization_metadata()
        {
            _endpoints.Should().NotBeEmpty();
            foreach (var endpoint in _endpoints)
            {
                bool anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                bool authorized = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
                (anonymous ^ authorized)
                    .Should()
                    .BeTrue(
                        "endpoint {0} requires exactly one authorization metadata kind",
                        endpoint.DisplayName
                    );
            }
        }

        [Test]
        public void It_preserves_the_exact_anonymous_method_and_template_set()
        {
            _endpoints.Should().NotBeEmpty();
            string[] expected =
            [
                "GET /",
                "GET /health",
                "GET /tenancy",
                "GET /metadata/specifications",
                "GET /.well-known/openiddict-status",
                "GET /openapi/{documentName}.json",
                "POST /connect/register/{**contextPath}",
                "POST /connect/token/{**contextPath}",
                "POST /connect/introspect/{**contextPath}",
                "POST /connect/revoke/{**contextPath}",
            ];
            if (identityProvider == "self-contained")
            {
                expected =
                [
                    .. expected,
                    "GET /.well-known/jwks.json",
                    "GET /.well-known/openid-configuration",
                ];
            }
            AnonymousMethodTemplates(_endpoints).Should().Equal(expected.Order(StringComparer.Ordinal));
        }

        [Test]
        public void It_keeps_the_probe_out_of_the_production_inventory()
        {
            _endpoints.Should().NotBeEmpty();
            _endpoints.Should().NotContain(endpoint => endpoint.RoutePattern.RawText == ProbePath);
        }

        [Test]
        public void It_keeps_tenant_management_protected_and_configuration_gated()
        {
            _endpoints.Should().NotBeEmpty();
            var tenants = _endpoints
                .Where(endpoint =>
                    NormalizePattern(endpoint.RoutePattern.RawText!) is var path
                    && (path == "/v3/tenants" || path.StartsWith("/v3/tenants/", StringComparison.Ordinal))
                )
                .ToArray();
            tenants.Any().Should().Be(multiTenancy);
            foreach (var endpoint in tenants)
            {
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().NotBeEmpty();
                endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
            }
        }
    }

    private const string ProbePath = "/__dms1582/no-metadata";

    private static WebApplicationFactory<Program> CreateBehaviorFactory(
        string identityProvider,
        bool multiTenancy,
        ProbeState state,
        bool includeProbe = false
    ) =>
        CreateFactory(identityProvider, multiTenancy)
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    var tenants = A.Fake<ITenantRepository>();
                    A.CallTo(() => tenants.GetTenantByName(A<string>.Ignored))
                        .Returns(new TenantGetByNameResult.FailureNotFound());
                    A.CallTo(() => tenants.GetTenantByName("Tenant_A"))
                        .Returns(
                            new TenantGetByNameResult.Success(
                                new TenantResponse { Id = 1, Name = "Tenant_A" }
                            )
                        );
                    services.AddTransient(_ => tenants);
                    services.AddTransient(_ => A.Fake<IClaimsProvider>());
                    services.AddTransient(_ => A.Fake<IClaimsUploadService>());
                    services.Configure<ClaimsOptions>(options =>
                        options.DangerouslyEnableUnrestrictedClaimsLoading = false
                    );
                    services.AddSingleton<IStartupFilter>(
                        new AuthorizationProbeStartupFilter(state, includeProbe)
                    );
                })
            );

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string? principal = null,
        string? tenant = null
    )
    {
        var request = new HttpRequestMessage(method, path);
        if (principal is not null)
        {
            request.Headers.Add("X-DMS-1582-Principal", principal);
        }
        if (tenant is not null)
        {
            request.Headers.Add("Tenant", tenant);
        }
        return request;
    }

    private sealed class ProbeState
    {
        public int HandlerCalls;
        public string TraceIdentifier = string.Empty;
    }

    private sealed class AuthorizationProbeStartupFilter(ProbeState state, bool includeProbe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                var identity = app.ApplicationServices.GetRequiredService<IOptions<IdentitySettings>>().Value;
                app.Use(
                    async (context, nextMiddleware) =>
                    {
                        state.TraceIdentifier = context.TraceIdentifier;
                        string mode = context.Request.Headers["X-DMS-1582-Principal"].ToString();
                        if (mode.Length > 0)
                        {
                            List<Claim> claims = [];
                            if (mode is "role-only" or "role-and-scope" or "role-readonly")
                            {
                                claims.Add(new Claim(identity.RoleClaimType, identity.ConfigServiceRole));
                            }
                            if (mode is "scope-only" or "role-and-scope")
                            {
                                claims.Add(new Claim("scope", "edfi_admin_api/full_access"));
                            }
                            if (mode == "role-readonly")
                            {
                                claims.Add(new Claim("scope", "edfi_admin_api/readonly_access"));
                            }
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "DMS-1582"));
                        }
                        await nextMiddleware(context);
                    }
                );
                next(app);
                if (includeProbe)
                {
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet(
                            ProbePath,
                            () =>
                            {
                                state.HandlerCalls++;
                                return Results.Text("probe reached");
                            }
                        )
                    );
                }
            };
    }

    private static async Task AssertFailure(
        HttpResponseMessage response,
        ProbeState state,
        HttpStatusCode status,
        string type,
        string title,
        string detail
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body["type"]!.GetValue<string>().Should().Be(type);
        body["title"]!.GetValue<string>().Should().Be(title);
        body["detail"]!.GetValue<string>().Should().Be(detail);
        body["status"]!.GetValue<int>().Should().Be((int)status);
        state.TraceIdentifier.Should().NotBeNullOrEmpty();
        body["correlationId"]!.GetValue<string>().Should().Be(state.TraceIdentifier);
        body["validationErrors"]!.AsObject().Should().BeEmpty();
        body["errors"]!.AsArray().Should().BeEmpty();
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private static async Task AssertChallenge(HttpResponseMessage response, ProbeState state)
    {
        await AssertFailure(
            response,
            state,
            HttpStatusCode.Unauthorized,
            "urn:ed-fi:management-api:security:authentication",
            "Authentication Failed",
            "Authentication is required to access this resource."
        );
        response.Headers.WwwAuthenticate.Select(header => header.Scheme).Should().Contain("Bearer");
    }

    [TestFixture("self-contained", false)]
    [TestFixture("self-contained", true)]
    [TestFixture("keycloak", false)]
    [TestFixture("keycloak", true)]
    public class Given_the_production_fallback_policy(string identityProvider, bool multiTenancy)
    {
        [Test]
        public async Task It_configures_only_an_authenticated_user_requirement()
        {
            await using var factory = CreateFactory(identityProvider, multiTenancy);
            using var client = factory.CreateClient();
            var policy = await factory
                .Services.GetRequiredService<IAuthorizationPolicyProvider>()
                .GetFallbackPolicyAsync();
            policy.Should().NotBeNull();
            policy!
                .Requirements.Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<DenyAnonymousAuthorizationRequirement>();
            policy.AuthenticationSchemes.Should().BeEmpty();
        }
    }

    [TestFixture("self-contained", false)]
    [TestFixture("self-contained", true)]
    [TestFixture("keycloak", false)]
    [TestFixture("keycloak", true)]
    public class Given_a_metadata_free_probe(string identityProvider, bool multiTenancy)
    {
        [Test]
        public async Task It_rejects_anonymous_before_the_probe_handler()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(
                identityProvider,
                multiTenancy,
                state,
                includeProbe: true
            );
            using var client = factory.CreateClient();
            var probe = factory
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Should()
                .ContainSingle(endpoint => endpoint.RoutePattern.RawText == ProbePath)
                .Subject;
            probe.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
            probe.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().BeEmpty();
            using var request = CreateRequest(
                HttpMethod.Get,
                ProbePath,
                tenant: multiTenancy ? "Tenant_A" : null
            );
            using var response = await client.SendAsync(request);
            await AssertChallenge(response, state);
            state.HandlerCalls.Should().Be(0);
        }

        [Test]
        public async Task It_allows_an_authenticated_principal_without_cms_role_or_scope()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(
                identityProvider,
                multiTenancy,
                state,
                includeProbe: true
            );
            using var client = factory.CreateClient();
            using var request = CreateRequest(
                HttpMethod.Get,
                ProbePath,
                "authenticated-only",
                multiTenancy ? "Tenant_A" : null
            );
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("probe reached");
            state.HandlerCalls.Should().Be(1);
        }
    }

    [TestFixture("self-contained", false)]
    [TestFixture("self-contained", true)]
    [TestFixture("keycloak", false)]
    [TestFixture("keycloak", true)]
    public class Given_production_authorization_behavior(string identityProvider, bool multiTenancy)
    {
        [Test]
        public async Task It_challenges_an_anonymous_unknown_path()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var request = CreateRequest(
                HttpMethod.Get,
                "/healthcheck",
                tenant: multiTenancy ? "Tenant_A" : null
            );
            using var response = await client.SendAsync(request);
            await AssertChallenge(response, state);
        }

        [Test]
        public async Task It_keeps_an_authenticated_unknown_path_not_found()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var request = CreateRequest(
                HttpMethod.Get,
                "/healthcheck",
                "authenticated-only",
                multiTenancy ? "Tenant_A" : null
            );
            using var response = await client.SendAsync(request);
            await AssertFailure(
                response,
                state,
                HttpStatusCode.NotFound,
                "urn:ed-fi:management-api:not-found",
                "Not Found",
                "The requested resource could not be found."
            );
        }

        [TestCase("role-only")]
        [TestCase("scope-only")]
        public async Task It_requires_both_the_production_role_and_scope(string principal)
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var request = CreateRequest(
                HttpMethod.Get,
                "/management/current-claims",
                principal,
                multiTenancy ? "Tenant_A" : null
            );
            using var response = await client.SendAsync(request);
            await AssertFailure(
                response,
                state,
                HttpStatusCode.Forbidden,
                "urn:ed-fi:management-api:security:authorization",
                "Authorization Failed",
                "The authenticated client is not authorized to access this resource."
            );
        }

        [Test]
        public async Task It_preserves_admin_scope_on_existing_write_routes()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var request = CreateRequest(
                HttpMethod.Post,
                "/management/reload-claims",
                "role-readonly",
                multiTenancy ? "Tenant_A" : null
            );
            using var response = await client.SendAsync(request);
            await AssertFailure(
                response,
                state,
                HttpStatusCode.Forbidden,
                "urn:ed-fi:management-api:security:authorization",
                "Authorization Failed",
                "The authenticated client is not authorized to access this resource."
            );
        }

        [Test]
        public async Task It_preserves_a_matched_handler_feature_gate()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var request = CreateRequest(
                HttpMethod.Get,
                "/management/current-claims",
                "role-and-scope",
                multiTenancy ? "Tenant_A" : null
            );
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            body["detail"]!.GetValue<string>().Should().Be("Current claims endpoint is not available.");
        }

        [TestCase("/v3/dataStores/edOrgs/refresh")]
        [TestCase("/v3/dataStores/7/edOrgs/refresh")]
        public async Task It_requires_the_admin_scope_to_queue_an_education_organization_refresh(string path)
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var readOnly = CreateRequest(
                HttpMethod.Post,
                path,
                "role-readonly",
                multiTenancy ? "Tenant_A" : null
            );
            using var forbidden = await client.SendAsync(readOnly);
            await AssertFailure(
                forbidden,
                state,
                HttpStatusCode.Forbidden,
                "urn:ed-fi:management-api:security:authorization",
                "Authorization Failed",
                "The authenticated client is not authorized to access this resource."
            );
            using var anonymous = CreateRequest(
                HttpMethod.Post,
                path,
                tenant: multiTenancy ? "Tenant_A" : null
            );
            using var challenged = await client.SendAsync(anonymous);
            await AssertChallenge(challenged, state);
        }

        [Test]
        public async Task It_challenges_an_anonymous_wrong_method()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var request = CreateRequest(HttpMethod.Delete, "/health");
            using var response = await client.SendAsync(request);
            await AssertChallenge(response, state);
            response.Content.Headers.Allow.Should().BeEmpty();
        }

        [Test]
        public async Task It_preserves_authenticated_method_not_allowed()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var request = CreateRequest(HttpMethod.Delete, "/health", "authenticated-only");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.Allow.Should().Contain("GET");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:method-not-allowed");
        }

        [Test]
        public async Task It_preserves_permitted_cors_preflight()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var request = CreateRequest(HttpMethod.Options, "/health");
            request.Headers.Add("Origin", "http://localhost:8082");
            request.Headers.Add("Access-Control-Request-Method", "GET");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
            response
                .Headers.GetValues("Access-Control-Allow-Origin")
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be("http://localhost:8082");
            response.Headers.GetValues("Access-Control-Allow-Methods").Should().Contain("GET");
        }

        [Test]
        public async Task It_preserves_a_matched_public_openapi_not_found()
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(identityProvider, multiTenancy, state);
            using var client = factory.CreateClient();
            using var request = CreateRequest(
                HttpMethod.Get,
                "/openapi/missing.json",
                tenant: multiTenancy ? "Tenant_A" : null
            );
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        }
    }

    [TestFixture("self-contained")]
    [TestFixture("keycloak")]
    public class Given_tenant_validation_before_fallback(string identityProvider)
    {
        [TestCase(null, null)]
        [TestCase(null, "authenticated-only")]
        [TestCase("UnknownTenant", null)]
        [TestCase("UnknownTenant", "authenticated-only")]
        public async Task It_keeps_missing_or_invalid_tenant_errors_before_authorization(
            string? tenant,
            string? principal
        )
        {
            var state = new ProbeState();
            await using var factory = CreateBehaviorFactory(
                identityProvider,
                true,
                state,
                includeProbe: true
            );
            using var client = factory.CreateClient();
            using var request = CreateRequest(HttpMethod.Get, ProbePath, principal, tenant);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            state.HandlerCalls.Should().Be(0);
            using var scope = factory.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
            if (tenant is null)
            {
                A.CallTo(() => repository.GetTenantByName(A<string>.Ignored)).MustNotHaveHappened();
            }
            else
            {
                A.CallTo(() => repository.GetTenantByName("UnknownTenant")).MustHaveHappenedOnceExactly();
            }
        }
    }
}

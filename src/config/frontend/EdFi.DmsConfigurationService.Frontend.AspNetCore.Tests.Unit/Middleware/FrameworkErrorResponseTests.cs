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
/// existing headers (WWW-Authenticate, Allow) are preserved (DMS-1218 INV-25…29).
/// </summary>
[TestFixture]
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

    private static async Task AssertShapedContractAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string type,
        string title,
        string detail
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        JsonNode body = JsonNode.Parse(content)!;
        body["type"]!.GetValue<string>().Should().Be(type);
        body["title"]!.GetValue<string>().Should().Be(title);
        body["detail"]!.GetValue<string>().Should().Be(detail);
        body["status"]!.GetValue<int>().Should().Be((int)status);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
    }

    [Test]
    public async Task Production_jwt_challenge_returns_shaped_401_and_preserves_www_authenticate()
    {
        using var factory = CreateFactory(addTestAuthentication: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/management/current-claims");

        await AssertShapedContractAsync(
            response,
            HttpStatusCode.Unauthorized,
            "urn:ed-fi:api:security:authentication",
            "Authentication Failed",
            "Authentication is required to access this resource."
        );
        response.Headers.WwwAuthenticate.Select(header => header.Scheme).Should().Contain("Bearer");
    }

    [Test]
    public async Task Test_auth_missing_scope_returns_shaped_401()
    {
        using var factory = CreateFactory(addTestAuthentication: true);
        using var client = factory.CreateClient();

        // No X-Test-Scope header → TestAuthHandler fails authentication, scheme-independent of JWT.
        var response = await client.GetAsync("/management/current-claims");

        await AssertShapedContractAsync(
            response,
            HttpStatusCode.Unauthorized,
            "urn:ed-fi:api:security:authentication",
            "Authentication Failed",
            "Authentication is required to access this resource."
        );
    }

    [Test]
    public async Task Test_auth_insufficient_scope_returns_shaped_403()
    {
        using var factory = CreateFactory(addTestAuthentication: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.ReadOnlyScope.Name);

        // reload-claims requires AdminScope, so a read-only principal is authenticated but forbidden.
        var response = await client.PostAsync(
            "/management/reload-claims",
            new StringContent("{}", Encoding.UTF8, "application/json")
        );

        await AssertShapedContractAsync(
            response,
            HttpStatusCode.Forbidden,
            "urn:ed-fi:api:security:authorization",
            "Authorization Failed",
            "The authenticated client is not authorized to access this resource."
        );
    }

    [Test]
    public async Task Unknown_route_returns_shaped_404_even_for_a_health_lookalike_path()
    {
        using var factory = CreateFactory(addTestAuthentication: false);
        using var client = factory.CreateClient();

        // A health-lookalike path proves the shaping middleware applies no route/health exclusions.
        var response = await client.GetAsync("/healthcheck");

        await AssertShapedContractAsync(
            response,
            HttpStatusCode.NotFound,
            "urn:ed-fi:api:not-found",
            "Not Found",
            "The requested resource could not be found."
        );
    }

    [Test]
    public async Task Wrong_method_returns_shaped_405_and_preserves_the_allow_header()
    {
        using var factory = CreateFactory(addTestAuthentication: false);
        using var client = factory.CreateClient();

        // /health is mapped for GET only; DELETE yields a routing 405 with an Allow header.
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/health"));

        await AssertShapedContractAsync(
            response,
            HttpStatusCode.MethodNotAllowed,
            "urn:ed-fi:api:method-not-allowed",
            "Method Not Allowed",
            "The request construction was invalid."
        );
        response.Content.Headers.Allow.Should().Contain("GET");
    }

    [Test]
    public async Task Unsupported_media_type_returns_shaped_415()
    {
        using var factory = CreateFactory(addTestAuthentication: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

        // upload-claims accepts application/json; text/plain is negotiated to a 415 before the handler.
        var response = await client.PostAsync(
            "/management/upload-claims",
            new StringContent("plain text", Encoding.UTF8, "text/plain")
        );

        await AssertShapedContractAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            "urn:ed-fi:api:unsupported-media-type",
            "Unsupported Media Type",
            "The value specified in the 'Content-Type' header is not supported by this host."
        );
    }

    [Test]
    public async Task Existing_structured_404_is_left_unchanged()
    {
        using var factory = CreateFactory(addTestAuthentication: true, dangerousFlag: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

        // The module already returns a structured 404 here; the shaping middleware must not clobber it.
        var response = await client.GetAsync("/management/current-claims");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        string content = await response.Content.ReadAsStringAsync();
        JsonNode body = JsonNode.Parse(content)!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
        body["detail"]!.GetValue<string>().Should().Be("Current claims endpoint is not available.");
    }

    [Test]
    public async Task Health_endpoint_200_is_left_unchanged()
    {
        using var factory = CreateFactory(addTestAuthentication: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Cors_preflight_204_is_left_unchanged()
    {
        using var factory = CreateFactory(addTestAuthentication: false);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", "http://localhost:8082");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }
}

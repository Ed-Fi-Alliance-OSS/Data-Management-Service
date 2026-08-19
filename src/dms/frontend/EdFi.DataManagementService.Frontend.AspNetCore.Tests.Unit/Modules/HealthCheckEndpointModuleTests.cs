// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
public class Given_HealthCheckEndpointModule
{
    private const string ValidRequiredRole = "dms-document-cache-operator";
    private const string RoleClaimType = "operator_role";
    private const string ValidBearerToken = "valid-token";

    private static WebApplicationFactory<Program> CreateFactory(
        ScriptedDocumentCacheStatusService documentCacheStatusService,
        string? requiredRole,
        ScriptedJwtValidationService? jwtValidationService = null,
        string? roleClaimType = RoleClaimType
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    if (requiredRole is not null)
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["DataManagement:DocumentCache:Status:RequiredRole"] = requiredRole,
                            }
                        );
                    }

                    Dictionary<string, string?> jwtConfiguration = new()
                    {
                        ["JwtAuthentication:ClientRole"] = "legacy-service",
                    };
                    if (roleClaimType is not null)
                    {
                        jwtConfiguration["JwtAuthentication:RoleClaimType"] = roleClaimType;
                    }

                    configuration.AddInMemoryCollection(jwtConfiguration);
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.Replace(
                    ServiceDescriptor.Singleton<IJwtValidationService>(
                        jwtValidationService
                            ?? new ScriptedJwtValidationService(new Dictionary<string, ClaimsPrincipal?>())
                    )
                );
                services.Replace(
                    ServiceDescriptor.Singleton<IDocumentCacheStatusService>(documentCacheStatusService)
                );
            });
        });
    }

    private static ScriptedDocumentCacheStatusService EmptyStatusService() =>
        new(new DocumentCacheStatusResponse(new DateTimeOffset(2026, 8, 17, 12, 34, 56, TimeSpan.Zero), []));

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static ScriptedJwtValidationService ValidJwtWithClaims(params Claim[] claims) =>
        new(new Dictionary<string, ClaimsPrincipal?> { [ValidBearerToken] = Principal(claims) });

    [Test]
    public void It_ships_the_self_contained_issuer_role_claim_type_as_the_default()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            requiredRole: null,
            roleClaimType: null
        );
        using HttpClient client = factory.CreateClient();

        factory
            .Services.GetRequiredService<IOptions<JwtAuthenticationOptions>>()
            .Value.RoleClaimType.Should()
            .Be(ClaimTypes.Role);
    }

    [Test]
    public async Task It_omits_document_cache_status_when_required_role_is_missing()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            requiredRole: null
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_omits_document_cache_status_when_required_role_is_invalid()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            requiredRole: "dms document cache operator"
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task It_omits_document_cache_status_when_role_claim_type_is_blank(string roleClaimType)
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole,
            roleClaimType: roleClaimType
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_DocumentCacheStatusAuthorization_401_when_token_is_missing()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_DocumentCacheStatusAuthorization_401_when_token_is_malformed()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole
        );
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "token");

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_DocumentCacheStatusAuthorization_401_when_token_is_invalid()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        ScriptedJwtValidationService jwtValidationService = new(new Dictionary<string, ClaimsPrincipal?>());
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole,
            jwtValidationService
        );
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        jwtValidationService.CallCount.Should().Be(1);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_DocumentCacheStatusAuthorization_403_when_token_lacks_exact_required_role()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        ScriptedJwtValidationService jwtValidationService = ValidJwtWithClaims(
            new Claim(RoleClaimType, "other-role"),
            new Claim(ClaimTypes.Role, ValidRequiredRole)
        );
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole,
            jwtValidationService
        );
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ValidBearerToken
        );

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        jwtValidationService.CallCount.Should().Be(1);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_document_cache_status_after_DocumentCacheStatusAuthorization_succeeds()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        ScriptedJwtValidationService jwtValidationService = ValidJwtWithClaims(
            new Claim(RoleClaimType, ValidRequiredRole)
        );
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole,
            jwtValidationService
        );
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ValidBearerToken
        );

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");
        string content = await response.Content.ReadAsStringAsync();
        JsonNode json = JsonNode.Parse(content)!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json["contractVersion"]!.GetValue<int>().Should().Be(1);
        json["observedAt"]!.GetValue<string>().Should().Be("2026-08-17T12:34:56Z");
        json["targets"]!.AsArray().Should().BeEmpty();
        jwtValidationService.CallCount.Should().Be(1);
        documentCacheStatusService.CallCount.Should().Be(1);
    }

    [Test]
    public async Task It_keeps_the_existing_health_endpoint_independent_from_document_cache_status()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");
        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("\"Name\": \"ApplicationHealthCheck\"");
        content.Should().NotContain("document-cache");
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public void It_excludes_document_cache_status_from_OpenApi_description_metadata()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole
        );
        using HttpClient client = factory.CreateClient();

        RouteEndpoint endpoint = factory
            .Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/health/document-cache");

        endpoint
            .Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!
            .ExcludeFromDescription.Should()
            .BeTrue();
    }

    private sealed class ScriptedDocumentCacheStatusService(DocumentCacheStatusResponse response)
        : IDocumentCacheStatusService
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<DocumentCacheStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(response);
        }
    }

    private sealed class ScriptedJwtValidationService(
        IReadOnlyDictionary<string, ClaimsPrincipal?> principalsByToken
    ) : IJwtValidationService
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<(
            ClaimsPrincipal? Principal,
            ClientAuthorizations? ClientAuthorizations
        )> ValidateAndExtractClientAuthorizationsAsync(string token, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            principalsByToken.TryGetValue(token, out ClaimsPrincipal? principal);
            return Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>((principal, null));
        }
    }
}

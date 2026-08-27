// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DataManagementService.Tests.Integration.Tests.DocumentCache;

[TestFixture]
[NonParallelizable]
[Category("DocumentCacheStatusEndpointAuthorization")]
public class Given_DocumentCacheStatusEndpointAuthorization
{
    private const string RequiredRole = "dms-document-cache-operator";
    private const string RoleClaimType = "operator_role";
    private const string ValidBearerToken = "valid-token";
    private const string TestAudience = "ed-fi-ods-api";
    private const string TestIssuer = "https://keycloak.example.com/realms/edfi";

    private static WebApplicationFactory<Program> CreateFactory(
        ScriptedDocumentCacheStatusService documentCacheStatusService,
        IJwtValidationService? jwtValidationService = null,
        string? requiredRole = RequiredRole,
        string roleClaimType = RoleClaimType
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["JwtAuthentication:RoleClaimType"] = roleClaimType,
                            ["JwtAuthentication:ClientRole"] = "legacy-service",
                        }
                    );

                    if (requiredRole is not null)
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["DataManagement:DocumentCache:Status:RequiredRole"] = requiredRole,
                            }
                        );
                    }
                }
            );
            builder.ConfigureServices(services =>
            {
                AddEssentialMocks(services);
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

    private static (IJwtValidationService JwtValidationService, string Token) RealJwtWithClaims(
        string roleClaimType,
        Claim[] claims,
        Action<JwtSecurityTokenHandler>? configureTokenHandler = null
    )
    {
        var configurationManager = A.Fake<IConfigurationManager<OpenIdConnectConfiguration>>();
        var signingKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" };
        OpenIdConnectConfiguration oidcConfig = new() { Issuer = TestIssuer };
        oidcConfig.SigningKeys.Add(signingKey);

        A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
            .Returns(Task.FromResult(oidcConfig));

        JwtAuthenticationOptions jwtAuthenticationOptions = new()
        {
            Audience = TestAudience,
            RoleClaimType = roleClaimType,
            ClientRole = "legacy-service",
            ClockSkewSeconds = 0,
            ValidatedTokenCacheMaxEntries = 4,
        };
        JwtValidationService jwtValidationService = new(
            configurationManager,
            Options.Create(jwtAuthenticationOptions),
            NullLogger<JwtValidationService>.Instance
        );

        JwtSecurityTokenHandler tokenHandler = new();
        configureTokenHandler?.Invoke(tokenHandler);
        string token = CreateSignedToken(claims, signingKey, tokenHandler);

        return (jwtValidationService, token);
    }

    private static string CreateSignedToken(
        Claim[] claims,
        SecurityKey signingKey,
        JwtSecurityTokenHandler tokenHandler
    )
    {
        DateTime now = DateTime.UtcNow;
        JwtSecurityToken jwt = new(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            notBefore: now.AddMinutes(-5),
            expires: now.AddMinutes(10),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        );

        return tokenHandler.WriteToken(jwt);
    }

    [Test]
    public async Task It_returns_404_when_required_role_is_missing_without_invoking_status_service()
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
    public async Task It_returns_404_when_required_role_is_invalid_without_invoking_status_service()
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

    [Test]
    public async Task It_returns_401_for_missing_token_without_invoking_status_service()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(documentCacheStatusService);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_401_for_malformed_authorization_scheme_without_invoking_status_service()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(documentCacheStatusService);
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "token");

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_401_for_invalid_token_without_invoking_status_service()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        ScriptedJwtValidationService jwtValidationService = new(new Dictionary<string, ClaimsPrincipal?>());
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
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
    public async Task It_returns_403_for_valid_token_without_exact_required_role()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        ScriptedJwtValidationService jwtValidationService = ValidJwtWithClaims(
            new Claim(RoleClaimType, "other-role"),
            new Claim(ClaimTypes.Role, RequiredRole)
        );
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
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
    public async Task It_returns_200_for_valid_token_with_exact_required_role()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        ScriptedJwtValidationService jwtValidationService = ValidJwtWithClaims(
            new Claim(RoleClaimType, RequiredRole)
        );
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            jwtValidationService
        );
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ValidBearerToken
        );

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");
        JsonNode json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json["contractVersion"]!.GetValue<int>().Should().Be(1);
        jwtValidationService.CallCount.Should().Be(1);
        documentCacheStatusService.CallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_200_for_real_signed_jwt_with_default_role_claim_type()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        var (jwtValidationService, token) = RealJwtWithClaims("role", [new Claim("role", RequiredRole)]);
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            jwtValidationService,
            roleClaimType: "role"
        );
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");
        JsonNode json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json["contractVersion"]!.GetValue<int>().Should().Be(1);
        documentCacheStatusService.CallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_200_for_real_signed_jwt_with_custom_role_claim_type()
    {
        const string customRoleClaimType = "status_role";
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        var (jwtValidationService, token) = RealJwtWithClaims(
            customRoleClaimType,
            [new Claim(customRoleClaimType, RequiredRole)]
        );
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            jwtValidationService,
            roleClaimType: customRoleClaimType
        );
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        documentCacheStatusService.CallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_403_for_real_signed_jwt_with_only_claim_types_role_when_role_claim_type_differs()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        var (jwtValidationService, token) = RealJwtWithClaims(
            "status_role",
            [new Claim(ClaimTypes.Role, RequiredRole)],
            handler => handler.OutboundClaimTypeMap.Clear()
        );
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            jwtValidationService,
            roleClaimType: "status_role"
        );
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_keeps_existing_health_anonymous_and_independent_from_document_cache_status()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(documentCacheStatusService);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");
        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("\"Name\": \"ApplicationHealthCheck\"");
        content.Should().NotContain("document-cache");
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    private static void AddEssentialMocks(IServiceCollection services)
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._)).Returns([]);
        services.AddTransient(_ => claimSetProvider);

        var dataStoreProvider = A.Fake<IDataStoreProvider>();
        var dataStore = new DataStore(1, "Test", "TestInstance", "test-connection-string", []);
        A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .Returns([dataStore]);
        A.CallTo(() => dataStoreProvider.LoadTenants()).Returns(["TestTenant"]);
        A.CallTo(() => dataStoreProvider.GetAll(A<string?>._)).Returns([dataStore]);
        A.CallTo(() => dataStoreProvider.GetById(A<long>._, A<string?>._)).Returns(dataStore);
        A.CallTo(() => dataStoreProvider.IsLoaded(A<string?>._)).Returns(true);
        A.CallTo(() => dataStoreProvider.TenantExists(A<string>.That.IsNotNull())).Returns(true);
        A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys()).Returns(new List<string> { "" }.AsReadOnly());
        services.AddTransient(_ => dataStoreProvider);

        var tenantValidator = A.Fake<ITenantValidator>();
        A.CallTo(() => tenantValidator.ValidateTenantAsync(A<string>.That.IsNotNull())).Returns(true);
        services.AddTransient(_ => tenantValidator);

        var connectionStringProvider = A.Fake<IConnectionStringProvider>();
        A.CallTo(() => connectionStringProvider.GetConnectionString(A<long>._, A<string?>._))
            .Returns("test-connection-string");
        A.CallTo(() => connectionStringProvider.GetHealthCheckConnectionString())
            .Returns("test-connection-string");
        services.AddTransient(_ => connectionStringProvider);

        var backendMappingInitializer = A.Fake<IBackendMappingInitializer>();
        A.CallTo(() => backendMappingInitializer.InitializeAsync(A<CancellationToken>._))
            .Returns(Task.CompletedTask);
        services.Replace(ServiceDescriptor.Singleton(backendMappingInitializer));
    }

    private sealed class ScriptedDocumentCacheStatusService(DocumentCacheStatusResponse response)
        : IDocumentCacheStatusService
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<DocumentCacheStatusResponse> GetStatusAsync(
            CancellationToken cancellationToken = default,
            DocumentCacheStatusEvaluationMode evaluationMode =
                DocumentCacheStatusEvaluationMode.RuntimeEndpoint,
            TimeSpan? endpointTimeoutOverride = null
        )
        {
            evaluationMode.Should().Be(DocumentCacheStatusEvaluationMode.RuntimeEndpoint);
            endpointTimeoutOverride.Should().BeNull();
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

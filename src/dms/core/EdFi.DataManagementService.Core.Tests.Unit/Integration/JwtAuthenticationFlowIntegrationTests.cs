// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit.Integration;

/// <summary>
/// Integration tests for the complete JWT authentication and authorization flow
/// </summary>
[TestFixture]
public class JwtAuthenticationFlowIntegrationTests
{
    private ServiceProvider _serviceProvider = null!;
    private IApiService _apiService = null!;
    private RSA _rsa = null!;
    private RsaSecurityKey _signingKey = null!;
    private readonly string _issuer = "https://test-issuer.com";
    private readonly string _audience = "ed-fi-ods-api";
    private IdentitySettings _identitySettings = null!;

    [SetUp]
    public void Setup()
    {
        // Create RSA key for signing test tokens
        _rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(_rsa);

        _identitySettings = new IdentitySettings
        {
            Authority = _issuer,
            Audience = _audience,
            RequireHttpsMetadata = false,
            RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
            ClientRole = "vendor",
        };

        // Setup dependency injection container
        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder => builder.AddConsole());

        // Add configuration
        services.AddSingleton<IOptions<IdentitySettings>>(Options.Create(_identitySettings));
        services.AddSingleton<IOptions<AppSettings>>(
            Options.Create(
                new AppSettings
                {
                    BypassStringTypeCoercion = false,
                    AllowIdentityUpdateOverrides = "",
                    MaskRequestBodyInLogs = false,
                    MaximumPageSize = 100,
                    UseApiSchemaPath = false,
                    ApiSchemaPath = null,
                    AuthenticationService = null,
                    EnableManagementEndpoints = false,
                }
            )
        );

        // Add JWT services
        services.AddSingleton<HttpClient>();
        services.AddTransient<IJwtTokenValidator, MockJwtTokenValidator>();
        services.AddSingleton<IApiClientDetailsProvider, MockApiClientDetailsProvider>();

        // Add core pipeline services
        services.AddTransient<DecodeJwtToClientAuthorizationsMiddleware>();
        services.AddTransient<ResourceActionAuthorizationMiddleware>();

        // Add security services
        services.AddTransient<IClaimSetCacheService, MockClaimSetCacheService>();

        // Add API schema services
        var mockApiSchemaProvider = A.Fake<IApiSchemaProvider>();

        // Build a proper JSON schema for School resource
        var builder = new JsonSchemaBuilder();
        builder.Title("Ed-Fi.School");
        builder.Description("This entity represents an educational organization");
        builder.Schema("https://json-schema.org/draft/2020-12/schema");
        builder.AdditionalProperties(false);
        builder
            .Properties(
                ("schoolId", new JsonSchemaBuilder().Type(SchemaValueType.Integer)),
                (
                    "nameOfInstitution",
                    new JsonSchemaBuilder().Type(SchemaValueType.String).Pattern("^(?!\\s*$).+")
                )
            )
            .Required("schoolId", "nameOfInstitution");

        // Create proper API schema structure with education organization hierarchy
        var coreApiSchema = new JsonObject
        {
            ["projectSchema"] = new JsonObject
            {
                ["abstractResources"] = new JsonObject(),
                ["caseInsensitiveEndpointNameMapping"] = new JsonObject { ["schools"] = "schools" },
                ["description"] = "Ed-Fi description",
                ["educationOrganizationTypes"] = new JsonArray { "School" },
                ["educationOrganizationHierarchy"] = new JsonObject
                {
                    ["School"] = new JsonObject(), // School has no parent education organizations
                },
                ["isExtensionProject"] = false,
                ["projectName"] = "Ed-Fi",
                ["projectVersion"] = "5.0.0",
                ["projectEndpointName"] = "ed-fi",
                ["resourceNameMapping"] = new JsonObject { ["schools"] = "School" },
                ["resourceSchemas"] = new JsonObject
                {
                    ["schools"] = new JsonObject
                    {
                        ["allowIdentityUpdates"] = false,
                        ["resourceName"] = "School",
                        ["isDescriptor"] = false,
                        ["isSchoolYearEnumeration"] = false,
                        ["isResourceExtension"] = false,
                        ["isSubclass"] = false,
                        ["subclassType"] = "",
                        ["documentPathsMapping"] = new JsonObject(),
                        ["jsonSchemaForInsert"] = JsonNode.Parse(JsonSerializer.Serialize(builder.Build()!)),
                        ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
                        ["identityFullNames"] = new JsonArray { "schoolId" },
                        ["booleanJsonPaths"] = new JsonArray(),
                        ["numericJsonPaths"] = new JsonArray(),
                        ["decimalPropertyValidationInfos"] = new JsonArray(),
                        ["dateTimeJsonPaths"] = new JsonArray(),
                        ["dateJsonPaths"] = new JsonArray(),
                        ["equalityConstraints"] = new JsonArray(),
                        ["arrayUniquenessConstraints"] = new JsonArray(),
                        ["securableElements"] = new JsonObject
                        {
                            ["Namespace"] = new JsonArray(),
                            ["EducationOrganization"] = new JsonArray(),
                            ["Student"] = new JsonArray(),
                            ["Contact"] = new JsonArray(),
                            ["Staff"] = new JsonArray(),
                        },
                        ["educationOrganizationTypes"] = new JsonArray { "School" },
                        ["educationOrganizationHierarchy"] = new JsonObject(),
                        ["authorizationPathways"] = new JsonArray(),
                    },
                },
            },
        };

        var apiSchemaNodes = new ApiSchemaDocumentNodes(coreApiSchema, []);

        var fixedReloadId = Guid.NewGuid(); // Use a fixed GUID for consistent versioning
        A.CallTo(() => mockApiSchemaProvider.GetApiSchemaNodes()).Returns(apiSchemaNodes);
        A.CallTo(() => mockApiSchemaProvider.IsSchemaValid).Returns(true);
        A.CallTo(() => mockApiSchemaProvider.ReloadId).Returns(fixedReloadId);
        A.CallTo(() => mockApiSchemaProvider.ApiSchemaFailures).Returns([]);

        services.AddSingleton(mockApiSchemaProvider);

        // Add other required services as mocks
        services.AddTransient(_ => A.Fake<IDocumentStoreRepository>());
        services.AddTransient<IDocumentValidator, DocumentValidator>();
        services.AddTransient(_ => A.Fake<IQueryHandler>());
        services.AddTransient<IMatchingDocumentUuidsValidator, MatchingDocumentUuidsValidator>();
        services.AddTransient<IEqualityConstraintValidator, EqualityConstraintValidator>();
        services.AddTransient<IDecimalValidator, DecimalValidator>();
        // Configure authorization service factory
        var mockAuthorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();
        A.CallTo(() =>
                mockAuthorizationServiceFactory.GetByName<IAuthorizationValidator>(
                    "NoFurtherAuthorizationRequired"
                )
            )
            .Returns(new NoFurtherAuthorizationRequiredValidator());
        A.CallTo(() =>
                mockAuthorizationServiceFactory.GetByName<IAuthorizationFiltersProvider>(
                    "NoFurtherAuthorizationRequired"
                )
            )
            .Returns(new NoFurtherAuthorizationRequiredFiltersProvider());
        // Return null for any other strategy (to trigger 403 responses)
        A.CallTo(() =>
                mockAuthorizationServiceFactory.GetByName<IAuthorizationValidator>(
                    A<string>.That.Not.IsEqualTo("NoFurtherAuthorizationRequired")
                )
            )
            .Returns(null);
        A.CallTo(() =>
                mockAuthorizationServiceFactory.GetByName<IAuthorizationFiltersProvider>(
                    A<string>.That.Not.IsEqualTo("NoFurtherAuthorizationRequired")
                )
            )
            .Returns(null);
        services.AddTransient(_ => mockAuthorizationServiceFactory);
        services.AddKeyedSingleton<ResiliencePipeline>(
            "backendResiliencePipeline",
            (_, _) => ResiliencePipeline.Empty
        );
        services.AddTransient<ResourceLoadOrderCalculator>();
        services.AddTransient(_ => A.Fake<IUploadApiSchemaService>());

        // Add ApiService
        services.AddTransient<IApiService, ApiService>();

        _serviceProvider = services.BuildServiceProvider();
        _apiService = _serviceProvider.GetRequiredService<IApiService>();
    }

    [TearDown]
    public void TearDown()
    {
        _rsa?.Dispose();
        _serviceProvider?.Dispose();
    }

    [Test]
    public async Task CompleteJwtFlow_ValidTokenWithPermissions_SuccessfulRequest()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "SIS-Vendor"),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {token}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        // The actual response depends on the document store mock, but we should not get 401/403
        response.StatusCode.Should().NotBe(401, "JWT authentication should succeed");
        response.StatusCode.Should().NotBe(403, "Authorization should succeed with proper claim set");
    }

    [Test]
    public async Task CompleteJwtFlow_MissingAuthorizationHeader_Returns401()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string>(), // No Authorization header
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(401);
        var responseBody = JsonSerializer.Deserialize<JsonObject>(response.Body!);
        responseBody!["error"]!.ToString().Should().Be("Missing Authorization header");
    }

    [Test]
    public async Task CompleteJwtFlow_InvalidToken_Returns401()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer invalid.jwt.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(401);
        var responseBody = JsonSerializer.Deserialize<JsonObject>(response.Body!);
        responseBody!["error"]!.ToString().Should().Be("Invalid token");
    }

    [Test]
    public async Task CompleteJwtFlow_ValidTokenNoPermissions_Returns403()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "NoPermissions-Vendor"), // Claim set with no permissions
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {token}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        if (response.StatusCode == 404)
        {
            Console.WriteLine($"Got 404. Response body: {response.Body}");
        }
        response.StatusCode.Should().Be(403);
    }

    [Test]
    public async Task CompleteJwtFlow_TokenWithEducationOrganizationFilter_AppliesFilter()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "SIS-Vendor"),
            new Claim("educationOrganizationId", "123456"),
            new Claim("educationOrganizationId", "789012"),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {token}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().NotBe(401);
        response.StatusCode.Should().NotBe(403);

        // Verify that the education organization IDs were extracted from the token
        var provider =
            _serviceProvider.GetRequiredService<IApiClientDetailsProvider>() as MockApiClientDetailsProvider;
        provider!.LastExtractedEducationOrganizationIds.Should().Contain(new EducationOrganizationId(123456));
        provider.LastExtractedEducationOrganizationIds.Should().Contain(new EducationOrganizationId(789012));
    }

    [Test]
    public async Task CompleteJwtFlow_TokenWithNamespacePrefixFilter_AppliesFilter()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "SIS-Vendor"),
            new Claim("namespacePrefix", "uri://ed-fi.org"),
            new Claim("namespacePrefix", "uri://example.org"),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {token}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().NotBe(401);
        response.StatusCode.Should().NotBe(403);

        // Verify that the namespace prefixes were extracted from the token
        var provider =
            _serviceProvider.GetRequiredService<IApiClientDetailsProvider>() as MockApiClientDetailsProvider;
        provider!.LastExtractedNamespacePrefixes.Should().Contain(new NamespacePrefix("uri://ed-fi.org"));
        provider.LastExtractedNamespacePrefixes.Should().Contain(new NamespacePrefix("uri://example.org"));
    }

    [Test]
    public async Task CompleteJwtFlow_ExpiredToken_Returns401()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "SIS-Vendor"),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(-10)); // Expired 10 minutes ago

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {token}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(401);
        var responseBody = JsonSerializer.Deserialize<JsonObject>(response.Body!);
        responseBody!["error"]!.ToString().Should().Contain("expired");
    }

    [Test]
    public async Task CompleteJwtFlow_ConcurrentRequests_AllSucceed()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "SIS-Vendor"),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var tasks = new List<Task<IFrontendResponse>>();

        // Create 10 concurrent requests
        for (int i = 0; i < 10; i++)
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/schools",
                Body: $"{{\"schoolId\": {12345 + i}, \"nameOfInstitution\": \"Test School {i}\"}}",
                Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {token}" } },
                QueryParameters: new Dictionary<string, string>(),
                TraceId: new TraceId($"test-trace-id-{i}")
            );

            tasks.Add(_apiService.Upsert(frontendRequest));
        }

        // Act
        var responses = await Task.WhenAll(tasks);

        // Assert
        responses.Should().HaveCount(10);
        foreach (var response in responses)
        {
            response.Should().NotBeNull();
            response.StatusCode.Should().NotBe(401, "Valid token should not return 401");
            response.StatusCode.Should().NotBe(403, "Valid permissions should not return 403");
        }
    }

    [Test]
    public async Task CompleteJwtFlow_TokenNearExpiration_HandledGracefully()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "SIS-Vendor"),
        };

        // Token expires in 30 seconds
        var token = CreateTestToken(claims, DateTime.UtcNow.AddSeconds(30));

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {token}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().NotBe(401, "Token that hasn't expired yet should be valid");
        response.StatusCode.Should().NotBe(403);
    }

    [Test]
    public async Task CompleteJwtFlow_DifferentClaimSets_ApplyCorrectPermissions()
    {
        // Arrange - Test with a claim set that has no permissions
        var claimsNoPermissions = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "NoPermissions-Vendor"),
        };

        var tokenNoPermissions = CreateTestToken(claimsNoPermissions, DateTime.UtcNow.AddMinutes(5));

        var requestNoPermissions = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {tokenNoPermissions}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id-1")
        );

        // Arrange - Test with a claim set that has permissions
        var claimsWithPermissions = new[]
        {
            new Claim("sub", "user456"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "SIS-Vendor"),
        };

        var tokenWithPermissions = CreateTestToken(claimsWithPermissions, DateTime.UtcNow.AddMinutes(5));

        var requestWithPermissions = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 67890, "nameOfInstitution": "Another School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {tokenWithPermissions}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id-2")
        );

        // Act
        var responseNoPermissions = await _apiService.Upsert(requestNoPermissions);
        var responseWithPermissions = await _apiService.Upsert(requestWithPermissions);

        // Assert
        responseNoPermissions.Should().NotBeNull();
        responseNoPermissions.StatusCode.Should().Be(403, "User with no permissions should get 403");

        responseWithPermissions.Should().NotBeNull();
        responseWithPermissions.StatusCode.Should().NotBe(401);
        responseWithPermissions.StatusCode.Should().NotBe(403, "User with proper permissions should succeed");
    }

    [Test]
    public async Task CompleteJwtFlow_MissingRequiredRole_Returns401()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, "wrong-role"), // Wrong role
            new Claim("aud", _audience),
            new Claim("scope", "SIS-Vendor"),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School"}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {token}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(401);
        var responseBody = JsonSerializer.Deserialize<JsonObject>(response.Body!);
        responseBody!["error"]!.ToString().Should().Be("Insufficient permissions");
    }

    [Test]
    public async Task CompleteJwtFlow_ComplexAuthorizationScenario_HandledCorrectly()
    {
        // Arrange - User with specific education org access
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim(_identitySettings.RoleClaimType, _identitySettings.ClientRole),
            new Claim("aud", _audience),
            new Claim("scope", "SIS-Vendor"),
            new Claim("educationOrganizationId", "100001"),
            new Claim("educationOrganizationId", "100002"),
            new Claim("namespacePrefix", "uri://example.org/school1"),
            new Claim("namespacePrefix", "uri://example.org/school2"),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/schools",
            Body: """{"schoolId": 12345, "nameOfInstitution": "Test School", "educationOrganizationId": 100001}""",
            Headers: new Dictionary<string, string> { { "Authorization", $"Bearer {token}" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        // Act
        var response = await _apiService.Upsert(frontendRequest);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().NotBe(401);
        response.StatusCode.Should().NotBe(403);

        // Verify complex authorization details were extracted
        var provider =
            _serviceProvider.GetRequiredService<IApiClientDetailsProvider>() as MockApiClientDetailsProvider;
        provider!.LastExtractedEducationOrganizationIds.Should().HaveCount(2);
        provider.LastExtractedNamespacePrefixes.Should().HaveCount(2);
    }

    private string CreateTestToken(Claim[] claims, DateTime expires)
    {
        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            claims: claims,
            expires: expires,
            signingCredentials: credentials
        );

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(token);
    }

    // Mock implementations for integration testing

    private class MockJwtTokenValidator : IJwtTokenValidator
    {
        public Task<JwtValidationResult> ValidateTokenAsync(string token, IdentitySettings settings)
        {
            if (token == "invalid.jwt.token")
            {
                return Task.FromResult(new JwtValidationResult(false, new List<Claim>(), "Invalid token"));
            }

            try
            {
                var handler = new JwtSecurityTokenHandler();
                // Disable default claim type mapping to preserve original claim types
                handler.InboundClaimTypeMap.Clear();
                var jwtToken = handler.ReadJwtToken(token);

                // Check if token is expired
                if (jwtToken.ValidTo < DateTime.UtcNow)
                {
                    return Task.FromResult(
                        new JwtValidationResult(false, new List<Claim>(), "Token expired")
                    );
                }

                // For testing, we accept any properly formatted JWT that hasn't expired
                return Task.FromResult(new JwtValidationResult(true, jwtToken.Claims.ToList()));
            }
            catch
            {
                return Task.FromResult(
                    new JwtValidationResult(false, new List<Claim>(), "Token validation failed")
                );
            }
        }
    }

    private class MockApiClientDetailsProvider : IApiClientDetailsProvider
    {
        public List<EducationOrganizationId> LastExtractedEducationOrganizationIds { get; private set; } =
            new();
        public List<NamespacePrefix> LastExtractedNamespacePrefixes { get; private set; } = new();

        public ClientAuthorizations RetrieveApiClientDetailsFromToken(
            string jwtTokenHashCode,
            IList<Claim> claims
        )
        {
            Console.WriteLine(
                $"MockApiClientDetailsProvider.RetrieveApiClientDetailsFromToken called with {claims.Count} claims"
            );
            foreach (var claim in claims)
            {
                Console.WriteLine($"  Claim: {claim.Type} = {claim.Value}");
            }

            var claimSetName = claims.FirstOrDefault(c => c.Type == "scope")?.Value ?? "DefaultClaimSet";

            var educationOrgIds = claims
                .Where(c => c.Type == "educationOrganizationId")
                .Select(c => new EducationOrganizationId(long.Parse(c.Value)))
                .ToList();

            var namespacePrefixes = claims
                .Where(c => c.Type == "namespacePrefix")
                .Select(c => new NamespacePrefix(c.Value))
                .ToList();

            Console.WriteLine(
                $"  Extracted {educationOrgIds.Count} education org IDs and {namespacePrefixes.Count} namespace prefixes"
            );

            LastExtractedEducationOrganizationIds = educationOrgIds;
            LastExtractedNamespacePrefixes = namespacePrefixes;

            return new ClientAuthorizations(
                TokenId: jwtTokenHashCode,
                ClaimSetName: claimSetName,
                EducationOrganizationIds: educationOrgIds,
                NamespacePrefixes: namespacePrefixes
            );
        }
    }

    private class MockClaimSetCacheService : IClaimSetCacheService
    {
        public Task<IList<ClaimSet>> GetClaimSets()
        {
            IList<ClaimSet> claimSets = new List<ClaimSet>
            {
                new ClaimSet(
                    Name: "SIS-Vendor",
                    ResourceClaims:
                    [
                        new ResourceClaim(
                            $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                            "Create",
                            [new AuthorizationStrategy("NoFurtherAuthorizationRequired")]
                        ),
                        new ResourceClaim(
                            $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                            "Read",
                            [new AuthorizationStrategy("NoFurtherAuthorizationRequired")]
                        ),
                        new ResourceClaim(
                            $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                            "Update",
                            [new AuthorizationStrategy("NoFurtherAuthorizationRequired")]
                        ),
                        new ResourceClaim(
                            $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                            "Delete",
                            [new AuthorizationStrategy("NoFurtherAuthorizationRequired")]
                        ),
                    ]
                ),
                new ClaimSet(
                    Name: "NoPermissions-Vendor",
                    ResourceClaims: [] // No permissions
                ),
            };

            return Task.FromResult(claimSets);
        }
    }
}

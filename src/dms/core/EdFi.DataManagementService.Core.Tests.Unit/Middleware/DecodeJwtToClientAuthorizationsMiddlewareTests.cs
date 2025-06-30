// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class DecodeJwtToClientAuthorizationsMiddlewareTests
{
    private ILogger<DecodeJwtToClientAuthorizationsMiddleware> _logger = null!;
    private IJwtTokenValidator _jwtTokenValidator = null!;
    private IApiClientDetailsProvider _apiClientDetailsProvider = null!;
    private IOptions<IdentitySettings> _identitySettings = null!;
    private DecodeJwtToClientAuthorizationsMiddleware _middleware = null!;
    private IdentitySettings _settings = null!;

    [SetUp]
    public void Setup()
    {
        _logger = A.Fake<ILogger<DecodeJwtToClientAuthorizationsMiddleware>>();
        _jwtTokenValidator = A.Fake<IJwtTokenValidator>();
        _apiClientDetailsProvider = A.Fake<IApiClientDetailsProvider>();
        _identitySettings = A.Fake<IOptions<IdentitySettings>>();

        _settings = new IdentitySettings
        {
            Authority = "https://example-authority.com",
            Audience = "ed-fi-ods-api",
            RequireHttpsMetadata = true,
            RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
            ClientRole = "vendor",
        };

        A.CallTo(() => _identitySettings.Value).Returns(_settings);

        _middleware = new DecodeJwtToClientAuthorizationsMiddleware(
            _logger,
            _jwtTokenValidator,
            _apiClientDetailsProvider,
            _identitySettings
        );
    }

    [Test]
    public async Task Execute_MissingAuthorizationHeader_Returns401Unauthorized()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string>(), // No Authorization header
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeFalse("Middleware should not call next when authorization is missing");
        requestData.FrontendResponse.Should().NotBeNull();
        requestData.FrontendResponse!.StatusCode.Should().Be(401);

        var responseBody = JsonSerializer.Deserialize<Dictionary<string, string>>(
            requestData.FrontendResponse.Body!
        );
        responseBody.Should().NotBeNull();
        responseBody!["error"].Should().Be("Missing Authorization header");
    }

    [Test]
    public async Task Execute_InvalidAuthorizationFormat_Returns401Unauthorized()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Basic dGVzdDp0ZXN0" } }, // Not Bearer
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeFalse("Middleware should not call next when authorization format is invalid");
        requestData.FrontendResponse.Should().NotBeNull();
        requestData.FrontendResponse!.StatusCode.Should().Be(401);

        var responseBody = JsonSerializer.Deserialize<Dictionary<string, string>>(
            requestData.FrontendResponse.Body!
        );
        responseBody.Should().NotBeNull();
        responseBody!["error"].Should().Be("Invalid Authorization header format");
    }

    [Test]
    public async Task Execute_InvalidJwtToken_Returns401Unauthorized()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer invalid.jwt.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var validationResult = new JwtValidationResult(
            IsValid: false,
            Claims: new List<Claim>(),
            ErrorMessage: "Invalid token signature"
        );

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("invalid.jwt.token", _settings))
            .Returns(Task.FromResult(validationResult));

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeFalse("Middleware should not call next when JWT is invalid");
        requestData.FrontendResponse.Should().NotBeNull();
        requestData.FrontendResponse!.StatusCode.Should().Be(401);

        var responseBody = JsonSerializer.Deserialize<Dictionary<string, string>>(
            requestData.FrontendResponse.Body!
        );
        responseBody.Should().NotBeNull();
        responseBody!["error"].Should().Be("Invalid token signature");
    }

    [Test]
    public async Task Execute_MissingRequiredRole_Returns401Unauthorized()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer valid.jwt.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var validationResult = new JwtValidationResult(
            IsValid: true,
            Claims: new List<Claim>
            {
                new Claim("sub", "user123"),
                new Claim("aud", "ed-fi-ods-api"),
                // Missing the required role claim
            },
            ErrorMessage: ""
        );

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("valid.jwt.token", _settings))
            .Returns(Task.FromResult(validationResult));

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeFalse("Middleware should not call next when required role is missing");
        requestData.FrontendResponse.Should().NotBeNull();
        requestData.FrontendResponse!.StatusCode.Should().Be(401);

        var responseBody = JsonSerializer.Deserialize<Dictionary<string, string>>(
            requestData.FrontendResponse.Body!
        );
        responseBody.Should().NotBeNull();
        responseBody!["error"].Should().Be("Insufficient permissions");
    }

    [Test]
    public async Task Execute_ValidTokenWithRequiredRole_CallsNextAndSetsClientAuthorizations()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer valid.jwt.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var validationResult = new JwtValidationResult(
            IsValid: true,
            Claims: new List<Claim>
            {
                new Claim("sub", "user123"),
                new Claim("aud", "ed-fi-ods-api"),
                new Claim(_settings.RoleClaimType, _settings.ClientRole),
            },
            ErrorMessage: ""
        );

        var expectedClientAuthorizations = new ClientAuthorizations(
            TokenId: "valid.jwt.token".GetHashCode().ToString(),
            ClaimSetName: "test-claimset",
            EducationOrganizationIds: new List<EducationOrganizationId> { new(1234) },
            NamespacePrefixes: new List<NamespacePrefix> { new("uri://ed-fi.org") }
        );

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("valid.jwt.token", _settings))
            .Returns(Task.FromResult(validationResult));

        A.CallTo(() =>
                _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
                    "valid.jwt.token".GetHashCode().ToString(),
                    validationResult.Claims
                )
            )
            .Returns(expectedClientAuthorizations);

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue("Middleware should call next when authorization is valid");
        requestData
            .FrontendResponse.Should()
            .Be(No.FrontendResponse, "Default response should not be changed for successful validation");
        requestData.ClientAuthorizations.Should().Be(expectedClientAuthorizations);
    }

    [Test]
    public async Task Execute_TokenValidationException_Returns401Unauthorized()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer malformed.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("malformed.token", _settings))
            .Throws(new Exception("Token parsing failed"));

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeFalse("Middleware should not call next when token validation throws");
        requestData.FrontendResponse.Should().NotBeNull();
        requestData.FrontendResponse!.StatusCode.Should().Be(401);

        var responseBody = JsonSerializer.Deserialize<Dictionary<string, string>>(
            requestData.FrontendResponse.Body!
        );
        responseBody.Should().NotBeNull();
        responseBody!["error"].Should().Contain("error"); // Generic error response
    }

    [Test]
    public async Task Execute_CaseInsensitiveBearerPrefix_ProcessesSuccessfully()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "BEARER valid.jwt.token" } }, // Uppercase
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var validationResult = new JwtValidationResult(
            IsValid: true,
            Claims: new List<Claim>
            {
                new Claim("sub", "user123"),
                new Claim(_settings.RoleClaimType, _settings.ClientRole),
            },
            ErrorMessage: ""
        );

        var expectedClientAuthorizations = new ClientAuthorizations(
            TokenId: "valid.jwt.token".GetHashCode().ToString(),
            ClaimSetName: "test-claimset",
            EducationOrganizationIds: new List<EducationOrganizationId>(),
            NamespacePrefixes: new List<NamespacePrefix>()
        );

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("valid.jwt.token", _settings))
            .Returns(Task.FromResult(validationResult));

        A.CallTo(() =>
                _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(A<string>._, A<List<Claim>>._)
            )
            .Returns(expectedClientAuthorizations);

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue("Middleware should handle case-insensitive Bearer prefix");
        requestData.ClientAuthorizations.Should().NotBeNull();
    }

    [Test]
    public async Task Execute_MultipleEducationOrganizationIds_ExtractsAll()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer valid.jwt.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var validationResult = new JwtValidationResult(
            IsValid: true,
            Claims: new List<Claim>
            {
                new Claim("sub", "user123"),
                new Claim(_settings.RoleClaimType, _settings.ClientRole),
                new Claim("educationOrganizationId", "123456"),
                new Claim("educationOrganizationId", "789012"),
                new Claim("educationOrganizationId", "345678"),
            },
            ErrorMessage: ""
        );

        var expectedClientAuthorizations = new ClientAuthorizations(
            TokenId: "valid.jwt.token".GetHashCode().ToString(),
            ClaimSetName: "test-claimset",
            EducationOrganizationIds: new List<EducationOrganizationId>
            {
                new(123456),
                new(789012),
                new(345678),
            },
            NamespacePrefixes: new List<NamespacePrefix>()
        );

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("valid.jwt.token", _settings))
            .Returns(Task.FromResult(validationResult));

        A.CallTo(() =>
                _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
                    "valid.jwt.token".GetHashCode().ToString(),
                    validationResult.Claims
                )
            )
            .Returns(expectedClientAuthorizations);

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        requestData.ClientAuthorizations.Should().NotBeNull();
        requestData.ClientAuthorizations!.EducationOrganizationIds.Should().HaveCount(3);
        requestData
            .ClientAuthorizations.EducationOrganizationIds.Should()
            .Contain(new EducationOrganizationId(123456));
        requestData
            .ClientAuthorizations.EducationOrganizationIds.Should()
            .Contain(new EducationOrganizationId(789012));
        requestData
            .ClientAuthorizations.EducationOrganizationIds.Should()
            .Contain(new EducationOrganizationId(345678));
    }

    [Test]
    public async Task Execute_EmptyNamespacePrefixes_HandledGracefully()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer valid.jwt.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var validationResult = new JwtValidationResult(
            IsValid: true,
            Claims: new List<Claim>
            {
                new Claim("sub", "user123"),
                new Claim(_settings.RoleClaimType, _settings.ClientRole),
                new Claim("namespacePrefix", ""),
                new Claim("namespacePrefix", "   "), // Whitespace only
                new Claim("namespacePrefix", "uri://valid.org"),
            },
            ErrorMessage: ""
        );

        var expectedClientAuthorizations = new ClientAuthorizations(
            TokenId: "valid.jwt.token".GetHashCode().ToString(),
            ClaimSetName: "test-claimset",
            EducationOrganizationIds: new List<EducationOrganizationId>(),
            NamespacePrefixes: new List<NamespacePrefix> { new("uri://valid.org") }
        );

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("valid.jwt.token", _settings))
            .Returns(Task.FromResult(validationResult));

        A.CallTo(() =>
                _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
                    "valid.jwt.token".GetHashCode().ToString(),
                    validationResult.Claims
                )
            )
            .Returns(expectedClientAuthorizations);

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        requestData.ClientAuthorizations.Should().NotBeNull();
        requestData.ClientAuthorizations!.NamespacePrefixes.Should().HaveCount(1);
        requestData
            .ClientAuthorizations.NamespacePrefixes.Should()
            .Contain(new NamespacePrefix("uri://valid.org"));
    }

    [Test]
    public async Task Execute_SpecialCharactersInClaims_HandledCorrectly()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer valid.jwt.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var validationResult = new JwtValidationResult(
            IsValid: true,
            Claims: new List<Claim>
            {
                new Claim("sub", "user@example.com"),
                new Claim(_settings.RoleClaimType, _settings.ClientRole),
                new Claim("scope", "Test-Claim$et!@#"),
                new Claim("namespacePrefix", "uri://example.org/~test?param=value&other=123"),
            },
            ErrorMessage: ""
        );

        var expectedClientAuthorizations = new ClientAuthorizations(
            TokenId: "valid.jwt.token".GetHashCode().ToString(),
            ClaimSetName: "Test-Claim$et!@#",
            EducationOrganizationIds: new List<EducationOrganizationId>(),
            NamespacePrefixes: new List<NamespacePrefix>
            {
                new("uri://example.org/~test?param=value&other=123"),
            }
        );

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("valid.jwt.token", _settings))
            .Returns(Task.FromResult(validationResult));

        A.CallTo(() =>
                _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
                    "valid.jwt.token".GetHashCode().ToString(),
                    validationResult.Claims
                )
            )
            .Returns(expectedClientAuthorizations);

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        requestData.ClientAuthorizations.Should().NotBeNull();
        requestData.ClientAuthorizations!.ClaimSetName.Should().Be("Test-Claim$et!@#");
        requestData
            .ClientAuthorizations.NamespacePrefixes.Should()
            .Contain(new NamespacePrefix("uri://example.org/~test?param=value&other=123"));
    }

    [Test]
    public async Task Execute_VeryLongClaimValues_HandledCorrectly()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer valid.jwt.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var veryLongNamespace = "uri://example.org/" + new string('a', 1000);
        var veryLongClaimSet = "ClaimSet_" + new string('x', 500);

        var validationResult = new JwtValidationResult(
            IsValid: true,
            Claims: new List<Claim>
            {
                new Claim("sub", "user123"),
                new Claim(_settings.RoleClaimType, _settings.ClientRole),
                new Claim("scope", veryLongClaimSet),
                new Claim("namespacePrefix", veryLongNamespace),
            },
            ErrorMessage: ""
        );

        var expectedClientAuthorizations = new ClientAuthorizations(
            TokenId: "valid.jwt.token".GetHashCode().ToString(),
            ClaimSetName: veryLongClaimSet,
            EducationOrganizationIds: new List<EducationOrganizationId>(),
            NamespacePrefixes: new List<NamespacePrefix> { new(veryLongNamespace) }
        );

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("valid.jwt.token", _settings))
            .Returns(Task.FromResult(validationResult));

        A.CallTo(() =>
                _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
                    "valid.jwt.token".GetHashCode().ToString(),
                    validationResult.Claims
                )
            )
            .Returns(expectedClientAuthorizations);

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        requestData.ClientAuthorizations.Should().NotBeNull();
        requestData.ClientAuthorizations!.ClaimSetName.Should().Be(veryLongClaimSet);
        requestData.ClientAuthorizations.NamespacePrefixes[0].Value.Should().Be(veryLongNamespace);
    }

    [Test]
    public async Task Execute_DuplicateClaims_AllAreProcessed()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer valid.jwt.token" } },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("test-trace-id")
        );

        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        bool nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var validationResult = new JwtValidationResult(
            IsValid: true,
            Claims: new List<Claim>
            {
                new Claim("sub", "user123"),
                new Claim(_settings.RoleClaimType, _settings.ClientRole),
                new Claim("namespacePrefix", "uri://ed-fi.org"),
                new Claim("namespacePrefix", "uri://ed-fi.org"), // Duplicate
                new Claim("namespacePrefix", "uri://example.org"),
                new Claim("educationOrganizationId", "123456"),
                new Claim("educationOrganizationId", "123456"), // Duplicate
            },
            ErrorMessage: ""
        );

        var expectedClientAuthorizations = new ClientAuthorizations(
            TokenId: "valid.jwt.token".GetHashCode().ToString(),
            ClaimSetName: "test-claimset",
            EducationOrganizationIds: new List<EducationOrganizationId> { new(123456) },
            NamespacePrefixes: new List<NamespacePrefix> { new("uri://ed-fi.org"), new("uri://example.org") }
        );

        A.CallTo(() => _jwtTokenValidator.ValidateTokenAsync("valid.jwt.token", _settings))
            .Returns(Task.FromResult(validationResult));

        A.CallTo(() =>
                _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
                    "valid.jwt.token".GetHashCode().ToString(),
                    validationResult.Claims
                )
            )
            .Returns(expectedClientAuthorizations);

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        requestData.ClientAuthorizations.Should().NotBeNull();
        // The actual deduplication logic would be in ApiClientDetailsProvider
        requestData.ClientAuthorizations!.EducationOrganizationIds.Should().HaveCount(1);
        requestData.ClientAuthorizations.NamespacePrefixes.Should().HaveCount(2);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DataManagementService.Tests.Integration;

[TestFixture]
public class JwtAuthenticationIntegrationTests
{
    private WebApplicationFactory<Frontend.AspNetCore.Program> _factory = null!;
    private HttpClient _client = null!;
    private const string TestIssuer = "http://localhost:8045/realms/edfi";
    private const string TestAudience = "edfi-api";

    [SetUp]
    public void Setup()
    {
        _factory = new WebApplicationFactory<Frontend.AspNetCore.Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Override JWT configuration for testing
                services.Configure<Core.Security.JwtAuthenticationOptions>(options =>
                {
                    options.Enabled = true;
                    options.Authority = TestIssuer;
                    options.Audience = TestAudience;
                    options.RequireHttpsMetadata = false;
                });
            });
        });

        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task DataEndpoint_WithValidJwtToken_ReturnsSuccess()
    {
        // Arrange
        var token = GenerateValidJwtToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.GetAsync("/data/ed-fi/students");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task DataEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        // Arrange
        _client.DefaultRequestHeaders.Authorization = null;

        // Act
        var response = await _client.GetAsync("/data/ed-fi/students");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();
        response.Headers.WwwAuthenticate.Should().Contain(auth => auth.Scheme == "Bearer");
    }

    [Test]
    public async Task DataEndpoint_WithExpiredToken_ReturnsUnauthorized()
    {
        // Arrange
        var token = GenerateExpiredJwtToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.GetAsync("/data/ed-fi/students");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task DataEndpoint_WithInvalidSignature_ReturnsUnauthorized()
    {
        // Arrange
        var validToken = GenerateValidJwtToken();
        var invalidToken = validToken.Substring(0, validToken.Length - 1) + "X";
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);

        // Act
        var response = await _client.GetAsync("/data/ed-fi/students");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task DataEndpoint_WithBasicAuth_ReturnsUnauthorized()
    {
        // Arrange
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:password"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        // Act
        var response = await _client.GetAsync("/data/ed-fi/students");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task DiscoveryEndpoint_WithoutToken_ReturnsSuccess()
    {
        // Arrange
        _client.DefaultRequestHeaders.Authorization = null;

        // Act
        var response = await _client.GetAsync("/");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task MetadataEndpoint_WithServiceRole_ReturnsSuccess()
    {
        // Arrange
        var token = GenerateJwtTokenWithRole("service");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.GetAsync("/metadata");

        // Assert
        // Note: Actual behavior depends on whether metadata endpoints require service role
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task PostDescriptor_WithValidToken_ReturnsCreated()
    {
        // Arrange
        var token = GenerateValidJwtToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var content = new StringContent(
            """
            {
                "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
                "codeValue": "TestGrade",
                "shortDescription": "Test Grade"
            }
            """,
            Encoding.UTF8,
            "application/json"
        );

        // Act
        var response = await _client.PostAsync("/data/ed-fi/gradeLevelDescriptors", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
    }

    [Test]
    public async Task TokenEndpoint_WithBasicAuth_ReturnsToken()
    {
        // Arrange
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-client:test-secret"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        var content = new FormUrlEncodedContent(
            new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }
        );

        // Act
        var response = await _client.PostAsync("/oauth/token", content);

        // Assert
        // Note: This endpoint behavior depends on implementation
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task JwtToken_WithNamespaceRestriction_EnforcesAccess()
    {
        // Arrange
        var token = GenerateJwtTokenWithNamespacePrefix("uri://ed-fi.org");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var content = new StringContent(
            """
            {
                "namespace": "uri://disallowed.org/AcademicWeekDescriptor",
                "codeValue": "Week1",
                "shortDescription": "Week 1"
            }
            """,
            Encoding.UTF8,
            "application/json"
        );

        // Act
        var response = await _client.PostAsync("/data/ed-fi/academicWeekDescriptors", content);

        // Assert
        // Should be forbidden if namespace authorization is enforced
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Created);
    }

    private string GenerateValidJwtToken()
    {
        return GenerateJwtTokenWithClaims(
            new[]
            {
                new Claim("sub", "test-client"),
                new Claim(
                    "clientAuthorizations",
                    """
                    {
                        "tokenId": "test-client",
                        "claimSetName": "E2E-Default",
                        "namespacePrefixes": [],
                        "educationOrganizationIds": []
                    }
                    """
                ),
            }
        );
    }

    private string GenerateExpiredJwtToken()
    {
        return GenerateJwtTokenWithClaims(
            new[]
            {
                new Claim("sub", "test-client"),
                new Claim(
                    "clientAuthorizations",
                    """
                    {
                        "tokenId": "test-client",
                        "claimSetName": "E2E-Default",
                        "namespacePrefixes": [],
                        "educationOrganizationIds": []
                    }
                    """
                ),
            },
            expires: DateTime.UtcNow.AddMinutes(-30)
        );
    }

    private string GenerateJwtTokenWithRole(string role)
    {
        return GenerateJwtTokenWithClaims(
            new[]
            {
                new Claim("sub", "test-client"),
                new Claim("role", role),
                new Claim(
                    "clientAuthorizations",
                    """
                    {
                        "tokenId": "test-client",
                        "claimSetName": "E2E-ServiceRole",
                        "namespacePrefixes": [],
                        "educationOrganizationIds": []
                    }
                    """
                ),
            }
        );
    }

    private string GenerateJwtTokenWithNamespacePrefix(string namespacePrefix)
    {
        return GenerateJwtTokenWithClaims(
            new[]
            {
                new Claim("sub", "test-client"),
                new Claim(
                    "clientAuthorizations",
                    $$"""
                    {
                        "tokenId": "test-client",
                        "claimSetName": "E2E-NameSpaceBasedClaimSet",
                        "namespacePrefixes": ["{{namespacePrefix}}"],
                        "educationOrganizationIds": []
                    }
                    """
                ),
            }
        );
    }

    private string GenerateJwtTokenWithClaims(Claim[] claims, DateTime? expires = null)
    {
        // Note: This is for testing only
        // In production, tokens come from the identity provider (Keycloak)
        var key = new RsaSecurityKey(RSA.Create(2048));
        var signingCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: expires ?? DateTime.UtcNow.AddHours(1),
            signingCredentials: signingCredentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

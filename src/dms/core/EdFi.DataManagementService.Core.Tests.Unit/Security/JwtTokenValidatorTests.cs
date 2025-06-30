// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

[TestFixture]
public class JwtTokenValidatorTests
{
    private HttpClient _httpClient = null!;
    private ILogger<JwtTokenValidator> _logger = null!;
    private IdentitySettings _settings = null!;
    private RSA _rsa = null!;
    private RsaSecurityKey _signingKey = null!;
    private readonly string _issuer = "https://test-issuer.com";
    private readonly string _audience = "test-audience";

    [SetUp]
    public void Setup()
    {
        _httpClient = new HttpClient();
        _logger = A.Fake<ILogger<JwtTokenValidator>>();

        _settings = new IdentitySettings
        {
            Authority = _issuer,
            Audience = _audience,
            RequireHttpsMetadata = true,
            RoleClaimType = "role",
            ClientRole = "client",
        };

        // Create RSA key for signing test tokens
        _rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(_rsa);
    }

    [TearDown]
    public void TearDown()
    {
        _rsa?.Dispose();
        _httpClient?.Dispose();
    }

    [Test]
    public async Task ValidateTokenAsync_ValidToken_ReturnsSuccessWithClaims()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim("name", "Test User"),
            new Claim("role", "client"),
            new Claim("aud", _audience),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        // Mock the HTTP client to return a valid OIDC configuration
        var mockHandler = new MockHttpMessageHandler(GetMockOidcConfiguration(), _rsa);
        var mockHttpClient = new HttpClient(mockHandler);
        var validator = new JwtTokenValidator(mockHttpClient, _logger);

        // Act
        var result = await validator.ValidateTokenAsync(token, _settings);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeEmpty();
        result.Claims.Should().Contain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "user123");
        result.Claims.Should().Contain(c => c.Type == "name" && c.Value == "Test User");
        result
            .Claims.Should()
            .Contain(c =>
                c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
                && c.Value == "client"
            );
        result.Claims.Should().Contain(c => c.Type == "aud" && c.Value == _audience);
    }

    [Test]
    public async Task ValidateTokenAsync_ExpiredToken_ReturnsFailure()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim("role", "client"),
            new Claim("aud", _audience),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(-10)); // Expired 10 minutes ago

        var mockHandler = new MockHttpMessageHandler(GetMockOidcConfiguration(), _rsa);
        var mockHttpClient = new HttpClient(mockHandler);
        var validator = new JwtTokenValidator(mockHttpClient, _logger);

        // Act
        var result = await validator.ValidateTokenAsync(token, _settings);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Token expired");
        result.Claims.Should().BeEmpty();
    }

    [Test]
    public async Task ValidateTokenAsync_InvalidAudience_ReturnsFailure()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim("role", "client"),
            new Claim("aud", "wrong-audience"), // Wrong audience
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var mockHandler = new MockHttpMessageHandler(GetMockOidcConfiguration(), _rsa);
        var mockHttpClient = new HttpClient(mockHandler);
        var validator = new JwtTokenValidator(mockHttpClient, _logger);

        // Act
        var result = await validator.ValidateTokenAsync(token, _settings);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid audience");
        result.Claims.Should().BeEmpty();
    }

    [Test]
    public async Task ValidateTokenAsync_InvalidIssuer_ReturnsFailure()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim("role", "client"),
            new Claim("aud", _audience),
        };

        var customSettings = new IdentitySettings
        {
            Authority = "https://wrong-issuer.com", // Wrong issuer
            Audience = _audience,
            RequireHttpsMetadata = true,
            RoleClaimType = "role",
            ClientRole = "client",
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var mockHandler = new MockHttpMessageHandler(GetMockOidcConfiguration(), _rsa);
        var mockHttpClient = new HttpClient(mockHandler);
        var validator = new JwtTokenValidator(mockHttpClient, _logger);

        // Act
        var result = await validator.ValidateTokenAsync(token, customSettings);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid issuer");
        result.Claims.Should().BeEmpty();
    }

    [Test]
    public async Task ValidateTokenAsync_InvalidSignature_ReturnsFailure()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim("role", "client"),
            new Claim("aud", _audience),
        };

        // Create token with different signing key
        var differentRsa = RSA.Create(2048);
        var differentKey = new RsaSecurityKey(differentRsa);
        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5), differentKey);
        differentRsa.Dispose();

        var mockHandler = new MockHttpMessageHandler(GetMockOidcConfiguration(), _rsa);
        var mockHttpClient = new HttpClient(mockHandler);
        var validator = new JwtTokenValidator(mockHttpClient, _logger);

        // Act
        var result = await validator.ValidateTokenAsync(token, _settings);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Token validation failed");
        result.Claims.Should().BeEmpty();
    }

    [Test]
    public async Task ValidateTokenAsync_MalformedToken_ReturnsFailure()
    {
        // Arrange
        var token = "not.a.valid.jwt.token";

        var mockHandler = new MockHttpMessageHandler(GetMockOidcConfiguration(), _rsa);
        var mockHttpClient = new HttpClient(mockHandler);
        var validator = new JwtTokenValidator(mockHttpClient, _logger);

        // Act
        var result = await validator.ValidateTokenAsync(token, _settings);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Token validation failed");
        result.Claims.Should().BeEmpty();
    }

    [Test]
    public async Task ValidateTokenAsync_NotJwtToken_ReturnsFailure()
    {
        // Arrange
        // Create a base64 encoded string that's not a JWT
        var notJwt = Convert.ToBase64String(Encoding.UTF8.GetBytes("This is not a JWT"));
        var token = $"{notJwt}.{notJwt}.{notJwt}";

        var mockHandler = new MockHttpMessageHandler(GetMockOidcConfiguration(), _rsa);
        var mockHttpClient = new HttpClient(mockHandler);
        var validator = new JwtTokenValidator(mockHttpClient, _logger);

        // Act
        var result = await validator.ValidateTokenAsync(token, _settings);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Token validation failed");
        result.Claims.Should().BeEmpty();
    }

    [Test]
    public async Task ValidateTokenAsync_ConfigurationManagerCaching_ReusesSameInstance()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("sub", "user123"),
            new Claim("role", "client"),
            new Claim("aud", _audience),
        };

        var token = CreateTestToken(claims, DateTime.UtcNow.AddMinutes(5));

        var mockHandler = new MockHttpMessageHandler(GetMockOidcConfiguration(), _rsa);
        var mockHttpClient = new HttpClient(mockHandler);
        var validator = new JwtTokenValidator(mockHttpClient, _logger);

        // Act - Validate twice with same settings
        var result1 = await validator.ValidateTokenAsync(token, _settings);
        var result2 = await validator.ValidateTokenAsync(token, _settings);

        // Assert
        result1.IsValid.Should().BeTrue();
        result2.IsValid.Should().BeTrue();
        // First validation fetches OIDC config and JWKS (2 calls)
        // Second validation should use cached values (0 additional calls)
        mockHandler
            .CallCount.Should()
            .Be(2, "Configuration and JWKS should only be fetched once due to caching");
    }

    private string CreateTestToken(Claim[] claims, DateTime expires, RsaSecurityKey? customKey = null)
    {
        var key = customKey ?? _signingKey;
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            claims: claims,
            expires: expires,
            signingCredentials: credentials
        );

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(token);
    }

    private string GetMockOidcConfiguration()
    {
        var config = new
        {
            issuer = _issuer,
            jwks_uri = $"{_issuer}/.well-known/jwks.json",
            authorization_endpoint = $"{_issuer}/authorize",
            token_endpoint = $"{_issuer}/token",
            id_token_signing_alg_values_supported = new[] { "RS256" },
            subject_types_supported = new[] { "public" },
            response_types_supported = new[] { "code", "token", "id_token" },
        };

        return System.Text.Json.JsonSerializer.Serialize(config);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _oidcConfig;
        private readonly RSA _rsa;
        public int CallCount { get; private set; }

        public MockHttpMessageHandler(string oidcConfig, RSA rsa)
        {
            _oidcConfig = oidcConfig;
            _rsa = rsa;
            CallCount = 0;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

            // If requesting OIDC configuration
            if (request.RequestUri?.AbsolutePath.EndsWith("/.well-known/openid-configuration") == true)
            {
                response.Content = new StringContent(_oidcConfig, Encoding.UTF8, "application/json");
            }
            // If requesting JWKS
            else if (request.RequestUri?.AbsolutePath.EndsWith("/jwks.json") == true)
            {
                var jwks = new
                {
                    keys = new[]
                    {
                        new
                        {
                            kty = "RSA",
                            use = "sig",
                            kid = "test-key-id",
                            alg = "RS256",
                            n = Base64UrlEncoder.Encode(_rsa.ExportParameters(false).Modulus),
                            e = Base64UrlEncoder.Encode(_rsa.ExportParameters(false).Exponent),
                        },
                    },
                };
                response.Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(jwks),
                    Encoding.UTF8,
                    "application/json"
                );
            }

            return await Task.FromResult(response);
        }
    }
}

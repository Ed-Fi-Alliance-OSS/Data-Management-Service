// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

[TestFixture]
public class JwtValidationServiceTests
{
    private IConfigurationManager<OpenIdConnectConfiguration> _configurationManager = null!;
    private IOptions<JwtAuthenticationOptions> _options = null!;
    private ILogger<JwtValidationService> _logger = null!;
    private JwtValidationService _service = null!;
    private JwtSecurityTokenHandler _tokenHandler = null!;
    private RsaSecurityKey _signingKey = null!;

    [SetUp]
    public void Setup()
    {
        _configurationManager = A.Fake<IConfigurationManager<OpenIdConnectConfiguration>>();
        _options = A.Fake<IOptions<JwtAuthenticationOptions>>();
        _logger = A.Fake<ILogger<JwtValidationService>>();
        _tokenHandler = new JwtSecurityTokenHandler();

        // Create RSA key for testing
        var rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(rsa);

        var jwtOptions = new JwtAuthenticationOptions
        {
            Audience = "ed-fi-ods-api",
            ClockSkewSeconds = 30,
            RoleClaimType = "role",
        };
        A.CallTo(() => _options.Value).Returns(jwtOptions);

        _service = new JwtValidationService(_configurationManager, _options, _logger);
    }

    [Test]
    public async Task ValidateAndExtractClientAuthorizationsAsync_ValidToken_ReturnsClientAuthorizations()
    {
        // Arrange
        var oidcConfig = new OpenIdConnectConfiguration
        {
            Issuer = "https://keycloak.example.com/realms/edfi",
        };
        oidcConfig.SigningKeys.Add(_signingKey);

        A.CallTo(() => _configurationManager.GetConfigurationAsync(A<CancellationToken>._))
            .Returns(Task.FromResult(oidcConfig));

        var claims = new[]
        {
            new Claim("jti", "test-token-id"),
            new Claim("scope", "edfi-admin"),
            new Claim("namespacePrefixes", "uri://ed-fi.org,uri://tpdm.ed-fi.org"),
            new Claim("educationOrganizationIds", "123,456"),
        };

        var token = CreateTestToken(claims, oidcConfig.Issuer, _options.Value.Audience);

        // Act
        var (principal, clientAuthorizations) = await _service.ValidateAndExtractClientAuthorizationsAsync(
            token,
            CancellationToken.None
        );

        // Assert
        principal.Should().NotBeNull();
        clientAuthorizations.Should().NotBeNull();
        clientAuthorizations!.TokenId.Should().Be("test-token-id");
        clientAuthorizations.ClaimSetName.Should().Be("edfi-admin");
        clientAuthorizations.NamespacePrefixes.Should().HaveCount(2);
        clientAuthorizations.NamespacePrefixes[0].Value.Should().Be("uri://ed-fi.org");
        clientAuthorizations.NamespacePrefixes[1].Value.Should().Be("uri://tpdm.ed-fi.org");
        clientAuthorizations.EducationOrganizationIds.Should().HaveCount(2);
        clientAuthorizations.EducationOrganizationIds[0].Value.Should().Be(123);
        clientAuthorizations.EducationOrganizationIds[1].Value.Should().Be(456);
    }

    [Test]
    public async Task ValidateAndExtractClientAuthorizationsAsync_ExpiredToken_ReturnsNull()
    {
        // Arrange
        var oidcConfig = new OpenIdConnectConfiguration
        {
            Issuer = "https://keycloak.example.com/realms/edfi",
        };
        oidcConfig.SigningKeys.Add(_signingKey);

        A.CallTo(() => _configurationManager.GetConfigurationAsync(A<CancellationToken>._))
            .Returns(Task.FromResult(oidcConfig));

        var claims = new[] { new Claim("jti", "test-token-id"), new Claim("scope", "edfi-admin") };

        var token = CreateTestToken(claims, oidcConfig.Issuer, _options.Value.Audience, expired: true);

        // Act
        var (principal, clientAuthorizations) = await _service.ValidateAndExtractClientAuthorizationsAsync(
            token,
            CancellationToken.None
        );

        // Assert
        principal.Should().BeNull();
        clientAuthorizations.Should().BeNull();
    }

    [Test]
    public async Task ValidateAndExtractClientAuthorizationsAsync_InvalidSignature_ReturnsNull()
    {
        // Arrange
        var oidcConfig = new OpenIdConnectConfiguration
        {
            Issuer = "https://keycloak.example.com/realms/edfi",
        };
        // Use different key for validation than signing
        var differentRsa = RSA.Create(2048);
        oidcConfig.SigningKeys.Add(new RsaSecurityKey(differentRsa));

        A.CallTo(() => _configurationManager.GetConfigurationAsync(A<CancellationToken>._))
            .Returns(Task.FromResult(oidcConfig));

        var claims = new[] { new Claim("jti", "test-token-id"), new Claim("scope", "edfi-admin") };

        var token = CreateTestToken(claims, oidcConfig.Issuer, _options.Value.Audience);

        // Act
        var (principal, clientAuthorizations) = await _service.ValidateAndExtractClientAuthorizationsAsync(
            token,
            CancellationToken.None
        );

        // Assert
        principal.Should().BeNull();
        clientAuthorizations.Should().BeNull();
    }

    [Test]
    public async Task ValidateAndExtractClientAuthorizationsAsync_MissingClaims_UsesDefaults()
    {
        // Arrange
        var oidcConfig = new OpenIdConnectConfiguration
        {
            Issuer = "https://keycloak.example.com/realms/edfi",
        };
        oidcConfig.SigningKeys.Add(_signingKey);

        A.CallTo(() => _configurationManager.GetConfigurationAsync(A<CancellationToken>._))
            .Returns(Task.FromResult(oidcConfig));

        // Token with minimal claims
        var claims = new Claim[] { };
        var token = CreateTestToken(claims, oidcConfig.Issuer, _options.Value.Audience);

        // Act
        var (principal, clientAuthorizations) = await _service.ValidateAndExtractClientAuthorizationsAsync(
            token,
            CancellationToken.None
        );

        // Assert
        principal.Should().NotBeNull();
        clientAuthorizations.Should().NotBeNull();
        clientAuthorizations!.TokenId.Should().NotBeNullOrEmpty(); // Should use hash code
        clientAuthorizations.ClaimSetName.Should().BeEmpty();
        clientAuthorizations.NamespacePrefixes.Should().BeEmpty();
        clientAuthorizations.EducationOrganizationIds.Should().BeEmpty();
    }

    private string CreateTestToken(Claim[] claims, string issuer, string audience, bool expired = false)
    {
        var signingCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);

        var now = DateTime.UtcNow;
        var expires = expired ? now.AddMinutes(-10) : now.AddMinutes(10);
        var notBefore = expired ? now.AddMinutes(-15) : now.AddMinutes(-5);

        var jwt = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: signingCredentials
        );

        return _tokenHandler.WriteToken(jwt);
    }
}

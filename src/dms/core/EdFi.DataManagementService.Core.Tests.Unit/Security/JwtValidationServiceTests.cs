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
[Parallelizable]
public class JwtValidationServiceTests
{
    internal static (
        JwtValidationService service,
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        IOptions<JwtAuthenticationOptions> options,
        ILogger<JwtValidationService> logger,
        JwtSecurityTokenHandler tokenHandler,
        RsaSecurityKey signingKey
    ) CreateService()
    {
        var configurationManager = A.Fake<IConfigurationManager<OpenIdConnectConfiguration>>();
        var options = A.Fake<IOptions<JwtAuthenticationOptions>>();
        var logger = A.Fake<ILogger<JwtValidationService>>();
        var tokenHandler = new JwtSecurityTokenHandler();

        // Create RSA key for testing
        var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);

        var jwtOptions = new JwtAuthenticationOptions
        {
            Audience = "ed-fi-ods-api",
            ClockSkewSeconds = 30,
            RoleClaimType = "role",
        };
        A.CallTo(() => options.Value).Returns(jwtOptions);

        var service = new JwtValidationService(configurationManager, options, logger);

        return (service, configurationManager, options, logger, tokenHandler, signingKey);
    }

    private static string CreateTestToken(
        Claim[] claims,
        string issuer,
        string audience,
        RsaSecurityKey signingKey,
        JwtSecurityTokenHandler tokenHandler,
        bool expired = false
    )
    {
        var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);

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

        return tokenHandler.WriteToken(jwt);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_With_All_Claims : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[]
            {
                new Claim("jti", "test-token-id"),
                new Claim("scope", "edfi-admin"),
                new Claim("namespacePrefixes", "uri://ed-fi.org,uri://tpdm.ed-fi.org"),
                new Claim("educationOrganizationIds", "123,456"),
            };

            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_a_valid_principal()
        {
            _principal.Should().NotBeNull();
        }

        [Test]
        public void It_returns_client_authorizations()
        {
            _clientAuthorizations.Should().NotBeNull();
        }

        [Test]
        public void It_extracts_the_token_id()
        {
            _clientAuthorizations!.TokenId.Should().Be("test-token-id");
        }

        [Test]
        public void It_extracts_the_claim_set_name()
        {
            _clientAuthorizations!.ClaimSetName.Should().Be("edfi-admin");
        }

        [Test]
        public void It_extracts_namespace_prefixes()
        {
            _clientAuthorizations!.NamespacePrefixes.Should().HaveCount(2);
            _clientAuthorizations.NamespacePrefixes[0].Value.Should().Be("uri://ed-fi.org");
            _clientAuthorizations.NamespacePrefixes[1].Value.Should().Be("uri://tpdm.ed-fi.org");
        }

        [Test]
        public void It_extracts_education_organization_ids()
        {
            _clientAuthorizations!.EducationOrganizationIds.Should().HaveCount(2);
            _clientAuthorizations.EducationOrganizationIds[0].Value.Should().Be(123);
            _clientAuthorizations.EducationOrganizationIds[1].Value.Should().Be(456);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Expired_Token : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[] { new Claim("jti", "test-token-id"), new Claim("scope", "edfi-admin") };

            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler,
                expired: true
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_null_principal()
        {
            _principal.Should().BeNull();
        }

        [Test]
        public void It_returns_null_client_authorizations()
        {
            _clientAuthorizations.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Token_With_Invalid_Signature : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            // Use different key for validation than signing
            var differentRsa = RSA.Create(2048);
            oidcConfig.SigningKeys.Add(new RsaSecurityKey(differentRsa));

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[] { new Claim("jti", "test-token-id"), new Claim("scope", "edfi-admin") };

            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_null_principal()
        {
            _principal.Should().BeNull();
        }

        [Test]
        public void It_returns_null_client_authorizations()
        {
            _clientAuthorizations.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_With_Missing_Claims : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            // Token with minimal claims
            var claims = Array.Empty<Claim>();
            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_a_valid_principal()
        {
            _principal.Should().NotBeNull();
        }

        [Test]
        public void It_returns_client_authorizations_with_defaults()
        {
            _clientAuthorizations.Should().NotBeNull();
        }

        [Test]
        public void It_generates_a_token_id_from_hash()
        {
            _clientAuthorizations!.TokenId.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void It_uses_empty_claim_set_name()
        {
            _clientAuthorizations!.ClaimSetName.Should().BeEmpty();
        }

        [Test]
        public void It_uses_empty_namespace_prefixes()
        {
            _clientAuthorizations!.NamespacePrefixes.Should().BeEmpty();
        }

        [Test]
        public void It_uses_empty_education_organization_ids()
        {
            _clientAuthorizations!.EducationOrganizationIds.Should().BeEmpty();
        }
    }
}

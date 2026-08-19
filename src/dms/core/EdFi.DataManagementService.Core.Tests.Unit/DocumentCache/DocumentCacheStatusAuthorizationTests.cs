// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
public class DocumentCacheStatusAuthorizationTests
{
    private const string RequiredRole = "dms-document-cache-operator";
    private const string RoleClaimType = "operator_role";
    private const string Token = "valid-token";
    private const string TestAudience = "ed-fi-ods-api";
    private const string TestIssuer = "https://keycloak.example.com/realms/edfi";

    private static (
        DocumentCacheStatusAuthorizationService Service,
        IJwtValidationService JwtValidationService
    ) CreateService(
        ClaimsPrincipal? principal,
        string requiredRole = RequiredRole,
        string roleClaimType = RoleClaimType,
        string clientRole = "legacy-service",
        ILogger<DocumentCacheStatusAuthorizationService>? logger = null
    )
    {
        var jwtValidationService = A.Fake<IJwtValidationService>();
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<(ClaimsPrincipal? Principal, ClientAuthorizations? ClientAuthorizations)>(
                    (principal, null)
                )
            );

        DocumentCacheOptions documentCacheOptions = new()
        {
            Status = new DocumentCacheStatusOptions { RequiredRole = requiredRole },
        };
        JwtAuthenticationOptions jwtAuthenticationOptions = new()
        {
            RoleClaimType = roleClaimType,
            ClientRole = clientRole,
        };

        DocumentCacheStatusAuthorizationService service = new(
            jwtValidationService,
            Options.Create(documentCacheOptions),
            Options.Create(jwtAuthenticationOptions),
            logger ?? NullLogger<DocumentCacheStatusAuthorizationService>.Instance
        );

        return (service, jwtValidationService);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static (
        DocumentCacheStatusAuthorizationService Service,
        JwtValidationService JwtValidationService,
        string Token
    ) CreateServiceWithRealJwtValidation(
        Claim[] claims,
        string requiredRole = RequiredRole,
        string roleClaimType = "role",
        string clientRole = "legacy-service",
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
            ClientRole = clientRole,
            ClockSkewSeconds = 0,
            ValidatedTokenCacheMaxEntries = 4,
        };
        JwtValidationService jwtValidationService = new(
            configurationManager,
            Options.Create(jwtAuthenticationOptions),
            NullLogger<JwtValidationService>.Instance
        );
        DocumentCacheOptions documentCacheOptions = new()
        {
            Status = new DocumentCacheStatusOptions { RequiredRole = requiredRole },
        };
        DocumentCacheStatusAuthorizationService service = new(
            jwtValidationService,
            Options.Create(documentCacheOptions),
            Options.Create(jwtAuthenticationOptions),
            NullLogger<DocumentCacheStatusAuthorizationService>.Instance
        );

        JwtSecurityTokenHandler tokenHandler = new();
        configureTokenHandler?.Invoke(tokenHandler);
        string token = CreateSignedToken(claims, signingKey, tokenHandler);

        return (service, jwtValidationService, token);
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
    public void It_defaults_role_claim_type_to_the_self_contained_issuer_claim_type()
    {
        new JwtAuthenticationOptions().RoleClaimType.Should().Be(ClaimTypes.Role);
    }

    [Test]
    public async Task It_returns_unauthorized_when_authorization_header_is_missing()
    {
        var (service, jwtValidationService) = CreateService(Principal());

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync(null);

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Unauthorized);
        result.Message.Should().Be("Authorization header is missing.");
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_unauthorized_when_authorization_header_is_malformed()
    {
        var (service, jwtValidationService) = CreateService(Principal());

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync("Basic token");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Unauthorized);
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_unauthorized_when_jwt_validation_rejects_the_token()
    {
        var (service, jwtValidationService) = CreateService(principal: null);

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Unauthorized);
        result.Message.Should().Be("Invalid token");
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    Token,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_authorizes_when_configured_role_claim_type_has_exact_required_role()
    {
        ClaimsPrincipal principal = Principal(new Claim(RoleClaimType, RequiredRole));
        var (service, _) = CreateService(principal);

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Authorized);
        result.Message.Should().BeNull();
    }

    [Test]
    public async Task It_authorizes_default_role_claim_type_from_real_signed_jwt()
    {
        var (service, _, token) = CreateServiceWithRealJwtValidation(
            [new Claim("role", RequiredRole)],
            roleClaimType: "role"
        );

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Authorized);
        result.Message.Should().BeNull();
    }

    [Test]
    public async Task It_authorizes_self_contained_issuer_role_claim_from_real_signed_jwt()
    {
        var (service, _, token) = CreateServiceWithRealJwtValidation(
            [new Claim(ClaimTypes.Role, RequiredRole)],
            roleClaimType: ClaimTypes.Role,
            configureTokenHandler: handler => handler.OutboundClaimTypeMap.Clear()
        );

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Authorized);
        result.Message.Should().BeNull();
    }

    [Test]
    public async Task It_authorizes_custom_role_claim_type_from_real_signed_jwt()
    {
        const string customRoleClaimType = "status_role";
        var (service, _, token) = CreateServiceWithRealJwtValidation(
            [new Claim(customRoleClaimType, RequiredRole)],
            roleClaimType: customRoleClaimType
        );

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Authorized);
    }

    [Test]
    public async Task It_authorizes_when_any_configured_role_claim_has_exact_required_role()
    {
        ClaimsPrincipal principal = Principal(
            new Claim(RoleClaimType, "other-role"),
            new Claim(RoleClaimType, RequiredRole)
        );
        var (service, _) = CreateService(principal);

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Authorized);
    }

    [Test]
    public async Task It_returns_forbidden_when_token_lacks_exact_required_role()
    {
        ClaimsPrincipal principal = Principal(new Claim(RoleClaimType, "other-role"));
        var (service, _) = CreateService(principal);

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Forbidden);
        result.Message.Should().Be("Insufficient permissions");
    }

    [Test]
    public async Task It_logs_the_configured_claim_type_when_the_required_role_is_missing()
    {
        var logger = new RecordingLogger<DocumentCacheStatusAuthorizationService>();
        ClaimsPrincipal principal = Principal(new Claim(RoleClaimType, "other-role"));
        var (service, _) = CreateService(principal, logger: logger);

        await service.AuthorizeAsync($"Bearer {Token}");

        LogRecord warning = logger
            .Records.Should()
            .ContainSingle(record => record.Level == LogLevel.Warning)
            .Which;
        warning.Properties["RoleClaimType"].Should().Be(RoleClaimType);
    }

    [Test]
    public async Task It_uses_the_configured_role_claim_type()
    {
        ClaimsPrincipal principal = Principal(new Claim("role", RequiredRole));
        var (service, _) = CreateService(principal);

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Forbidden);
    }

    [Test]
    public async Task It_rejects_real_signed_jwt_with_only_claim_types_role_when_role_claim_type_differs()
    {
        var (service, _, token) = CreateServiceWithRealJwtValidation(
            [new Claim(ClaimTypes.Role, RequiredRole)],
            roleClaimType: "status_role",
            configureTokenHandler: handler => handler.OutboundClaimTypeMap.Clear()
        );

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Forbidden);
    }

    [Test]
    public async Task It_does_not_fall_back_to_client_role_or_claim_types_role()
    {
        ClaimsPrincipal principal = Principal(
            new Claim(RoleClaimType, "legacy-service"),
            new Claim(ClaimTypes.Role, RequiredRole)
        );
        var (service, _) = CreateService(principal, clientRole: RequiredRole);

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Forbidden);
    }

    [TestCase("DMS-DOCUMENT-CACHE-OPERATOR")]
    [TestCase(" dms-document-cache-operator")]
    [TestCase("dms-document-cache-operator ")]
    [TestCase("dms-document-cache-operator other")]
    [TestCase("dms-document-cache-operator,other")]
    [TestCase("[\"dms-document-cache-operator\"]")]
    public async Task It_does_not_normalize_split_or_parse_role_claim_values(string claimValue)
    {
        ClaimsPrincipal principal = Principal(new Claim(RoleClaimType, claimValue));
        var (service, _) = CreateService(principal);

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(DocumentCacheStatusAuthorizationOutcome.Forbidden);
    }
}

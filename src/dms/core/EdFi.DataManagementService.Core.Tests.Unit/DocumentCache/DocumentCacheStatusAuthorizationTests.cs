// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
public class DocumentCacheStatusAuthorizationTests
{
    private const string RequiredRole = "dms-document-cache-operator";
    private const string RoleClaimType = "operator_role";
    private const string Token = "valid-token";

    private static (
        DocumentCacheStatusAuthorizationService Service,
        IJwtValidationService JwtValidationService
    ) CreateService(
        ClaimsPrincipal? principal,
        string requiredRole = RequiredRole,
        string roleClaimType = RoleClaimType,
        string clientRole = "legacy-service"
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
            NullLogger<DocumentCacheStatusAuthorizationService>.Instance
        );

        return (service, jwtValidationService);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

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
    public async Task It_uses_the_configured_role_claim_type()
    {
        ClaimsPrincipal principal = Principal(new Claim("role", RequiredRole));
        var (service, _) = CreateService(principal);

        DocumentCacheStatusAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

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

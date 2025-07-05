// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

[Binding]
public class JwtAuthenticationStepDefinitions(ScenarioContext _scenarioContext)
{
    private static readonly string _testIssuer = "http://localhost:8045/realms/edfi";
    private static readonly string _testAudience = "edfi-api";

    [Given(@"JWT authentication is enabled")]
    public void GivenJWTAuthenticationIsEnabled()
    {
        // This would typically be handled by test configuration
        // The test environment should have JWT enabled in appsettings
        _scenarioContext["JwtEnabled"] = true;
    }

    [Given(@"JWT authentication is disabled")]
    public void GivenJWTAuthenticationIsDisabled()
    {
        // This would typically be handled by test configuration
        _scenarioContext["JwtEnabled"] = false;
    }

    [Given(@"the API client is authorized with a valid JWT token")]
    public void GivenTheAPIClientIsAuthorizedWithAValidJWTToken()
    {
        var token = GenerateValidJwtToken();
        _scenarioContext["AuthorizationHeader"] = $"Bearer {token}";
    }

    [Given(@"the API client has an expired JWT token")]
    public void GivenTheAPIClientHasAnExpiredJWTToken()
    {
        var token = GenerateExpiredJwtToken();
        _scenarioContext["AuthorizationHeader"] = $"Bearer {token}";
    }

    [Given(@"the API client has a JWT token with invalid signature")]
    public void GivenTheAPIClientHasAJWTTokenWithInvalidSignature()
    {
        var validToken = GenerateValidJwtToken();
        // Manipulate the signature by changing the last character
        var invalidToken = validToken[..^1] + "X";
        _scenarioContext["AuthorizationHeader"] = $"Bearer {invalidToken}";
    }

    [Given(@"the API client has a JWT token with namespace prefix ""(.*)""")]
    public void GivenTheAPIClientHasAJWTTokenWithNamespacePrefix(string namespacePrefix)
    {
        var claims = new[]
        {
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
        };

        var token = GenerateJwtTokenWithClaims(claims);
        _scenarioContext["AuthorizationHeader"] = $"Bearer {token}";
    }

    [Given(@"the API client has a JWT token restricted to education organization (.*)")]
    public void GivenTheAPIClientHasAJWTTokenRestrictedToEducationOrganization(int edOrgId)
    {
        var claims = new[]
        {
            new Claim(
                "clientAuthorizations",
                $$"""
                {
                    "tokenId": "test-client",
                    "claimSetName": "E2E-EdOrgBasedClaimSet",
                    "namespacePrefixes": [],
                    "educationOrganizationIds": [{{edOrgId}}]
                }
                """
            ),
        };

        var token = GenerateJwtTokenWithClaims(claims);
        _scenarioContext["AuthorizationHeader"] = $"Bearer {token}";
    }

    [Given(@"the API client has a JWT token with ""(.*)"" role")]
    public void GivenTheAPIClientHasAJWTTokenWithRole(string role)
    {
        var claims = new[]
        {
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
        };

        var token = GenerateJwtTokenWithClaims(claims);
        _scenarioContext["AuthorizationHeader"] = $"Bearer {token}";
    }

    [Given(@"the API client has a JWT token without ""(.*)"" role")]
    public void GivenTheAPIClientHasAJWTTokenWithoutRole(string role) // role parameter is used by the Gherkin step for clarity
    {
        var claims = new[]
        {
            new Claim(
                "clientAuthorizations",
                """
                {
                    "tokenId": "test-client",
                    "claimSetName": "E2E-NoServiceRole",
                    "namespacePrefixes": [],
                    "educationOrganizationIds": []
                }
                """
            ),
        };

        var token = GenerateJwtTokenWithClaims(claims);
        _scenarioContext["AuthorizationHeader"] = $"Bearer {token}";
    }

    [Given(@"JWT authentication is enabled for client ""(.*)""")]
    public void GivenJWTAuthenticationIsEnabledForClient(string clientId)
    {
        // This would be handled by test configuration
        _scenarioContext["EnabledClients"] = new[] { clientId };
    }

    [Given(@"the API client ""(.*)"" has a valid JWT token")]
    public void GivenTheAPIClientHasAValidJWTToken(string clientId)
    {
        var claims = new[]
        {
            new Claim("sub", clientId),
            new Claim(
                "clientAuthorizations",
                $$"""
                {
                    "tokenId": "{{clientId}}",
                    "claimSetName": "E2E-Default",
                    "namespacePrefixes": [],
                    "educationOrganizationIds": []
                }
                """
            ),
        };

        var token = GenerateJwtTokenWithClaims(claims);
        _scenarioContext["AuthorizationHeader"] = $"Bearer {token}";
    }

    [Given(@"the API client ""(.*)"" uses traditional authentication")]
    public void GivenTheAPIClientUsesTraditionalAuthentication(string clientId)
    {
        // Set up for OAuth token endpoint usage
        _scenarioContext["UseTraditionalAuth"] = true;
        _scenarioContext["ClientId"] = clientId;
    }

    [Then(@"the response should contain a WWW-Authenticate header")]
    public void ThenTheResponseShouldContainAWWWAuthenticateHeader()
    {
        var response = _scenarioContext.Get<HttpResponseMessage>("Response");
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();
        response.Headers.WwwAuthenticate.Should().Contain(auth => auth.Scheme == "Bearer");
    }

    private static string GenerateValidJwtToken()
    {
        return GenerateJwtTokenWithClaims(
            [
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
            ]
        );
    }

    private static string GenerateExpiredJwtToken()
    {
        return GenerateJwtTokenWithClaims(
            [
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
            ],
            expires: DateTime.UtcNow.AddMinutes(-30) // Expired 30 minutes ago
        );
    }

    private static string GenerateJwtTokenWithClaims(Claim[] claims, DateTime? expires = null)
    {
        // This is for testing only - in production, tokens come from Keycloak
        var key = new RsaSecurityKey(RSA.Create(2048));
        var signingCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: _testIssuer,
            audience: _testAudience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: expires ?? DateTime.UtcNow.AddHours(1),
            signingCredentials: signingCredentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

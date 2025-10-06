// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Service for validating JWT tokens using OIDC metadata
/// </summary>
internal class JwtValidationService(
    IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
    IOptions<JwtAuthenticationOptions> options,
    ILogger<JwtValidationService> logger
) : IJwtValidationService
{
    private readonly JwtSecurityTokenHandler _tokenHandler = new();
    private readonly JwtAuthenticationOptions _options = options.Value;

    /// <summary>
    /// Validates a JWT token and extracts client authorization information.
    /// </summary>
    /// <param name="token">The JWT token to validate</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>A tuple containing the validated ClaimsPrincipal and extracted ClientAuthorizations, or (null, null) if validation fails</returns>
    public async Task<(
        ClaimsPrincipal? Principal,
        ClientAuthorizations? ClientAuthorizations
    )> ValidateAndExtractClientAuthorizationsAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            OpenIdConnectConfiguration oidcConfig = await configurationManager.GetConfigurationAsync(
                cancellationToken
            );

            TokenValidationParameters validationParameters = new()
            {
                // SECURITY CRITICAL: All must be true
                ValidateIssuer = true,
                ValidIssuer = oidcConfig.Issuer,

                ValidateAudience = true,
                ValidAudience = _options.Audience,

                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidcConfig.SigningKeys,

                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,

                ClockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds),

                NameClaimType = ClaimTypes.Name,
                RoleClaimType = _options.RoleClaimType,
            };

            ClaimsPrincipal principal = _tokenHandler.ValidateToken(
                token,
                validationParameters,
                out SecurityToken validatedToken
            );

            ClientAuthorizations clientAuthorizations = ExtractClientAuthorizations(
                principal,
                validatedToken
            );

            logger.LogDebug(
                "Token validation successful for TokenId: {TokenId}",
                clientAuthorizations.TokenId
            );

            return (principal, clientAuthorizations);
        }
        catch (SecurityTokenExpiredException ex)
        {
            logger.LogWarning(ex, "Token validation failed: Token expired");
            return (null, null);
        }
        catch (SecurityTokenException ex)
        {
            logger.LogWarning(ex, "Token validation failed");
            return (null, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during token validation");
            return (null, null);
        }
    }

    /// <summary>
    /// Extracts client authorization information from validated JWT claims.
    /// </summary>
    /// <param name="principal">The validated ClaimsPrincipal containing JWT claims</param>
    /// <param name="validatedToken">The validated SecurityToken object</param>
    /// <returns>ClientAuthorizations object containing tokenId, claimSetName, educationOrganizationIds, and namespacePrefixes</returns>
    private static ClientAuthorizations ExtractClientAuthorizations(
        ClaimsPrincipal principal,
        SecurityToken validatedToken
    )
    {
        var jwtToken = validatedToken as JwtSecurityToken;
        List<Claim> claims = principal.Claims.ToList();

        string claimSetName = claims.Find(c => c.Type == "scope")?.Value ?? string.Empty;
        string tokenId =
            claims.Find(c => c.Type == "jti")?.Value
            ?? jwtToken?.RawData?.GetHashCode().ToString()
            ?? string.Empty;
        string clientId = claims.Find(c => c.Type == "client_id")?.Value ?? string.Empty;

        string[] namespacePrefixes =
            claims
                .Find(c => c.Type == "namespacePrefixes")
                ?.Value.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];

        List<EducationOrganizationId> educationOrganizationIds =
            claims
                .Find(c => c.Type == "educationOrganizationIds")
                ?.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => new EducationOrganizationId(long.Parse(id)))
                .ToList() ?? [];

        return new ClientAuthorizations(
            TokenId: tokenId,
            ClientId: clientId,
            ClaimSetName: claimSetName,
            EducationOrganizationIds: educationOrganizationIds,
            NamespacePrefixes: namespacePrefixes.Select(np => new NamespacePrefix(np)).ToList()
        );
    }
}

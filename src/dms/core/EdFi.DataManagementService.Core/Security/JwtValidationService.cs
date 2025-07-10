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
internal class JwtValidationService : IJwtValidationService
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly JwtAuthenticationOptions _options;
    private readonly ILogger<JwtValidationService> _logger;

    public JwtValidationService(
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        IOptions<JwtAuthenticationOptions> options,
        ILogger<JwtValidationService> logger
    )
    {
        _configurationManager = configurationManager;
        _tokenHandler = new JwtSecurityTokenHandler();
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(
        ClaimsPrincipal? Principal,
        ClientAuthorizations? ClientAuthorizations
    )> ValidateAndExtractClientAuthorizationsAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var oidcConfig = await _configurationManager.GetConfigurationAsync(cancellationToken);

            var validationParameters = new TokenValidationParameters
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

            var principal = _tokenHandler.ValidateToken(
                token,
                validationParameters,
                out SecurityToken validatedToken
            );

            var clientAuthorizations = ExtractClientAuthorizations(principal, validatedToken);

            _logger.LogDebug(
                "Token validation successful for TokenId: {TokenId}",
                clientAuthorizations.TokenId
            );

            return (principal, clientAuthorizations);
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogWarning(ex, "Token validation failed: Token expired");
            return (null, null);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during token validation");
            return (null, null);
        }
    }

    private static ClientAuthorizations ExtractClientAuthorizations(
        ClaimsPrincipal principal,
        SecurityToken validatedToken
    )
    {
        var jwtToken = validatedToken as JwtSecurityToken;
        var claims = principal.Claims.ToList();

        // Port exact logic from ApiClientDetailsProvider
        var claimSetName = claims.Find(c => c.Type == "scope")?.Value ?? string.Empty;
        var tokenId =
            claims.Find(c => c.Type == "jti")?.Value
            ?? jwtToken?.RawData?.GetHashCode().ToString()
            ?? string.Empty;

        var namespacePrefixes =
            claims
                .Find(c => c.Type == "namespacePrefixes")
                ?.Value.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

        var educationOrganizationIds =
            claims
                .Find(c => c.Type == "educationOrganizationIds")
                ?.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => new EducationOrganizationId(long.Parse(id)))
                .ToList() ?? new List<EducationOrganizationId>();

        return new ClientAuthorizations(
            TokenId: tokenId,
            ClaimSetName: claimSetName,
            EducationOrganizationIds: educationOrganizationIds,
            NamespacePrefixes: namespacePrefixes.Select(np => new NamespacePrefix(np)).ToList()
        );
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// JWT token validator that validates tokens against an OpenID Connect identity provider
/// </summary>
internal class JwtTokenValidator(HttpClient httpClient, ILogger<JwtTokenValidator> logger)
    : IJwtTokenValidator
{
    private readonly SemaphoreSlim _configSemaphore = new(1, 1);
    private ConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;

    public async Task<JwtValidationResult> ValidateTokenAsync(string token, IdentitySettings settings)
    {
        try
        {
            var configurationManager = await GetConfigurationManagerAsync(settings);
            var config = await configurationManager.GetConfigurationAsync();

            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Authority,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys,
                ClockSkew = TimeSpan.FromMinutes(5),
                RoleClaimType = settings.RoleClaimType,
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            // Validate that the token is a JWT
            if (validatedToken is not JwtSecurityToken)
            {
                return new JwtValidationResult(false, [], "Token is not a valid JWT");
            }

            return new JwtValidationResult(true, principal.Claims.ToList());
        }
        catch (SecurityTokenExpiredException ex)
        {
            logger.LogDebug(ex, "JWT validation failed: Token expired");
            return new JwtValidationResult(false, [], "Token expired");
        }
        catch (SecurityTokenInvalidAudienceException ex)
        {
            logger.LogDebug(ex, "JWT validation failed: Invalid audience");
            return new JwtValidationResult(false, [], "Invalid audience");
        }
        catch (SecurityTokenInvalidIssuerException ex)
        {
            logger.LogDebug(ex, "JWT validation failed: Invalid issuer");
            return new JwtValidationResult(false, [], "Invalid issuer");
        }
        catch (SecurityTokenValidationException ex)
        {
            logger.LogDebug(ex, "JWT validation failed");
            return new JwtValidationResult(false, [], "Token validation failed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during JWT validation");
            return new JwtValidationResult(false, [], "Token validation failed");
        }
    }

    private async Task<ConfigurationManager<OpenIdConnectConfiguration>> GetConfigurationManagerAsync(
        IdentitySettings settings
    )
    {
        await _configSemaphore.WaitAsync();
        try
        {
            if (_configurationManager == null)
            {
                var metadataAddress = $"{settings.Authority}/.well-known/openid-configuration";
                _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    metadataAddress,
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(httpClient) { RequireHttps = settings.RequireHttpsMetadata }
                );
            }
            return _configurationManager;
        }
        finally
        {
            _configSemaphore.Release();
        }
    }
}

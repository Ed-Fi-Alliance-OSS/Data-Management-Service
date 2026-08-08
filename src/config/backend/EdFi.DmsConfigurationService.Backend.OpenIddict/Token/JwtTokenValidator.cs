// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    public static class JwtTokenValidator
    {
        /// <summary>
        /// Grace period accepted past a token's stated expiration to absorb clock drift.
        /// Anything that removes token state (such as the expired-token cleanup sweep) must
        /// keep rows until expiration plus this skew, or tokens the validator still accepts
        /// would fail their status lookup.
        /// </summary>
        public static readonly TimeSpan TokenValidationClockSkew = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Validates a JWT token using a dictionary of public keys, selecting the correct key by 'kid' in the JWT header.
        /// </summary>
        /// <param name="token">JWT token string</param>
        /// <param name="publicKeys">Dictionary of KeyId to SecurityKey</param>
        /// <param name="issuer">Expected issuer</param>
        /// <param name="audience">Expected audience</param>
        /// <param name="jwtToken">Out: parsed JwtSecurityToken</param>
        /// <param name="logger">Optional logger for diagnostic information</param>
        /// <returns>True if valid, false otherwise</returns>
        public static bool ValidateToken(
            string token,
            IDictionary<string, SecurityKey> publicKeys,
            string issuer,
            string audience,
            out JwtSecurityToken? jwtToken,
            ILogger? logger = null
        )
        {
            jwtToken = null;
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var parsedToken = tokenHandler.ReadJwtToken(token);
                var kid = parsedToken.Header.TryGetValue("kid", out var kidObj) ? kidObj?.ToString() : null;
                if (string.IsNullOrEmpty(kid) || !publicKeys.TryGetValue(kid, out var signingKey))
                {
                    // No kid or key not found
                    logger?.LogWarning(
                        "JWT validation failed: Missing or invalid 'kid' header. Kid: {Kid}, Available keys: {AvailableKeys}",
                        kid,
                        string.Join(", ", publicKeys.Keys)
                    );
                    return false;
                }
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TokenValidationClockSkew,
                };
                tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
                jwtToken = validatedToken as JwtSecurityToken;
                logger?.LogDebug(
                    "JWT token validated successfully. Issuer: {Issuer}, Audience: {Audience}, Subject: {Subject}",
                    jwtToken?.Issuer,
                    jwtToken?.Audiences?.FirstOrDefault(),
                    jwtToken?.Subject
                );
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "JWT token validation failed: {ErrorMessage}", ex.Message);
                return false;
            }
        }
    }
}

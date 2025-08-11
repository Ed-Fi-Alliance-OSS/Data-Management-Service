// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    public static class JwtTokenValidator
    {
        /// <summary>
        /// Validates a JWT token using a dictionary of public keys, selecting the correct key by 'kid' in the JWT header.
        /// </summary>
        /// <param name="token">JWT token string</param>
        /// <param name="publicKeys">Dictionary of KeyId to SecurityKey</param>
        /// <param name="issuer">Expected issuer</param>
        /// <param name="audience">Expected audience</param>
        /// <param name="jwtToken">Out: parsed JwtSecurityToken</param>
        /// <returns>True if valid, false otherwise</returns>
        public static bool ValidateToken(
            string token,
            IDictionary<string, SecurityKey> publicKeys,
            string issuer,
            string audience,
            out JwtSecurityToken? jwtToken)
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
                    ClockSkew = TimeSpan.FromMinutes(5)
                };
                tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
                jwtToken = validatedToken as JwtSecurityToken;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    public static class JwtTokenGenerator
    {
        public static JwtSettings GetJwtSettings(IConfiguration configuration)
        {
            var jwtSettings = new JwtSettings();
            if (configuration != null)
            {
                jwtSettings.Audience = configuration.GetValue<string>("IdentitySettings:Audience") ?? string.Empty;
                jwtSettings.Issuer = configuration.GetValue<string>("IdentitySettings:Authority") ?? string.Empty;
            }
            return jwtSettings;
        }


        public static string GenerateJwtToken(
            Guid tokenId,
            string clientId,
            string? displayName,
            string[]? permissions,
            string[]? roles,
            string scope,
            DateTimeOffset issuedAt,
            DateTimeOffset expiresAt,
            string issuer,
            string audience,
            SecurityKey signingKey,
            IConfiguration? configuration = null
        )
        {
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Jti, tokenId.ToString()),
                new Claim(JwtRegisteredClaimNames.Sub, clientId),
                new Claim(
                    JwtRegisteredClaimNames.Iat,
                    issuedAt.ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64
                ),
                new Claim(
                    JwtRegisteredClaimNames.Exp,
                    expiresAt.ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64
                ),
                new Claim("client_id", clientId),
                new Claim("typ", "Bearer"),
                new Claim("azp", clientId),
                new Claim("scope", scope),
                // Add audience and issuer as claims if present
                new Claim(JwtRegisteredClaimNames.Aud, audience),
                new Claim(JwtRegisteredClaimNames.Iss, issuer)
            };
            // Remove any null claims (if audience or issuer is null)
            claims = claims.Where(c => c != null).ToList();

            if (!string.IsNullOrEmpty(displayName))
            {
                claims.Add(new Claim("client_name", displayName));
            }

            if (roles != null && roles.Length > 0)
            {
                // Read the role claim type from configuration (JSON), fallback to default if not set
                var rolesClaim = configuration?.GetValue<string>("Authentication:RoleClaimAttribute")
                    ?? "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
            }
            if (permissions != null && permissions.Length > 0)
            {
                foreach (var permission in permissions)
                {
                    claims.Add(new Claim("permission", permission));
                }
            }
            foreach (var claim in claims)
            {
                claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
            }
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = expiresAt.DateTime,
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
                Issuer = issuer,
                Audience = audience,
            };

            // Prevent claim type mapping so that claim types like the role URI are not mapped to short names
            JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
            JwtSecurityTokenHandler.DefaultOutboundClaimTypeMap.Clear();

            // Create a JwtPayload directly to handle arrays properly
            var payload = new JwtPayload();

            // Add all standard claims
            foreach (var claim in claims)
            {
                payload.Add(claim.Type, claim.Value);
            }

            // Add roles as an actual array if present
            if (roles != null && roles.Length > 0)
            {
                var rolesClaim = configuration?.GetValue<string>("Authentication:RoleClaimAttribute")
                    ?? "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
                payload.Add(rolesClaim, roles);
            }

            // Create the JWT with header and payload
            var header = new JwtHeader(new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
            var token = new JwtSecurityToken(
                header,
                payload
            );

            var tokenHandler = new JwtSecurityTokenHandler();
            return tokenHandler.WriteToken(token);
        }
    }
}

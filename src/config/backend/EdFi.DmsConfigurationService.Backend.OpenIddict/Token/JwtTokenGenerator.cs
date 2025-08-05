// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static Dapper.SqlMapper;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    public static class JwtTokenGenerator
    {
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
            };

            if (!string.IsNullOrEmpty(displayName))
            {
                claims.Add(new Claim("client_name", displayName));
            }

            if (roles != null && roles.Length > 0)
            {
                // Read the role claim type from configuration (JSON), fallback to default if not set
                var rolesClaim = configuration?.GetValue<string>("Authentication:RoleClaimAttribute")
                    ?? "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
                foreach (var role in roles)
                {
                    var claim = new Claim(rolesClaim, role);
                    claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
                    claims.Add(claim);
                }
            }
            if (permissions != null && permissions.Length > 0)
            {
                foreach (var permission in permissions)
                {
                    claims.Add(new Claim("permission", permission));
                }
            }

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = expiresAt.DateTime,
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
                Issuer = issuer,
                Audience = audience,
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var securityToken = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(securityToken);
        }
    }
}

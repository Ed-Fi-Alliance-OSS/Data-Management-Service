// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EdFi.DataManagementService.Api.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DataManagementService.Api.Modules
{
    public class TokenModule : IModule
    {
        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/oauth/token", GenerateToken);
        }

        internal async Task GenerateToken(HttpContext httpContext, IOptions<AppSettings> options)
        {
            var authSettings = options.Value.Authentication;

            if (authSettings == null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await httpContext.Response.WriteAsync("Authentication settings are missing.");
            }
            else
            {
                var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authSettings.SigningKey));

                var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "dms_client"),
                    new Claim(ClaimTypes.Role, "SIS Vendor")
                };

                var token = new JwtSecurityToken(
                    authSettings.Issuer,
                    claims: claims,
                    expires: DateTime.Now.AddMinutes(60),
                    signingCredentials: credentials
                );

                var generatedToken = new JwtSecurityTokenHandler().WriteToken(token);
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                await httpContext.Response.WriteAsync(generatedToken);
            }
        }
    }
}

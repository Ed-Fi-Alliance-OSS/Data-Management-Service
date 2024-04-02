// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EdFi.DataManagementService.Api.Configuration;
using EdFi.DataManagementService.Api.Infrastructure.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DataManagementService.Api.Modules;

public class TokenModule : IModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/oauth/token", GenerateToken);
    }

    internal async Task GenerateToken(
        HttpContext httpContext,
        IOptions<AppSettings> options,
        AuthenticationRequest authenticationRequest
    )
    {
        if (
            authenticationRequest == null
            || (
                string.IsNullOrEmpty(authenticationRequest.Key)
                || string.IsNullOrEmpty(authenticationRequest.Secret)
            )
        )
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync("Please provide valid key and secret.");
            return;
        }

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
            var tokenDetails = new TokenResponse { Token = generatedToken, ExpiresIn = "60 minutes" };
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            await httpContext.Response.WriteAsSerializedJsonAsync(tokenDetails);
        }
    }
}

public class AuthenticationRequest
{
    public required string Key { get; set; }
    public required string Secret { get; set; }
}

public class TokenResponse
{
    public string? Token { get; set; }
    public string? ExpiresIn { get; set; }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Backend.OAuthService;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/oauth/token", GenerateToken);
    }

    internal static async Task GenerateToken(HttpContext httpContext, IOptions<AppSettings> appSettings, OAuthManager oAuthManager, ILogger<TokenEndpointModule> logger)
    {
        try
        {
            logger.LogInformation("Requesting access token from OAuth Manager.");
            var response = await oAuthManager.GetAccessTokenAsync(httpContext, appSettings.Value.AuthenticationService);

            logger.LogInformation("OAuth Manager access token request successful.");
            await response.Content.CopyToAsync(httpContext.Response.Body);
        }
        catch (OAuthIdentityException ex)
        {
            httpContext.Response.StatusCode = (int?)ex.StatusCode ?? (int)HttpStatusCode.BadGateway;
            await httpContext.Response.WriteAsync($"Error Getting Access Token Async: {ex.Message}");
        }
    }
}

public record TokenResponse(string access_token, int expires_in, string token_type);

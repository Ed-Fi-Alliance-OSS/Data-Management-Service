// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/oauth/token", GenerateToken);
    }

    internal static async Task GenerateToken(HttpContext httpContext, IOptions<AppSettings> appSettings, IOAuthManager oAuthManager, ILogger<TokenEndpointModule> logger, IHttpClientFactory httpClientFactory)
    {
        // Create client for sending upstream request.
        logger.LogInformation("Handling OAuth Token request.");
        var client = httpClientFactory.CreateClient();

        // Extract the Authorization Headers.
        httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader);
        try
        {
            logger.LogInformation("Requesting access token from OAuth Manager.");
            var response = await oAuthManager.GetAccessTokenAsync(client, authHeader.ToString(), appSettings.Value.AuthenticationService);

            logger.LogInformation("OAuth Manager access token request successful.");
            await response.Content.CopyToAsync(httpContext.Response.Body);
        }
        catch (OAuthIdentityException ex)
        {
            logger.LogError(ex, "Error from OAuthManager service");
            httpContext.Response.StatusCode = (int?)ex.StatusCode ?? (int)HttpStatusCode.BadGateway;
            await httpContext.Response.WriteAsync($"Error Getting Access Token Async: {ex.Message}");
        }
    }
}

public record TokenResponse(string access_token, int expires_in, string token_type);

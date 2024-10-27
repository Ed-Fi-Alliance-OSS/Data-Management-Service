// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/oauth/token", GenerateToken);
    }

    internal static async Task GenerateToken(
        HttpContext httpContext,
        IOptions<AppSettings> appSettings,
        IOAuthManager oAuthManager,
        ILogger<TokenEndpointModule> logger,
        IHttpClientFactory httpClientFactory
    )
    {
        var traceId = AspNetCoreFrontend.ExtractTraceIdFrom(httpContext.Request, appSettings);
        logger.LogInformation(
            "Received token request for proxying to identity provider - {TraceId}",
            traceId
        );

        var client = new HttpClientWrapper(httpClientFactory.CreateClient());

        httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader);

        var response = await oAuthManager.GetAccessTokenAsync(
            client,
            authHeader.ToString(),
            appSettings.Value.AuthenticationService,
            traceId
        );

        if (logger.IsDebugEnabled())
        {
            logger.LogDebug("Proxied token response code {Code} - {TraceId}", response.StatusCode, traceId);
        }
        httpContext.Response.StatusCode = (int)response.StatusCode;
        await response.Content.CopyToAsync(httpContext.Response.Body);
    }
}

public record TokenResponse(string access_token, int expires_in, string token_type);

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    private DiscoveryService? _discoveryService;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost("/oauth/token", HandleFormData)
            .Accepts<TokenRequest>(contentType: "application/x-www-form-urlencoded")
            .DisableAntiforgery();
        endpoints
            .MapPost("/oauth/token", HandleJsonData)
            .Accepts<TokenRequest>(contentType: "application/json")
            .DisableAntiforgery();
    }

    internal async Task HandleFormData(
        HttpContext httpContext,
        [FromForm] TokenRequest tokenRequest,
        IOptions<AppSettings> appSettings,
        IOAuthManager oAuthManager,
        ILogger<TokenEndpointModule> logger,
        IHttpClientFactory httpClientFactory
    )
    {
        await GenerateToken(httpContext, tokenRequest, appSettings, oAuthManager, logger, httpClientFactory);
    }

    internal async Task HandleJsonData(
        HttpContext httpContext,
        TokenRequest tokenRequest,
        IOptions<AppSettings> appSettings,
        IOAuthManager oAuthManager,
        ILogger<TokenEndpointModule> logger,
        IHttpClientFactory httpClientFactory
    )
    {
        await GenerateToken(httpContext, tokenRequest, appSettings, oAuthManager, logger, httpClientFactory);
    }

    private async Task GenerateToken(
        HttpContext httpContext,
        TokenRequest tokenRequest,
        IOptions<AppSettings> appSettings,
        IOAuthManager oAuthManager,
        ILogger<TokenEndpointModule> logger,
        IHttpClientFactory httpClientFactory
    )
    {
        var traceId = AspNetCoreFrontend.ExtractTraceIdFrom(httpContext.Request, appSettings);
        logger.LogInformation(
            "Received token request for forwarding to identity provider - {TraceId}",
            traceId.Value
        );

        // Get token_endpoint from Discovery URL
        _discoveryService ??= httpContext.RequestServices.GetRequiredService<DiscoveryService>();
        string discoveryUrl = appSettings.Value.AuthenticationService;
        string tokenEndpoint = await _discoveryService.GetTokenEndpointAsync(discoveryUrl);

        var client = new HttpClientWrapper(httpClientFactory.CreateClient());

        httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader);

        var response = await oAuthManager.GetAccessTokenAsync(
            client,
            tokenRequest.grant_type,
            authHeader.ToString(),
            tokenEndpoint,
            traceId
        );

        if (logger.IsDebugEnabled())
        {
            logger.LogDebug(
                "Upstream token response code {Code} - {TraceId}",
                response.StatusCode,
                traceId.Value
            );
        }
        httpContext.Response.StatusCode = (int)response.StatusCode;
        await response.Content.CopyToAsync(httpContext.Response.Body);
    }
}

public class TokenRequest
{
    public string grant_type { get; set; } = "";
}

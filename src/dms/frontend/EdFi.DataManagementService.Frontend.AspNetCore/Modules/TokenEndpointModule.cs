// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Build the fixed route pattern prefix for root-level endpoints (may be empty)
        var appSettings = endpoints.ServiceProvider.GetRequiredService<IOptions<AppSettings>>();
        string routePattern = FixedRoutePattern.Build(
            appSettings.Value.GetRouteQualifierSegmentsArray(),
            appSettings.Value.MultiTenancy
        );

        endpoints
            .MapPost($"{routePattern}/oauth/token", HandleFormData)
            .Accepts<TokenRequest>(contentType: "application/x-www-form-urlencoded")
            .DisableAntiforgery();
        endpoints
            .MapPost($"{routePattern}/oauth/token", HandleJsonData)
            .Accepts<TokenRequest>(contentType: "application/json")
            .DisableAntiforgery();
    }

    internal static async Task HandleFormData(
        HttpContext httpContext,
        IOptions<AppSettings> appSettings,
        ITenantValidator tenantValidator,
        IOAuthManager oAuthManager,
        ILogger<TokenEndpointModule> logger,
        IHttpClientFactory httpClientFactory
    )
    {
        // Manually read form data to handle empty form bodies in .NET 10
        // (Minimal API [FromForm] binding returns 400 with empty body before handler is invoked)
        TokenRequest tokenRequest = new();
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync();
            tokenRequest = new TokenRequest { grant_type = form["grant_type"].ToString() };
        }

        await GenerateToken(
            httpContext,
            tokenRequest,
            appSettings,
            tenantValidator,
            oAuthManager,
            logger,
            httpClientFactory
        );
    }

    internal static async Task HandleJsonData(
        HttpContext httpContext,
        TokenRequest tokenRequest,
        IOptions<AppSettings> appSettings,
        ITenantValidator tenantValidator,
        IOAuthManager oAuthManager,
        ILogger<TokenEndpointModule> logger,
        IHttpClientFactory httpClientFactory
    )
    {
        await GenerateToken(
            httpContext,
            tokenRequest,
            appSettings,
            tenantValidator,
            oAuthManager,
            logger,
            httpClientFactory
        );
    }

    private static async Task GenerateToken(
        HttpContext httpContext,
        TokenRequest tokenRequest,
        IOptions<AppSettings> appSettings,
        ITenantValidator tenantValidator,
        IOAuthManager oAuthManager,
        ILogger<TokenEndpointModule> logger,
        IHttpClientFactory httpClientFactory
    )
    {
        if (appSettings.Value.MultiTenancy)
        {
            string? tenant = ExtractTenantFromRoute(httpContext);
            if (tenant is not null && !await tenantValidator.ValidateTenantAsync(tenant))
            {
                httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await httpContext.Response.WriteAsSerializedJsonAsync(
                    new
                    {
                        detail = "The specified resource could not be found.",
                        type = "urn:ed-fi:api:not-found",
                        title = "Not Found",
                        status = 404,
                    }
                );
                return;
            }
        }

        var traceId = AspNetCoreFrontend.ExtractTraceIdFrom(httpContext.Request, appSettings);
        logger.LogInformation(
            "Received token request for forwarding to identity provider - {TraceId}",
            traceId.Value
        );

        var client = new HttpClientWrapper(httpClientFactory.CreateClient());

        httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader);

        var response = await oAuthManager.GetAccessTokenAsync(
            client,
            tokenRequest.grant_type,
            authHeader.ToString(),
            appSettings.Value.AuthenticationService,
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
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await response.Content.CopyToAsync(httpContext.Response.Body);
    }

    private static string? ExtractTenantFromRoute(HttpContext httpContext)
    {
        if (
            httpContext.Request.RouteValues.TryGetValue("tenant", out object? value)
            && value is string tenant
            && !string.IsNullOrWhiteSpace(tenant)
        )
        {
            return tenant;
        }

        return null;
    }
}

public class TokenRequest
{
    public string grant_type { get; set; } = "";
}


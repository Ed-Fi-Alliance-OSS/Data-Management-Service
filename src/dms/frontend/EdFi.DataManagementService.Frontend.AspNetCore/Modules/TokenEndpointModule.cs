// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var appSettings = endpoints.ServiceProvider.GetRequiredService<IOptions<AppSettings>>();
        bool multiTenancy = appSettings.Value.MultiTenancy;
        string routePattern = FixedRoutePattern.Build(
            appSettings.Value.GetRouteQualifierSegmentsArray(),
            multiTenancy
        );

        MapTokenEndpoints(endpoints, string.Empty);

        if (!string.IsNullOrEmpty(routePattern))
        {
            MapTokenEndpoints(endpoints, routePattern);
        }
    }

    private static void MapTokenEndpoints(IEndpointRouteBuilder endpoints, string routePattern)
    {
        endpoints
            .MapPost($"{routePattern}/oauth/token", HandleFormData)
            .Accepts<TokenRequest>(contentType: "application/x-www-form-urlencoded")
            .WithMetadata(TokenEndpointMetadata.Instance)
            .DisableAntiforgery();
        endpoints
            .MapPost($"{routePattern}/oauth/token", HandleJsonData)
            .Accepts<TokenRequest>(contentType: "application/json")
            .WithMetadata(TokenEndpointMetadata.Instance)
            .DisableAntiforgery();
        endpoints.MapMethodNotAllowed($"{routePattern}/oauth/token", HttpMethods.Post);
    }

    internal static async Task HandleFormData(
        HttpContext httpContext,
        IOptions<AppSettings> appSettings,
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

            // RFC 6749 section 3.2: request parameters must not be included more than once.
            if (Array.Exists(_singleValuedFormParameters, name => form[name].Count > 1))
            {
                await WriteBadRequest(
                    httpContext,
                    appSettings,
                    OAuthErrorResponse.DuplicateParameterDescription
                );
                return;
            }

            // An absent field reads as empty here. TokenRequestCredentials treats an empty field as
            // omitted, as RFC 6749 section 3.2 requires, so the two need no telling apart.
            tokenRequest = new TokenRequest
            {
                grant_type = form["grant_type"].ToString(),
                client_id = form["client_id"].ToString(),
                client_secret = form["client_secret"].ToString(),
            };
        }

        await GenerateToken(httpContext, tokenRequest, appSettings, oAuthManager, logger, httpClientFactory);
    }

    private static readonly string[] _singleValuedFormParameters =
    [
        "grant_type",
        "client_id",
        "client_secret",
    ];

    private static async Task WriteBadRequest(
        HttpContext httpContext,
        IOptions<AppSettings> appSettings,
        string detail
    )
    {
        var traceId = AspNetCoreFrontend.ExtractTraceIdFrom(httpContext.Request, appSettings);
        using var response = OAuthErrorResponse.Create(
            System.Net.HttpStatusCode.BadRequest,
            "invalid_request",
            detail,
            traceId
        );
        await TokenErrorResponseWriter.WriteAsync(httpContext, response);
    }

    private const string MalformedJsonDescription =
        "The token request body must be a valid JSON object with string-valued fields";

    internal static async Task HandleJsonData(
        HttpContext httpContext,
        IOptions<AppSettings> appSettings,
        IOptions<JsonOptions> jsonOptions,
        IOAuthManager oAuthManager,
        ILogger<TokenEndpointModule> logger,
        IHttpClientFactory httpClientFactory
    )
    {
        // Manually read the JSON body so the framework's binder never sees it: its parse errors
        // quote the body, and the framework logs them (or throws them into the error log).
        TokenRequest? tokenRequest = null;
        try
        {
            tokenRequest = await httpContext.Request.ReadFromJsonAsync<TokenRequest>(
                jsonOptions.Value.SerializerOptions
            );
        }
        catch (JsonException)
        {
            // Deliberately not logged or passed on: the exception message can contain body content.
        }

        if (tokenRequest is null)
        {
            var traceId = AspNetCoreFrontend.ExtractTraceIdFrom(httpContext.Request, appSettings);
            logger.LogDebug("Token request body is not valid JSON - {TraceId}", traceId.Value);
            using var response = OAuthErrorResponse.Create(
                System.Net.HttpStatusCode.BadRequest,
                "invalid_request",
                MalformedJsonDescription,
                traceId
            );
            await TokenErrorResponseWriter.WriteAsync(httpContext, response);
            return;
        }

        await GenerateToken(httpContext, tokenRequest, appSettings, oAuthManager, logger, httpClientFactory);
    }

    private static async Task GenerateToken(
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

        using HttpClient upstreamClient = httpClientFactory.CreateClient();
        // RemoteIpAddress has already passed through trusted forwarded-header processing. Replace,
        // never append or relay, caller-supplied forwarding values so CMS can isolate source budgets.
        // CMS must explicitly trust this DMS proxy via its ReverseProxy settings. System-token refresh
        // uses a separate client and sends no forwarded source, retaining the DMS transport budget.
        // Unknown public sources share a fixed address instead of spending the private refresh budget.
        // Clear factory-configured defaults too.
        upstreamClient.DefaultRequestHeaders.Remove("X-Forwarded-For");
        IPAddress source = httpContext.Connection.RemoteIpAddress ?? IPAddress.Any;
        upstreamClient.DefaultRequestHeaders.Add(
            "X-Forwarded-For",
            (source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source).ToString()
        );
        var client = new HttpClientWrapper(upstreamClient);

        // null when the request carried no Authorization header at all, which the manager must
        // tell apart from a header that is present but empty.
        string? authHeader = httpContext.Request.Headers.TryGetValue(
            "Authorization",
            out var authHeaderValues
        )
            ? authHeaderValues.ToString()
            : null;

        using var response = await oAuthManager.GetAccessTokenAsync(
            client,
            tokenRequest.grant_type ?? string.Empty,
            authHeader,
            new TokenRequestCredentials(tokenRequest.client_id, tokenRequest.client_secret),
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
        await TokenErrorResponseWriter.WriteAsync(httpContext, response);
    }
}

public class TokenRequest
{
    /// <summary>
    /// Nullable because JSON binding assigns a JSON <c>null</c> whatever the declared type, and a
    /// non-nullable declaration let that <c>null</c> reach the manager unchecked.
    /// </summary>
    public string? grant_type { get; set; } = "";

    /// <summary>
    /// <c>null</c> or empty when the body did not carry a value, which the manager treats alike.
    /// </summary>
    public string? client_id { get; set; }

    /// <summary>
    /// <c>null</c> or empty when the body did not carry a value, which the manager treats alike.
    /// Never logged.
    /// </summary>
    public string? client_secret { get; set; }
}

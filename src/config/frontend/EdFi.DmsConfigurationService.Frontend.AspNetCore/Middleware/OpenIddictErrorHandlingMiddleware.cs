// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

/// <summary>
/// Middleware that handles OpenIddict-specific errors and converts them to proper OAuth 2.0/OpenID Connect error responses
/// </summary>
public class OpenIddictErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<OpenIddictErrorHandlingMiddleware> _logger;

    public OpenIddictErrorHandlingMiddleware(RequestDelegate next, ILogger<OpenIddictErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (IsOAuthEndpoint(context.Request.Path))
        {
            await HandleOAuthErrorAsync(context, ex);
        }
    }

    private static bool IsOAuthEndpoint(PathString path)
    {
        return path.StartsWithSegments("/connect/token") ||
               path.StartsWithSegments("/connect/authorize") ||
               path.StartsWithSegments("/connect/introspect") ||
               path.StartsWithSegments("/connect/revoke") ||
               path.StartsWithSegments("/connect/userinfo") ||
               path.StartsWithSegments("/.well-known/openid_configuration") ||
               path.StartsWithSegments("/.well-known/jwks");
    }

    private async Task HandleOAuthErrorAsync(HttpContext context, Exception exception)
    {
        _logger.LogError(exception, "An error occurred while processing OAuth/OpenID Connect request");

        context.Response.StatusCode = 400;
        context.Response.ContentType = "application/json";

        var errorResponse = CreateErrorResponse(exception);
        var json = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        await context.Response.WriteAsync(json);
    }

    private static object CreateErrorResponse(Exception exception)
    {
        return exception switch
        {
            ArgumentException when exception.Message.Contains("client") => new
            {
                Error = OpenIddictConstants.Errors.InvalidClient,
                ErrorDescription = "The client credentials are invalid."
            },
            ArgumentException when exception.Message.Contains("grant") => new
            {
                Error = OpenIddictConstants.Errors.UnsupportedGrantType,
                ErrorDescription = "The authorization grant type is not supported."
            },
            ArgumentException when exception.Message.Contains("scope") => new
            {
                Error = OpenIddictConstants.Errors.InvalidScope,
                ErrorDescription = "The requested scope is invalid, unknown, or malformed."
            },
            UnauthorizedAccessException => new
            {
                Error = OpenIddictConstants.Errors.AccessDenied,
                ErrorDescription = "The resource owner or authorization server denied the request."
            },
            InvalidOperationException when exception.Message.Contains("token") => new
            {
                Error = OpenIddictConstants.Errors.InvalidToken,
                ErrorDescription = "The access token provided is expired, revoked, malformed, or invalid."
            },
            _ => new
            {
                Error = OpenIddictConstants.Errors.ServerError,
                ErrorDescription = "The authorization server encountered an unexpected condition that prevented it from fulfilling the request."
            }
        };
    }
}

/// <summary>
/// Extension methods for registering the OpenIddict error handling middleware
/// </summary>
public static class OpenIddictErrorHandlingMiddlewareExtensions
{
    /// <summary>
    /// Adds the OpenIddict error handling middleware to the request pipeline
    /// </summary>
    /// <param name="builder">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseOpenIddictErrorHandling(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<OpenIddictErrorHandlingMiddleware>();
    }
}

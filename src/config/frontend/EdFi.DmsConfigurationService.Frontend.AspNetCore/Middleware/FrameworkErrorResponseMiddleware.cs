// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

/// <summary>
/// Shapes framework-generated, bodiless error responses (401/403/404/405/415) into the Ed-Fi error
/// contract, independent of route and authentication scheme (DMS-1218 INV-25…29).
///
/// Registered immediately after <c>UseRouting</c> and before CORS/authentication/authorization, so it
/// wraps the authentication/authorization short-circuits and the endpoint terminal. It shapes a
/// response only when the response has not started, has no content type, and has no (or zero) content
/// length — so already-structured error bodies (e.g. a module's <c>FailureResults.NotFound</c>) and all
/// success/204 responses are left untouched. It never inspects the route, never re-executes routing or
/// endpoints, and never clears headers, so <c>WWW-Authenticate</c>, <c>Allow</c>, and CORS headers are
/// preserved.
/// </summary>
public class FrameworkErrorResponseMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        if (context.Response.HasStarted)
        {
            return;
        }

        if (!string.IsNullOrEmpty(context.Response.ContentType))
        {
            return;
        }

        if (context.Response.ContentLength is not null and not 0)
        {
            return;
        }

        JsonNode? failure = context.Response.StatusCode switch
        {
            StatusCodes.Status401Unauthorized => FailureResponse.ForUnauthorized(
                "Authentication Failed",
                "Authentication is required to access this resource.",
                context.TraceIdentifier
            ),
            StatusCodes.Status403Forbidden => FailureResponse.ForForbidden(
                "Authorization Failed",
                "The authenticated client is not authorized to access this resource.",
                context.TraceIdentifier
            ),
            StatusCodes.Status404NotFound => FailureResponse.ForNotFound(
                "The requested resource could not be found.",
                context.TraceIdentifier
            ),
            StatusCodes.Status405MethodNotAllowed => FailureResponse.ForMethodNotAllowed(
                context.TraceIdentifier
            ),
            StatusCodes.Status415UnsupportedMediaType => FailureResponse.ForUnsupportedMediaType(
                context.TraceIdentifier
            ),
            _ => null,
        };

        if (failure is not null)
        {
            await FailureResponseWriter.WriteAsync(context, failure, context.RequestAborted);
        }
    }
}

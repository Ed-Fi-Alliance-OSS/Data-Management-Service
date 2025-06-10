// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

public class RequestLoggingMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task Invoke(HttpContext context, ILogger<RequestLoggingMiddleware> logger)
    {
        if (context.Request.Path.StartsWithSegments(new PathString("/.well-known")))
        {
            // Requests to the OpenId Connect ".well-known" endpoint are too chatty for informational logging, but could be useful in debug logging.
            logger.LogDebug(
                JsonSerializer.Serialize(
                    new { path = context.Request.Path.Value, traceId = context.TraceIdentifier }
                )
            );
        }
        else
        {
            logger.LogInformation(
                JsonSerializer.Serialize(
                    new { path = context.Request.Path.Value, traceId = context.TraceIdentifier }
                )
            );
        }
        await _next(context);
    }
}

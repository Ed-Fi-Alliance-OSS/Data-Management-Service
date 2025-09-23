// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class LoggingMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context, ILogger<LoggingMiddleware> logger)
    {
        var stopwatch = Stopwatch.StartNew();

        logger.LogInformation(
            "Request started: {Method} {Path} - TraceId: {TraceId}",
            context.Request.Method,
            context.Request.Path.Value,
            context.TraceIdentifier
        );

        try
        {
            await next(context);

            stopwatch.Stop();
            logger.LogInformation(
                "Request completed: {Method} {Path} - Status: {StatusCode} - Duration: {Duration}ms - TraceId: {TraceId}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                context.TraceIdentifier
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Request failed: {Method} {Path} - Duration: {Duration}ms - TraceId: {TraceId}",
                context.Request.Method,
                context.Request.Path.Value,
                stopwatch.ElapsedMilliseconds,
                context.TraceIdentifier
            );

            var response = context.Response;
            if (!response.HasStarted)
            {
                response.ContentType = "application/json";
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        new
                        {
                            message = "The server encountered an unexpected condition that prevented it from fulfilling the request.",
                            traceId = context.TraceIdentifier,
                        }
                    )
                );
            }
        }
    }
}

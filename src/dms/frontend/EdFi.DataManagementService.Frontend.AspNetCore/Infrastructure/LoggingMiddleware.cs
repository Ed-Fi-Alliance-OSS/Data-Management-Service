// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class LoggingMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context, ILogger<LoggingMiddleware> logger)
    {
        var stopwatch = Stopwatch.StartNew();
        bool logInformation = logger.IsEnabled(LogLevel.Information);
        string sanitizedMethod = logInformation
            ? LoggingSanitizer.SanitizeForLogging(context.Request.Method)
            : string.Empty;
        string sanitizedPath = logInformation
            ? LoggingSanitizer.SanitizeForLogging(context.Request.Path.Value)
            : string.Empty;

        if (logInformation)
        {
            logger.LogInformation(
                "Request started: {Method} {Path} - TraceId: {TraceId}",
                sanitizedMethod,
                sanitizedPath,
                context.TraceIdentifier
            );
        }

        try
        {
            await next(context);

            stopwatch.Stop();
            if (logInformation)
            {
                logger.LogInformation(
                    "Request completed: {Method} {Path} - Status: {StatusCode} - Duration: {Duration}ms - TraceId: {TraceId}",
                    sanitizedMethod,
                    sanitizedPath,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    context.TraceIdentifier
                );
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Request failed: {Method} {Path} - Duration: {Duration}ms - TraceId: {TraceId}",
                sanitizedMethod.Length == 0
                    ? LoggingSanitizer.SanitizeForLogging(context.Request.Method)
                    : sanitizedMethod,
                sanitizedPath.Length == 0
                    ? LoggingSanitizer.SanitizeForLogging(context.Request.Path.Value)
                    : sanitizedPath,
                stopwatch.ElapsedMilliseconds,
                context.TraceIdentifier
            );

            var response = context.Response;
            if (!response.HasStarted)
            {
                try
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
                catch (Exception responseEx)
                {
                    logger.LogError(
                        responseEx,
                        "Failed to write error response for TraceId: {TraceId}",
                        context.TraceIdentifier
                    );
                }
            }

            // Re-throw with contextual information for the middleware pipeline
            throw new InvalidOperationException(
                $"Request processing failed for {LoggingSanitizer.SanitizeForLogging(context.Request.Method)} {LoggingSanitizer.SanitizeForLogging(context.Request.Path.Value)} - TraceId: {context.TraceIdentifier}",
                ex
            );
        }
    }
}

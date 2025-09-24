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

        logger.LogInformation(
            "Request started: {Method} {Path} - TraceId: {TraceId}",
            LoggingSanitizer.SanitizeForLogging(context.Request.Method),
            LoggingSanitizer.SanitizeForLogging(context.Request.Path.Value),
            LoggingSanitizer.SanitizeForLogging(context.TraceIdentifier)
        );

        try
        {
            await next(context);

            stopwatch.Stop();
            logger.LogInformation(
                "Request completed: {Method} {Path} - Status: {StatusCode} - Duration: {Duration}ms - TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(context.Request.Method),
                LoggingSanitizer.SanitizeForLogging(context.Request.Path.Value),
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                LoggingSanitizer.SanitizeForLogging(context.TraceIdentifier)
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Request failed: {Method} {Path} - Duration: {Duration}ms - TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(context.Request.Method),
                LoggingSanitizer.SanitizeForLogging(context.Request.Path.Value),
                stopwatch.ElapsedMilliseconds,
                LoggingSanitizer.SanitizeForLogging(context.TraceIdentifier)
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
                                traceId = LoggingSanitizer.SanitizeForLogging(context.TraceIdentifier),
                            }
                        )
                    );
                }
                catch (Exception responseEx)
                {
                    logger.LogError(
                        responseEx,
                        "Failed to write error response for TraceId: {TraceId}",
                        LoggingSanitizer.SanitizeForLogging(context.TraceIdentifier)
                    );
                }
            }

            // Re-throw with contextual information for the middleware pipeline
            throw new InvalidOperationException(
                $"Request processing failed for {context.Request.Method} {context.Request.Path} - TraceId: {context.TraceIdentifier}",
                ex
            );
        }
    }
}

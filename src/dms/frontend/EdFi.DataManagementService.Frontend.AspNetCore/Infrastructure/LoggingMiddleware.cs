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

        // Always compute a safe-for-logging value (escape newlines to avoid log forging
        // and ensure we never log raw user-provided values). Use these for all log calls
        // including error/exception paths where LogLevel.Information may be disabled.
        string sanitizedMethodForLog = string.IsNullOrEmpty(sanitizedMethod)
            ? (LoggingSanitizer.SanitizeForLogging(context.Request.Method) ?? string.Empty)
            : sanitizedMethod;
        sanitizedMethodForLog = sanitizedMethodForLog.Replace("\r", "\\r").Replace("\n", "\\n");

        string sanitizedPathForLog = string.IsNullOrEmpty(sanitizedPath)
            ? (LoggingSanitizer.SanitizeForLogging(context.Request.Path.Value) ?? string.Empty)
            : sanitizedPath;
        sanitizedPathForLog = sanitizedPathForLog.Replace("\r", "\\r").Replace("\n", "\\n");

        if (logInformation)
        {
            logger.LogInformation(
                "Request started: {Method} {Path} - TraceId: {TraceId}",
                sanitizedMethodForLog,
                sanitizedPathForLog,
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
                    sanitizedMethodForLog,
                    sanitizedPathForLog,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    context.TraceIdentifier
                );
            }
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
            when (ex.StatusCode == Microsoft.AspNetCore.Http.StatusCodes.Status413PayloadTooLarge)
        {
            stopwatch.Stop();
            logger.LogWarning(
                ex,
                "Request rejected because the payload was too large: {Method} {Path} - Duration: {Duration}ms - TraceId: {TraceId}",
                sanitizedMethodForLog,
                sanitizedPathForLog,
                stopwatch.ElapsedMilliseconds,
                context.TraceIdentifier
            );

            var response = context.Response;
            if (!response.HasStarted)
            {
                response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status413PayloadTooLarge;
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Request failed: {Method} {Path} - Duration: {Duration}ms - TraceId: {TraceId}",
                sanitizedMethodForLog,
                sanitizedPathForLog,
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
                $"Request processing failed for {sanitizedMethodForLog} {sanitizedPathForLog} - TraceId: {context.TraceIdentifier}",
                ex
            );
        }
    }
}

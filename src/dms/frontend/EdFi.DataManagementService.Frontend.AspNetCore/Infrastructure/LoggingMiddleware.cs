// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class LoggingMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Sanitizes input strings to prevent log injection attacks by removing or encoding potentially dangerous characters
    /// </summary>
    private static string SanitizeForLogging(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var sanitized = new StringBuilder();
        foreach (char c in input)
        {
            switch (c)
            {
                case '\r':
                case '\n':
                case '\t':
                    // Replace line breaks and tabs with spaces to prevent log injection
                    sanitized.Append(' ');
                    break;
                default:
                    // Only include printable ASCII characters and common safe Unicode characters
                    if (char.IsControl(c) && c != ' ')
                    {
                        sanitized.Append('?'); // Replace control characters
                    }
                    else
                    {
                        sanitized.Append(c);
                    }
                    break;
            }
        }
        return sanitized.ToString();
    }

    public async Task Invoke(HttpContext context, ILogger<LoggingMiddleware> logger)
    {
        var stopwatch = Stopwatch.StartNew();

        logger.LogInformation(
            "Request started: {Method} {Path} - TraceId: {TraceId}",
            SanitizeForLogging(context.Request.Method),
            SanitizeForLogging(context.Request.Path.Value),
            SanitizeForLogging(context.TraceIdentifier)
        );

        try
        {
            await next(context);

            stopwatch.Stop();
            logger.LogInformation(
                "Request completed: {Method} {Path} - Status: {StatusCode} - Duration: {Duration}ms - TraceId: {TraceId}",
                SanitizeForLogging(context.Request.Method),
                SanitizeForLogging(context.Request.Path.Value),
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                SanitizeForLogging(context.TraceIdentifier)
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Request failed: {Method} {Path} - Duration: {Duration}ms - TraceId: {TraceId}",
                SanitizeForLogging(context.Request.Method),
                SanitizeForLogging(context.Request.Path.Value),
                stopwatch.ElapsedMilliseconds,
                SanitizeForLogging(context.TraceIdentifier)
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
                            traceId = SanitizeForLogging(context.TraceIdentifier),
                        }
                    )
                );
            }
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that logs request entry and exit with traceId for all DMS Core Pipeline requests
/// </summary>
internal class RequestResponseLoggingMiddleware(ILogger _logger) : IPipelineStep
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

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        var traceId = requestInfo.FrontendRequest.TraceId.Value;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Core pipeline started: {Method} {Path} - TraceId: {TraceId}",
            SanitizeForLogging(requestInfo.Method.ToString()),
            SanitizeForLogging(requestInfo.FrontendRequest.Path),
            SanitizeForLogging(traceId)
        );

        try
        {
            await next();

            stopwatch.Stop();
            var statusCode = requestInfo.FrontendResponse?.StatusCode ?? 0;

            _logger.LogInformation(
                "Core pipeline completed: {Method} {Path} - Status: {StatusCode} - Duration: {Duration}ms - TraceId: {TraceId}",
                SanitizeForLogging(requestInfo.Method.ToString()),
                SanitizeForLogging(requestInfo.FrontendRequest.Path),
                statusCode,
                stopwatch.ElapsedMilliseconds,
                SanitizeForLogging(traceId)
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "Core pipeline failed: {Method} {Path} - Duration: {Duration}ms - TraceId: {TraceId}",
                SanitizeForLogging(requestInfo.Method.ToString()),
                SanitizeForLogging(requestInfo.FrontendRequest.Path),
                stopwatch.ElapsedMilliseconds,
                SanitizeForLogging(traceId)
            );

            // Re-throw with contextual information preserved in log
            throw new InvalidOperationException(
                $"Core pipeline execution failed for {SanitizeForLogging(requestInfo.Method.ToString())} {SanitizeForLogging(requestInfo.FrontendRequest.Path)} - TraceId: {SanitizeForLogging(traceId)}",
                ex
            );
        }
    }
}

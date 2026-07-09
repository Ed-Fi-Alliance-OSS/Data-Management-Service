// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using EdFi.DataManagementService.Core.External.Logging;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that logs request entry and exit with traceId for all DMS Core Pipeline requests
/// </summary>
internal class RequestResponseLoggingMiddleware(ILogger _logger) : IPipelineStep
{
    private const string ApplicationName = "EdFi.DataManagementService";
    private const string RequestLayer = "Core";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        var traceId = requestInfo.FrontendRequest.TraceId.Value;
        var stopwatch = Stopwatch.StartNew();
        string method = LoggingSanitizer.SanitizeForLogging(requestInfo.Method.ToString());
        string path = LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.Path);
        string sanitizedTraceId = LoggingSanitizer.SanitizeForLogging(traceId);

        var scopeValues = new Dictionary<string, object>
        {
            ["Application"] = ApplicationName,
            ["RequestLayer"] = RequestLayer,
            ["TraceId"] = sanitizedTraceId,
            ["Method"] = method,
            ["Path"] = path,
        };

        using (_logger.BeginScope(scopeValues))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Core pipeline started: {Method} {Path} - TraceId: {TraceId}",
                    method,
                    path,
                    sanitizedTraceId
                );
            }

            try
            {
                await next();

                stopwatch.Stop();
                if (requestInfo.CaughtException is not null)
                {
                    var failureStatusCode = GetFailureStatusCode(requestInfo);

                    _logger.LogError(
                        RequestLoggingEventIds.HttpRequestFailed,
                        requestInfo.CaughtException,
                        "{EventName}: DMS core request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                        RequestLoggingEventIds.HttpRequestFailed.Name,
                        method,
                        path,
                        failureStatusCode,
                        stopwatch.ElapsedMilliseconds,
                        sanitizedTraceId
                    );

                    return;
                }

                var statusCode = requestInfo.FrontendResponse?.StatusCode ?? 0;

                if (statusCode >= 500)
                {
                    _logger.Log(
                        LogLevel.Error,
                        RequestLoggingEventIds.HttpRequestFailed,
                        "{EventName}: DMS core request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                        RequestLoggingEventIds.HttpRequestFailed.Name,
                        method,
                        path,
                        statusCode,
                        stopwatch.ElapsedMilliseconds,
                        sanitizedTraceId
                    );

                    return;
                }

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.Log(
                        LogLevel.Information,
                        RequestLoggingEventIds.HttpRequestCompleted,
                        "{EventName}: DMS core request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                        RequestLoggingEventIds.HttpRequestCompleted.Name,
                        method,
                        path,
                        statusCode,
                        stopwatch.ElapsedMilliseconds,
                        sanitizedTraceId
                    );
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var statusCode = GetFailureStatusCode(requestInfo);
                _logger.LogError(
                    RequestLoggingEventIds.HttpRequestFailed,
                    ex,
                    "{EventName}: DMS core request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                    RequestLoggingEventIds.HttpRequestFailed.Name,
                    method,
                    path,
                    statusCode,
                    stopwatch.ElapsedMilliseconds,
                    sanitizedTraceId
                );

                // Re-throw with contextual information preserved in log
                throw new InvalidOperationException("Core pipeline execution failed.", ex);
            }
        }
    }

    private static int GetFailureStatusCode(RequestInfo requestInfo)
    {
        if (ReferenceEquals(requestInfo.FrontendResponse, No.FrontendResponse))
        {
            return (int)HttpStatusCode.InternalServerError;
        }

        var statusCode = requestInfo.FrontendResponse?.StatusCode ?? 0;
        return statusCode == 0 ? (int)HttpStatusCode.InternalServerError : statusCode;
    }
}

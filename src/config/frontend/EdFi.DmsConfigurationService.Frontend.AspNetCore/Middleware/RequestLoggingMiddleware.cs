// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

public class RequestLoggingMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private const string ApplicationName = "EdFi.DmsConfigurationService";

    public async Task Invoke(HttpContext context, ILogger<RequestLoggingMiddleware> logger)
    {
        var sw = Stopwatch.StartNew();
        var logLevel = context.Request.Path.StartsWithSegments(new PathString("/.well-known"))
            ? LogLevel.Debug
            : LogLevel.Information;

        var traceId = LoggingUtility.SanitizeForLog(context.TraceIdentifier);
        var method = LoggingUtility.SanitizeForLog(context.Request.Method);
        var path = LoggingUtility.SanitizeForLog(context.Request.Path.Value);
        var pathBase = LoggingUtility.SanitizeForLog(context.Request.PathBase.Value);

        var scopeValues = new Dictionary<string, object>
        {
            ["Application"] = ApplicationName,
            ["TraceId"] = traceId,
            ["Method"] = method,
            ["Path"] = path,
            ["PathBase"] = pathBase,
        };

        var activity = Activity.Current;
        if (activity is not null)
        {
            scopeValues["ActivityTraceId"] = activity.TraceId.ToString();
            scopeValues["SpanId"] = activity.SpanId.ToString();
        }

        using (logger.BeginScope(scopeValues))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Request started");
            }

            try
            {
                await _next(context);
                sw.Stop();

                var statusCode = context.Response?.StatusCode ?? 0;
                var statusCodeLogLevel =
                    statusCode >= StatusCodes.Status500InternalServerError ? LogLevel.Error : logLevel;

                if (!logger.IsEnabled(statusCodeLogLevel))
                {
                    return;
                }

                var durationMs = sw.ElapsedMilliseconds;
                scopeValues["StatusCode"] = statusCode;
                scopeValues["DurationMs"] = durationMs;

                if (statusCode >= StatusCodes.Status500InternalServerError)
                {
                    var handledException = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                    logger.LogError(
                        RequestLoggingEventIds.HttpRequestFailed,
                        handledException,
                        "{EventName}: CMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                        RequestLoggingEventIds.HttpRequestFailed.Name,
                        method,
                        path,
                        statusCode,
                        durationMs,
                        traceId
                    );
                }
                else
                {
                    logger.Log(
                        logLevel,
                        RequestLoggingEventIds.HttpRequestCompleted,
                        "{EventName}: CMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                        RequestLoggingEventIds.HttpRequestCompleted.Name,
                        method,
                        path,
                        statusCode,
                        durationMs,
                        traceId
                    );
                }
            }
            catch (Exception ex)
            {
                LogFailure(context, logger, sw, ex, scopeValues, method, path, traceId);
                throw;
            }
        }
    }

    private static void LogFailure(
        HttpContext context,
        ILogger<RequestLoggingMiddleware> logger,
        Stopwatch sw,
        Exception ex,
        Dictionary<string, object> scopeValues,
        string method,
        string path,
        string traceId
    )
    {
        try
        {
            if (sw.IsRunning)
            {
                sw.Stop();
            }

            var statusCode = GetFailureStatusCode(context);
            var durationMs = sw.ElapsedMilliseconds;
            scopeValues["StatusCode"] = statusCode;
            scopeValues["DurationMs"] = durationMs;

            logger.LogError(
                RequestLoggingEventIds.HttpRequestFailed,
                ex,
                "{EventName}: CMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                RequestLoggingEventIds.HttpRequestFailed.Name,
                method,
                path,
                statusCode,
                durationMs,
                traceId
            );
        }
        catch (Exception)
        {
            // Preserve the original downstream exception if the failure log path itself fails.
        }
    }

    private static int GetFailureStatusCode(HttpContext context) =>
        context.Response.HasStarted ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
}

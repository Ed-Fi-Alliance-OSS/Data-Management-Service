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
        var scopeValues = BuildScopeValues(context);

        using (logger.BeginScope(scopeValues))
        {
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

                scopeValues["StatusCode"] = statusCode;
                scopeValues["DurationMs"] = sw.ElapsedMilliseconds;

                if (statusCode >= StatusCodes.Status500InternalServerError)
                {
                    var handledException = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                    if (handledException is not null)
                    {
                        logger.LogError(
                            RequestLoggingEventIds.HttpRequestFailed,
                            handledException,
                            "{EventName}: CMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                            RequestLoggingEventIds.HttpRequestFailed.Name,
                            (string)scopeValues["Method"],
                            (string)scopeValues["Path"],
                            scopeValues["StatusCode"],
                            scopeValues["DurationMs"],
                            (string)scopeValues["TraceId"]
                        );
                    }
                    else
                    {
                        logger.LogError(
                            RequestLoggingEventIds.HttpRequestFailed,
                            "{EventName}: CMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                            RequestLoggingEventIds.HttpRequestFailed.Name,
                            (string)scopeValues["Method"],
                            (string)scopeValues["Path"],
                            scopeValues["StatusCode"],
                            scopeValues["DurationMs"],
                            (string)scopeValues["TraceId"]
                        );
                    }
                }
                else
                {
                    logger.Log(
                        logLevel,
                        RequestLoggingEventIds.HttpRequestCompleted,
                        "{EventName}: CMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                        RequestLoggingEventIds.HttpRequestCompleted.Name,
                        (string)scopeValues["Method"],
                        (string)scopeValues["Path"],
                        scopeValues["StatusCode"],
                        scopeValues["DurationMs"],
                        (string)scopeValues["TraceId"]
                    );
                }
            }
            catch (Exception ex)
            {
                LogFailure(context, logger, sw, ex, scopeValues);
                throw;
            }
        }
    }

    private static void LogFailure(
        HttpContext context,
        ILogger<RequestLoggingMiddleware> logger,
        Stopwatch sw,
        Exception ex,
        Dictionary<string, object> scopeValues
    )
    {
        try
        {
            if (sw.IsRunning)
            {
                sw.Stop();
            }

            scopeValues["StatusCode"] = GetFailureStatusCode(context);
            scopeValues["DurationMs"] = sw.ElapsedMilliseconds;

            logger.LogError(
                RequestLoggingEventIds.HttpRequestFailed,
                ex,
                "{EventName}: CMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                RequestLoggingEventIds.HttpRequestFailed.Name,
                (string)scopeValues["Method"],
                (string)scopeValues["Path"],
                scopeValues["StatusCode"],
                scopeValues["DurationMs"],
                (string)scopeValues["TraceId"]
            );
        }
        catch (Exception)
        {
            // Preserve the original downstream exception if the failure log path itself fails.
        }
    }

    private static Dictionary<string, object> BuildScopeValues(HttpContext context)
    {
        var scopeValues = new Dictionary<string, object>
        {
            ["Application"] = ApplicationName,
            ["TraceId"] = LoggingUtility.SanitizeForLog(context.TraceIdentifier),
            ["Method"] = LoggingUtility.SanitizeForLog(context.Request.Method),
            ["Path"] = LoggingUtility.SanitizeForLog(context.Request.Path.Value),
            ["PathBase"] = LoggingUtility.SanitizeForLog(context.Request.PathBase.Value),
        };

        var activity = Activity.Current;
        if (activity is not null)
        {
            scopeValues["ActivityTraceId"] = activity.TraceId.ToString();
            scopeValues["SpanId"] = activity.SpanId.ToString();
        }

        return scopeValues;
    }

    private static int GetFailureStatusCode(HttpContext context)
    {
        var statusCode = context.Response?.StatusCode ?? 0;
        if (context.Response is not null && !context.Response.HasStarted)
        {
            return StatusCodes.Status500InternalServerError;
        }

        return statusCode == 0 ? StatusCodes.Status500InternalServerError : statusCode;
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.External.Logging;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class LoggingMiddleware(RequestDelegate next, IOptions<AppSettings> appSettings)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly IOptions<AppSettings> _appSettings =
        appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    private const string ApplicationName = "EdFi.DataManagementService";
    private const string RequestLayer = "Frontend";

    public async Task Invoke(HttpContext context, ILogger<LoggingMiddleware> logger)
    {
        var stopwatch = Stopwatch.StartNew();
        var sanitizedMethod = LoggingSanitizer.SanitizeForLogging(context.Request.Method);
        var sanitizedPath = LoggingSanitizer.SanitizeForLogging(context.Request.Path.Value);
        var pathBase = LoggingSanitizer.SanitizeForLogging(context.Request.PathBase.Value);
        var traceId = LoggingSanitizer.SanitizeForLogging(ExtractTraceId(context));

        var scopeValues = new Dictionary<string, object>
        {
            ["Application"] = ApplicationName,
            ["RequestLayer"] = RequestLayer,
            ["TraceId"] = traceId,
            ["Method"] = sanitizedMethod,
            ["Path"] = sanitizedPath,
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

                stopwatch.Stop();
                var statusCode = context.Response?.StatusCode ?? 0;
                if (statusCode >= StatusCodes.Status500InternalServerError)
                {
                    logger.Log(
                        LogLevel.Error,
                        RequestLoggingEventIds.HttpRequestFailed,
                        "{EventName}: DMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                        RequestLoggingEventIds.HttpRequestFailed.Name,
                        sanitizedMethod,
                        sanitizedPath,
                        statusCode,
                        stopwatch.ElapsedMilliseconds,
                        traceId
                    );

                    return;
                }

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.Log(
                        LogLevel.Information,
                        RequestLoggingEventIds.HttpRequestCompleted,
                        "{EventName}: DMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                        RequestLoggingEventIds.HttpRequestCompleted.Name,
                        sanitizedMethod,
                        sanitizedPath,
                        statusCode,
                        stopwatch.ElapsedMilliseconds,
                        traceId
                    );
                }
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                stopwatch.Stop();

                var response = context.Response;
                if (!response.HasStarted)
                {
                    response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                }

                // An oversized body is a client error the pipeline responded to, so it participates
                // in the request-log contract as a completion event. The logged status must be the
                // status the client actually received, which is only 413 when the response had not
                // already started. The caught exception is expected control flow, not a failure, so
                // it is deliberately not attached to this Information-level completion event.
                var statusCode = response.StatusCode;
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception as a parameter
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.Log(
                        LogLevel.Information,
                        RequestLoggingEventIds.HttpRequestCompleted,
                        "{EventName}: DMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                        RequestLoggingEventIds.HttpRequestCompleted.Name,
                        sanitizedMethod,
                        sanitizedPath,
                        statusCode,
                        stopwatch.ElapsedMilliseconds,
                        traceId
                    );
                }
#pragma warning restore S6667
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var failureStatusCode = GetFailureStatusCode(context);
                var durationMs = stopwatch.ElapsedMilliseconds;

                logger.LogError(
                    RequestLoggingEventIds.HttpRequestFailed,
                    ex,
                    "{EventName}: DMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
                    RequestLoggingEventIds.HttpRequestFailed.Name,
                    sanitizedMethod,
                    sanitizedPath,
                    failureStatusCode,
                    durationMs,
                    traceId
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
                                    // The error response body must carry the same value the log events use
                                    // as TraceId, or a client-reported trace id cannot be found in the logs.
                                    traceId,
                                }
                            )
                        );
                    }
                    catch (Exception responseEx)
                    {
                        logger.LogError(
                            responseEx,
                            "Failed to write error response for TraceId: {TraceId}",
                            traceId
                        );
                    }
                }

                // Preserve existing behavior: wrap and rethrow. Keep the request identity in the
                // wrapper message so host-level logging retains correlation after the scope is disposed.
                throw new InvalidOperationException(
                    $"Request processing failed for {sanitizedMethod} {sanitizedPath} - TraceId: {traceId}",
                    ex
                );
            }
        }
    }

    private string? ExtractTraceId(HttpContext context)
    {
        try
        {
            return AspNetCoreFrontend.ExtractTraceIdFrom(context.Request, _appSettings).Value;
        }
        catch (OptionsValidationException)
        {
            return context.TraceIdentifier;
        }
    }

    private static int GetFailureStatusCode(HttpContext context) =>
        context.Response.HasStarted ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
}

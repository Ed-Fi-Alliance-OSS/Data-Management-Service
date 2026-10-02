// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

public class RequestLoggingMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private const string ApplicationName = "EdFi.DmsConfigurationService";

    private const string FailedTemplate =
        "{EventName}: CMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}";

    /// <summary>
    /// The failure event for a route marked with <see cref="ExceptionTypeOnlyLoggingMetadata"/>: the
    /// same event and fields, plus the exception type names in place of the exception object.
    /// </summary>
    private const string TypeOnlyFailedTemplate =
        "{EventName}: CMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId} ({ExceptionTypes})";

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

                // StatusCode and DurationMs travel in the completion/failure log event state
                // only; mutating the scope dictionary after BeginScope would depend on
                // providers reading the mutable dictionary lazily.
                var durationMs = sw.ElapsedMilliseconds;

                if (statusCode >= StatusCodes.Status500InternalServerError)
                {
                    var handledException = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                    if (LogsExceptionTypesOnly(context))
                    {
                        LogTypeOnlyFailure(
                            logger,
                            handledException,
                            method,
                            path,
                            statusCode,
                            durationMs,
                            traceId
                        );
                    }
                    else
                    {
                        logger.LogError(
                            RequestLoggingEventIds.HttpRequestFailed,
                            handledException,
                            FailedTemplate,
                            RequestLoggingEventIds.HttpRequestFailed.Name,
                            method,
                            path,
                            statusCode,
                            durationMs,
                            traceId
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
                bool typesOnly = LogsExceptionTypesOnly(context);
                LogFailure(context, logger, sw, ex, method, path, traceId, typesOnly);
                if (typesOnly)
                {
                    // The server logs an exception that escapes the pipeline with its own logger, so a
                    // route whose exception text is not under CMS control hands it a replacement.
                    throw TypeOnlyReplacement(context, ex);
                }
                throw;
            }
        }
    }

    private static void LogFailure(
        HttpContext context,
        ILogger<RequestLoggingMiddleware> logger,
        Stopwatch sw,
        Exception ex,
        string method,
        string path,
        string traceId,
        bool typesOnly
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

            if (typesOnly)
            {
                LogTypeOnlyFailure(logger, ex, method, path, statusCode, durationMs, traceId);
                return;
            }

            logger.LogError(
                RequestLoggingEventIds.HttpRequestFailed,
                ex,
                FailedTemplate,
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

    /// <summary>
    /// Whether the request's route is marked with <see cref="ExceptionTypeOnlyLoggingMetadata"/>.
    /// Exception handling clears the active endpoint but keeps the original on
    /// <see cref="IExceptionHandlerFeature"/>, so that is read first.
    /// </summary>
    private static bool LogsExceptionTypesOnly(HttpContext context) =>
        (
            context.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? context.GetEndpoint()
        )?.Metadata.GetMetadata<ExceptionTypeOnlyLoggingMetadata>()
            is not null;

    /// <summary>
    /// The failure event with the exception type names as a field and no exception object attached,
    /// so no message, inner exception or <c>Data</c> reaches a log sink.
    /// </summary>
    private static void LogTypeOnlyFailure(
        ILogger<RequestLoggingMiddleware> logger,
        Exception? exception,
        string method,
        string path,
        int statusCode,
        long durationMs,
        string traceId
    ) =>
        logger.LogError(
            RequestLoggingEventIds.HttpRequestFailed,
            TypeOnlyFailedTemplate,
            RequestLoggingEventIds.HttpRequestFailed.Name,
            method,
            path,
            statusCode,
            durationMs,
            traceId,
            exception is null ? "none" : ExceptionTypeNames.Chain(exception)
        );

    /// <summary>
    /// The exception rethrown to the server in place of one from a route that logs exception types
    /// only. A caller cancellation stays a cancellation, so the server still treats the request as
    /// aborted; anything else becomes a fixed-text exception naming only the original type chain.
    /// Neither carries the original as its inner exception.
    /// </summary>
    private static Exception TypeOnlyReplacement(HttpContext context, Exception exception) =>
        exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested
            ? new OperationCanceledException(context.RequestAborted)
            : new InvalidOperationException(
                $"The request failed with {ExceptionTypeNames.Chain(exception)}; the exception's content was withheld."
            );

    private static int GetFailureStatusCode(HttpContext context) =>
        context.Response.HasStarted ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
}

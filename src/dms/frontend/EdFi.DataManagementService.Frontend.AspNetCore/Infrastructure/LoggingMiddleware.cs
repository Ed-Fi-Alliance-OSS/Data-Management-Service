// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.External.Diagnostics;
using EdFi.DataManagementService.Core.External.Logging;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class LoggingMiddleware(
    RequestDelegate next,
    IOptions<AppSettings> appSettings,
    IOptions<RequestTimingOptions>? requestTimingOptions = null
)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly IOptions<AppSettings> _appSettings =
        appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    private readonly IOptions<RequestTimingOptions> _requestTimingOptions =
        requestTimingOptions ?? Options.Create(new RequestTimingOptions());
    private const string ApplicationName = "EdFi.DataManagementService";
    private const string RequestLayer = "Frontend";

    public async Task Invoke(HttpContext context, ILogger<LoggingMiddleware> logger)
    {
        var stopwatch = Stopwatch.StartNew();
        var sanitizedMethod = LoggingSanitizer.SanitizeForLogging(context.Request.Method);
        var sanitizedPath = LoggingSanitizer.SanitizeForLogging(context.Request.Path.Value);
        var pathBase = LoggingSanitizer.SanitizeForLogging(context.Request.PathBase.Value);
        var rawTraceId = ExtractTraceId(context) ?? string.Empty;
        var traceId = LoggingSanitizer.SanitizeForLogging(rawTraceId);

        // DMS-1236: start the ambient per-request phase timing context.
        RequestTiming? timing = null;
        if (RequestTimingContext.Enabled)
        {
            timing = RequestTimingContext.Begin(
                context.Request.Method,
                NormalizeResourcePath(context.Request.Path.Value)
            );
            RequestTimingRegistry.IncrementInFlight();
        }

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

        using var requestScope = logger.BeginScope(scopeValues);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Request started");
        }

        try
        {
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
                                    // The error response body echoes the raw correlation value, matching
                                    // every other DMS error response body (see FailureResponse). Only log
                                    // properties are sanitized; applying the logging whitelist to a
                                    // client-reported trace id yields the TraceId to search for in the logs.
                                    traceId = rawTraceId,
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
        finally
        {
            if (timing is not null)
            {
                CompleteRequestTiming(timing, context, logger);
            }
        }
    }

    /// <summary>
    /// DMS-1236: finalizes the request's timing context, folds it into the global
    /// registry, and emits a detailed phase breakdown for slow or sampled requests.
    /// </summary>
    private void CompleteRequestTiming(RequestTiming timing, HttpContext context, ILogger logger)
    {
        RequestTimingRegistry.DecrementInFlight();
        timing.Stop();
        RequestTimingRegistry.FoldRequest(timing);

        RequestTimingOptions options = _requestTimingOptions.Value;
        bool isSlow = options.SlowRequestThresholdMs > 0 && timing.TotalMs >= options.SlowRequestThresholdMs;
        bool isSampled = RequestTimingRegistry.ShouldSampleDetail(options.DetailSampleEveryN);

        if (isSlow || isSampled)
        {
            LogLevel level = isSlow ? LogLevel.Warning : LogLevel.Information;
            if (logger.IsEnabled(level))
            {
                logger.Log(
                    level,
                    "RequestTimingDetail{Kind}: {Method} {Resource} pipeline={Pipeline} status={StatusCode} total={TotalMs}ms phases: {Phases}",
                    isSlow ? "(slow)" : "(sampled)",
                    timing.Method,
                    timing.Resource,
                    timing.Pipeline,
                    context.Response?.StatusCode ?? 0,
                    Math.Round(timing.TotalMs, 2),
                    FormatPhases(timing)
                );
            }
        }

        RequestTimingContext.End();
    }

    /// <summary>
    /// Renders the recorded phases as "+offset phase duration [detail]" entries in
    /// chronological order, on a single line for log friendliness.
    /// </summary>
    private static string FormatPhases(RequestTiming timing)
    {
        List<PhaseSample> samples = [.. timing.SnapshotSamples()];
        samples.Sort((a, b) => a.OffsetMs.CompareTo(b.OffsetMs));

        StringBuilder builder = new(samples.Count * 48);
        foreach (PhaseSample sample in samples)
        {
            if (builder.Length > 0)
            {
                builder.Append(" | ");
            }

            builder
                .Append('+')
                .Append(sample.OffsetMs.ToString("F1"))
                .Append(' ')
                .Append(sample.Phase)
                .Append(' ')
                .Append(sample.DurationMs.ToString("F2"))
                .Append("ms");

            if (sample.Detail is not null)
            {
                builder.Append(" [").Append(sample.Detail).Append(']');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Normalizes a request path into a low-cardinality endpoint key: empty segments are
    /// dropped and GUID segments (document ids) are replaced with "{id}".
    /// </summary>
    internal static string NormalizeResourcePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "(root)";
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "(root)";
        }

        for (int i = 0; i < segments.Length; i++)
        {
            if (Guid.TryParse(segments[i], out _))
            {
                segments[i] = "{id}";
            }
        }

        return string.Join('/', segments);
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

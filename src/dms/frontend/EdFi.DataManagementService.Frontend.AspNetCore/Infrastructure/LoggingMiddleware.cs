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

public class LoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<AppSettings> _appSettings;
    private const string ApplicationName = "EdFi.DataManagementService";
    private const string RequestLayer = "Frontend";

    public LoggingMiddleware(RequestDelegate next, IOptions<AppSettings> appSettings)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    }

    public async Task Invoke(HttpContext context, ILogger<LoggingMiddleware> logger)
    {
        var stopwatch = Stopwatch.StartNew();
        var sanitizedMethod = LoggingSanitizer.SanitizeInternalValueForLogging(context.Request.Method);
        // Redact an identity get-by-id or results-poll identifier (D11) before sanitizing, so the
        // scope Path property and every rendered message template below carry the redacted value,
        // never the real identifier. Every other path - including the other four identity routes,
        // which carry no identifier - passes through unchanged.
        var redactedPath = RedactPath(context, context.Request.Path, _appSettings);
        var sanitizedPath = LoggingSanitizer.SanitizeInternalValueForLogging(redactedPath);
        var pathBase = LoggingSanitizer.SanitizeInternalValueForLogging(context.Request.PathBase.Value);
        // Normalized at the ingestion boundary by AspNetCoreFrontend, so no second,
        // differently-shaped normalization happens here; Method and Path keep the stricter
        // SanitizeInternalValueForLogging allowlist above. Registered ahead of routing, the rate
        // limiter and endpoint execution, this is the first component to ingest the correlation
        // ID, which is what makes the value it caches the one every later call site reads. No
        // guard for a host whose AppSettings failed validation: ingestion is total.
        var ingestion = AspNetCoreFrontend.IngestCorrelationIdFrom(context.Request, _appSettings);
        var traceId = ingestion.TraceId.Value;

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
            LogCorrelationIdModification(logger, ingestion, traceId);

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
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
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
                                    // The error response body echoes the normalized correlation
                                    // value so it always matches the TraceId searchable in the
                                    // logs. Because normalization happens once, at the ingestion
                                    // boundary, this body carries the same value as every other
                                    // DMS error response for the same request.
                                    traceId = traceId,
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

    /// <summary>
    /// Emits an Information-level notice when the correlation ID the request is logged and
    /// answered under is not the value the client sent.
    /// </summary>
    /// <remarks>
    /// <b>The original value is deliberately absent</b> - not raw, not sanitized, not truncated.
    /// What is logged instead is derived facts - two lengths and three booleans - plus the
    /// resulting normalized <c>TraceId</c>, which is already safe and is the key an operator
    /// searches on. Nothing at all is emitted on the normal path: no configured header, none
    /// sent, or a value that survived normalization unchanged.
    ///
    /// Both decisions, and the reverse-lookup limitation they accept, are settled in
    /// <c>reference/adr-correlation-id-normalization.md</c>.
    /// </remarks>
    private static void LogCorrelationIdModification(
        ILogger<LoggingMiddleware> logger,
        AspNetCoreFrontend.CorrelationIdIngestion ingestion,
        string traceId
    )
    {
        if (!ingestion.WasModified || !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        logger.Log(
            LogLevel.Information,
            CorrelationIdLoggingEventIds.CorrelationIdModified,
            "{EventName}: Client-supplied correlation ID was modified by normalization; the original value is deliberately not logged. "
                + "SuppliedLength {SuppliedLength}, NormalizedLength {NormalizedLength}, CharactersRemoved {CharactersRemoved}, "
                + "Truncated {Truncated}, FellBackToServerIdentifier {FellBackToServerIdentifier}, TraceId {TraceId}",
            CorrelationIdLoggingEventIds.CorrelationIdModified.Name,
            ingestion.SuppliedLength,
            traceId.Length,
            ingestion.CharactersRemoved,
            ingestion.Truncated,
            ingestion.FellBackToServerIdentifier,
            traceId
        );
    }

    private static int GetFailureStatusCode(HttpContext context) =>
        context.Response.HasStarted ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;

    /// <summary>
    /// Redacts an identity get-by-id or results-poll identifier from <paramref name="path"/> (D11).
    /// </summary>
    /// <remarks>
    /// Reads <see cref="AppSettings.MultiTenancy"/> and the configured route-qualifier segments to
    /// build the redaction pattern, through <see cref="AspNetCoreFrontend.TryReadAppSettings"/>
    /// rather than <paramref name="appSettings"/> directly: that helper caches the read (or the
    /// fact that it threw) on <see cref="HttpContext.Items"/>, so a request whose <c>AppSettings</c>
    /// failed validation costs one read and one thrown <see cref="OptionsValidationException"/>
    /// total, shared with <see cref="AspNetCoreFrontend.IngestCorrelationIdFrom"/> below, not a
    /// second one for redaction. Degrades to no redaction, rather than throwing, when the options
    /// value cannot be read.
    /// </remarks>
    private static string? RedactPath(HttpContext context, PathString path, IOptions<AppSettings> appSettings)
    {
        AppSettings? settings = AspNetCoreFrontend.TryReadAppSettings(context, appSettings);
        if (settings is null)
        {
            return path.Value;
        }

        return IdentityRoutePathRedactor.Redact(
            path,
            settings.GetRouteQualifierSegmentsArray(),
            settings.MultiTenancy
        );
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that logs request entry and exit with traceId for all DMS Core Pipeline requests
/// </summary>
internal class RequestResponseLoggingMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        var traceId = requestInfo.FrontendRequest.TraceId.Value;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Core pipeline started: {Method} {Path} - TraceId: {TraceId}",
            requestInfo.Method,
            requestInfo.FrontendRequest.Path,
            traceId
        );

        try
        {
            await next();

            stopwatch.Stop();
            var statusCode = requestInfo.FrontendResponse?.StatusCode ?? 0;

            _logger.LogInformation(
                "Core pipeline completed: {Method} {Path} - Status: {StatusCode} - Duration: {Duration}ms - TraceId: {TraceId}",
                requestInfo.Method,
                requestInfo.FrontendRequest.Path,
                statusCode,
                stopwatch.ElapsedMilliseconds,
                traceId
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "Core pipeline failed: {Method} {Path} - Duration: {Duration}ms - TraceId: {TraceId}",
                requestInfo.Method,
                requestInfo.FrontendRequest.Path,
                stopwatch.ElapsedMilliseconds,
                traceId
            );

            // Re-throw with contextual information preserved in log
            throw new InvalidOperationException(
                $"Core pipeline execution failed for {requestInfo.Method} {requestInfo.FrontendRequest.Path} - TraceId: {traceId}",
                ex
            );
        }
    }
}

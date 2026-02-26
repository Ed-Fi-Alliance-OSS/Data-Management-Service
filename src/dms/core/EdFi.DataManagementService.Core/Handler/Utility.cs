// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Timeout;

namespace EdFi.DataManagementService.Core.Handler;

public static class Utility
{
    /// <summary>
    /// ResilienceContext property key for TraceId, used to correlate per-retry log lines.
    /// </summary>
    internal static readonly ResiliencePropertyKey<string> TraceIdKey = new("TraceId");

    /// <summary>
    /// ResilienceContext property key for the operation name (e.g. "upsert", "delete").
    /// </summary>
    internal static readonly ResiliencePropertyKey<string> OperationNameKey = new("OperationName");

    /// <summary>
    /// Formats a error result string from the given error information and traceId
    /// </summary>
    public static JsonNode? ToJsonError(string errorInfo, TraceId traceId)
    {
        return new JsonObject { ["error"] = errorInfo, ["correlationId"] = traceId.Value };
    }

    /// <summary>
    /// Executes an operation within a resilience pipeline, handling retry logging and timeout.
    /// Returns null if a timeout occurred (caller should return early).
    /// </summary>
    internal static async Task<TResult?> ExecuteWithRetryLogging<TResult>(
        ResiliencePipeline resiliencePipeline,
        ILogger logger,
        string operationName,
        TraceId traceId,
        Func<TResult, bool> isRetryExhausted,
        Func<CancellationToken, ValueTask<TResult>> operation,
        RequestInfo requestInfo
    )
        where TResult : class
    {
        int attemptCount = 0;
        var context = ResilienceContextPool.Shared.Get();
        context.Properties.Set(TraceIdKey, traceId.Value);
        context.Properties.Set(OperationNameKey, operationName);

        try
        {
            var result = await resiliencePipeline.ExecuteAsync(
                async ctx =>
                {
                    attemptCount++;
                    return await operation(ctx.CancellationToken);
                },
                context
            );

            if (isRetryExhausted(result))
            {
                if (attemptCount > 1)
                {
                    logger.LogError(
                        "All deadlock retry attempts exhausted for {OperationName} after {AttemptCount} attempts - {TraceId}",
                        operationName,
                        attemptCount,
                        traceId.Value
                    );
                }
                else
                {
                    logger.LogWarning(
                        "Operation {OperationName} returned retryable result but retries are disabled - {TraceId}",
                        operationName,
                        traceId.Value
                    );
                }
            }
            else if (attemptCount > 1)
            {
                logger.LogWarning(
                    "Deadlock resolved after {RetryCount} retries for {OperationName} - {TraceId}",
                    attemptCount - 1,
                    operationName,
                    traceId.Value
                );
            }

            return result;
        }
        catch (TimeoutRejectedException ex)
        {
            logger.LogError(
                ex,
                "Operation timed out after {AttemptCount} attempts for {OperationName} - {TraceId}",
                attemptCount,
                operationName,
                traceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: ToJsonError("Request timed out due to database contention", traceId),
                Headers: []
            );
            return null;
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}

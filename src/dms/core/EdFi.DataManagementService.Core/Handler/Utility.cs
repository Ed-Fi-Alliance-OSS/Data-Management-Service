// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Polly;

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
    /// Returns true if the given result represents a retryable transient failure
    /// (deadlock or serialization conflict). This is the single source of truth
    /// for the retry predicate used by the resilience pipeline and handler logging.
    /// </summary>
    internal static bool IsRetryableResult(object result) =>
        result
            is DeleteResult.DeleteFailureWriteConflict
                or GetResult.GetFailureRetryable
                or QueryResult.QueryFailureRetryable
                or UpdateResult.UpdateFailureWriteConflict
                or UpsertResult.UpsertFailureWriteConflict;

    /// <summary>
    /// Formats a error result string from the given error information and traceId
    /// </summary>
    public static JsonNode? ToJsonError(string errorInfo, TraceId traceId)
    {
        return new JsonObject { ["error"] = errorInfo, ["correlationId"] = traceId.Value };
    }

    /// <summary>
    /// Executes an operation within a resilience pipeline, handling retry logging.
    /// </summary>
    internal static async Task<TResult?> ExecuteWithRetryLogging<TResult>(
        ResiliencePipeline resiliencePipeline,
        ILogger logger,
        string operationName,
        TraceId traceId,
        Func<TResult, bool> isRetryExhausted,
        Func<TResult, bool> isSuccess,
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
                if (isSuccess(result))
                {
                    logger.LogWarning(
                        "Deadlock resolved after {RetryCount} retries for {OperationName} - {TraceId}",
                        attemptCount - 1,
                        operationName,
                        traceId.Value
                    );
                }
                else
                {
                    logger.LogWarning(
                        "Operation {OperationName} ended with non-retryable result {ResultType} after {RetryCount} retries - {TraceId}",
                        operationName,
                        result.GetType().Name,
                        attemptCount - 1,
                        traceId.Value
                    );
                }
            }

            return result;
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}

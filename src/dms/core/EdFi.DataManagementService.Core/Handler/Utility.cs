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
using Polly.Retry;

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
    /// Creates the OnRetry callback used by the deadlock retry policy.
    /// Extracted so the production pipeline and tests share the same implementation.
    /// </summary>
    internal static Func<OnRetryArguments<object>, ValueTask> CreateOnRetryHandler(
        ILogger retryLogger,
        int maxRetryAttempts
    )
    {
        return args =>
        {
            args.Context.Properties.TryGetValue(TraceIdKey, out var traceId);
            args.Context.Properties.TryGetValue(OperationNameKey, out var operationName);

            retryLogger.LogWarning(
                "Deadlock retry attempt {DeadlockRetryAttempt}/{DeadlockRetryMaxAttempts} "
                    + "after {DelayMs}ms. OperationType: {OperationType}, "
                    + "OperationName: {OperationName} - {TraceId}",
                args.AttemptNumber,
                maxRetryAttempts,
                args.RetryDelay.TotalMilliseconds,
                args.Outcome.Result?.GetType().Name,
                operationName ?? "unknown",
                traceId ?? "unknown"
            );

            return ValueTask.CompletedTask;
        };
    }

    /// <summary>
    /// Formats an error result from the given error information and traceId.
    /// This intentionally differs from the problem+json shape (FailureResponse/ProblemDetailsResponse)
    /// because handler-level unknown failures do not set ContentType to application/problem+json.
    /// </summary>
    public static JsonNode? ToJsonError(string errorInfo, TraceId traceId)
    {
        return new JsonObject { ["error"] = errorInfo, ["correlationId"] = traceId.Value };
    }

    /// <summary>
    /// Executes an operation within a resilience pipeline, handling retry logging.
    /// </summary>
    internal static async Task<TResult> ExecuteWithRetryLogging<TResult>(
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
                var resourceName = requestInfo.ResourceInfo.ResourceName.Value;
                var documentUuid = requestInfo.PathComponents.DocumentUuid.Value;

                if (attemptCount > 1)
                {
                    logger.LogError(
                        "All deadlock retry attempts exhausted for {OperationName} on resource {ResourceName} "
                            + "(DocumentUuid: {DocumentUuid}) after {AttemptCount} attempts - {TraceId}",
                        operationName,
                        resourceName,
                        documentUuid,
                        attemptCount,
                        traceId.Value
                    );
                }
                else
                {
                    logger.LogWarning(
                        "Operation {OperationName} on resource {ResourceName} (DocumentUuid: {DocumentUuid}) "
                            + "returned retryable result but retries are disabled - {TraceId}",
                        operationName,
                        resourceName,
                        documentUuid,
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

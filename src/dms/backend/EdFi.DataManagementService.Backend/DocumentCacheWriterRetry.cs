// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace EdFi.DataManagementService.Backend;

internal sealed record DocumentCacheWriterRetryRequest
{
    public DocumentCacheWriterRetryRequest(
        RelationalProviderToken providerToken,
        DocumentCacheProjectionTargetKey targetKey,
        DocumentCacheWriterPurpose purpose,
        CancellationToken cancellationToken
    )
    {
        ProviderToken = providerToken ?? throw new ArgumentNullException(nameof(providerToken));
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        Purpose = DocumentCacheMaterializerGuards.RequireDefined(
            purpose,
            nameof(purpose),
            "Unsupported cache-writer purpose."
        );
        CancellationToken = cancellationToken;
    }

    public RelationalProviderToken ProviderToken { get; }

    public DocumentCacheProjectionTargetKey TargetKey { get; }

    public DocumentCacheWriterPurpose Purpose { get; }

    public CancellationToken CancellationToken { get; }

    public string SanitizedProvider => LoggingSanitizer.SanitizeForLogging(ProviderToken.Value);

    public string SanitizedTargetKey =>
        LoggingSanitizer.SanitizeForLogging(
            $"{(TargetKey.TenantKey.Length == 0 ? "(default)" : TargetKey.TenantKey)}:{TargetKey.DataStoreId.Value}"
        );
}

internal sealed record DocumentCacheWriterRetryAttemptContext
{
    public DocumentCacheWriterRetryAttemptContext(int attemptNumber)
    {
        if (attemptNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptNumber),
                attemptNumber,
                "Attempt number must be positive."
            );
        }

        AttemptNumber = attemptNumber;
    }

    public int AttemptNumber { get; }
}

internal interface IDocumentCacheWriterRetryAdapter
{
    Task<DocumentCacheWriterResult> ExecuteAsync(
        DocumentCacheWriterRetryRequest request,
        Func<
            DocumentCacheWriterRetryAttemptContext,
            CancellationToken,
            Task<DocumentCacheWriterResult>
        > attempt
    );
}

public sealed class DocumentCacheWriterRetryableDeleteRaceException : Exception
{
    public DocumentCacheWriterRetryableDeleteRaceException()
        : base("Retryable DocumentCache writer delete race.") { }
}

internal sealed class DocumentCacheWriterRetryAdapter : IDocumentCacheWriterRetryAdapter
{
    private static readonly ResiliencePropertyKey<string> ProviderKey = new("DocumentCacheWriterProvider");
    private static readonly ResiliencePropertyKey<string> TargetKey = new("DocumentCacheWriterTarget");
    private static readonly ResiliencePropertyKey<string> PurposeKey = new("DocumentCacheWriterPurpose");

    private readonly DeadlockRetrySettings _settings;
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier;
    private readonly ILogger<DocumentCacheWriterRetryAdapter> _logger;
    private readonly ResiliencePipeline<DocumentCacheWriterResult> _pipeline;

    public DocumentCacheWriterRetryAdapter(
        DeadlockRetrySettings settings,
        IRelationalWriteExceptionClassifier writeExceptionClassifier,
        ILogger<DocumentCacheWriterRetryAdapter> logger
    )
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.Validate();
        _writeExceptionClassifier =
            writeExceptionClassifier ?? throw new ArgumentNullException(nameof(writeExceptionClassifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pipeline = BuildPipeline();
    }

    public async Task<DocumentCacheWriterResult> ExecuteAsync(
        DocumentCacheWriterRetryRequest request,
        Func<
            DocumentCacheWriterRetryAttemptContext,
            CancellationToken,
            Task<DocumentCacheWriterResult>
        > attempt
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(attempt);

        int attemptCount = 0;
        ResilienceContext context = ResilienceContextPool.Shared.Get(request.CancellationToken);
        context.Properties.Set(ProviderKey, request.SanitizedProvider);
        context.Properties.Set(TargetKey, request.SanitizedTargetKey);
        context.Properties.Set(PurposeKey, request.Purpose.ToString());

        try
        {
            DocumentCacheWriterResult result = await _pipeline.ExecuteAsync(
                async resilienceContext =>
                {
                    attemptCount++;
                    return await attempt(
                        new DocumentCacheWriterRetryAttemptContext(attemptCount),
                        resilienceContext.CancellationToken
                    );
                },
                context
            );

            if (attemptCount > 1)
            {
                _logger.LogWarning(
                    "DocumentCache writer retry resolved after {RetryCount} retries. "
                        + "Provider: {Provider}, Target: {TargetKey}, Purpose: {Purpose}, Outcome: {Outcome}",
                    attemptCount - 1,
                    request.SanitizedProvider,
                    request.SanitizedTargetKey,
                    request.Purpose,
                    result.Outcome
                );
            }

            return result;
        }
        catch (OperationCanceledException exception) when (request.CancellationToken.IsCancellationRequested)
        {
            int observedAttempts = ObservedAttemptCount(attemptCount);
            _logger.LogWarning(
                exception,
                "DocumentCache writer retry aborted by caller after {AttemptCount} attempts. "
                    + "Provider: {Provider}, Target: {TargetKey}, Purpose: {Purpose}",
                observedAttempts,
                request.SanitizedProvider,
                request.SanitizedTargetKey,
                request.Purpose
            );

            return new DocumentCacheWriterResult.CallerAbortedRetry(observedAttempts);
        }
        catch (DocumentCacheWriterRetryableDeleteRaceException)
        {
            int observedAttempts = ObservedAttemptCount(attemptCount);
            LogRetryBudgetExhausted(request, observedAttempts, "DeleteRace");

            return new DocumentCacheWriterResult.DeleteRaceRetryExhausted(observedAttempts);
        }
        catch (DbException exception) when (_writeExceptionClassifier.IsTransientFailure(exception))
        {
            int observedAttempts = ObservedAttemptCount(attemptCount);
            LogRetryBudgetExhausted(request, observedAttempts, "ProviderTransient");

            return new DocumentCacheWriterResult.RetryBudgetExhausted(observedAttempts);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private ResiliencePipeline<DocumentCacheWriterResult> BuildPipeline()
    {
        var builder = new ResiliencePipelineBuilder<DocumentCacheWriterResult>();

        if (_settings.MaxRetryAttempts > 0)
        {
            builder.AddRetry(
                new RetryStrategyOptions<DocumentCacheWriterResult>
                {
                    BackoffType = DelayBackoffType.Exponential,
                    MaxRetryAttempts = _settings.MaxRetryAttempts,
                    Delay = TimeSpan.FromMilliseconds(_settings.BaseDelayMilliseconds),
                    UseJitter = _settings.UseJitter,
                    ShouldHandle = new PredicateBuilder<DocumentCacheWriterResult>()
                        .Handle<DbException>(_writeExceptionClassifier.IsTransientFailure)
                        .Handle<DocumentCacheWriterRetryableDeleteRaceException>(),
                    OnRetry = OnRetry,
                }
            );
        }

        return builder.Build();
    }

    private ValueTask OnRetry(OnRetryArguments<DocumentCacheWriterResult> args)
    {
        string provider = ReadContextProperty(args.Context, ProviderKey);
        string target = ReadContextProperty(args.Context, TargetKey);
        string purpose = ReadContextProperty(args.Context, PurposeKey);

        _logger.LogWarning(
            "DocumentCache writer retry attempt {DeadlockRetryAttempt}/{DeadlockRetryMaxAttempts} "
                + "after {DelayMs}ms. Provider: {Provider}, Target: {TargetKey}, Purpose: {Purpose}, Reason: {Reason}",
            args.AttemptNumber,
            _settings.MaxRetryAttempts,
            args.RetryDelay.TotalMilliseconds,
            provider,
            target,
            purpose,
            RetryReason(args.Outcome.Exception)
        );

        return ValueTask.CompletedTask;
    }

    private void LogRetryBudgetExhausted(
        DocumentCacheWriterRetryRequest request,
        int attemptCount,
        string reason
    )
    {
        LogLevel level = attemptCount > 1 ? LogLevel.Error : LogLevel.Warning;

        _logger.Log(
            level,
            "DocumentCache writer retry budget exhausted after {AttemptCount} attempts. "
                + "Provider: {Provider}, Target: {TargetKey}, Purpose: {Purpose}, Reason: {Reason}",
            attemptCount,
            request.SanitizedProvider,
            request.SanitizedTargetKey,
            request.Purpose,
            reason
        );
    }

    private static int ObservedAttemptCount(int attemptCount) => Math.Max(1, attemptCount);

    private static string ReadContextProperty(ResilienceContext context, ResiliencePropertyKey<string> key) =>
        context.Properties.TryGetValue(key, out string? value) ? value : "unknown";

    private static string RetryReason(Exception? exception) =>
        exception switch
        {
            DocumentCacheWriterRetryableDeleteRaceException => "DeleteRace",
            DbException => "ProviderTransient",
            _ => "Unknown",
        };
}

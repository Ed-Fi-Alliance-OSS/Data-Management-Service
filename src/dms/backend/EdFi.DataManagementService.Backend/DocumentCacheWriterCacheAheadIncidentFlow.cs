// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed record DocumentCacheWriterCacheAheadIncidentRequest
{
    public DocumentCacheWriterCacheAheadIncidentRequest(
        RelationalProviderToken providerToken,
        DocumentCacheProjectionTargetKey targetKey,
        DocumentCacheWriterPurpose purpose,
        TimeSpan incidentTimeout
    )
    {
        ProviderToken = providerToken ?? throw new ArgumentNullException(nameof(providerToken));
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        Purpose = DocumentCacheMaterializerGuards.RequireDefined(
            purpose,
            nameof(purpose),
            "Unsupported cache-writer purpose."
        );
        if (incidentTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(incidentTimeout),
                incidentTimeout,
                "Cache-ahead incident timeout must be positive."
            );
        }

        IncidentTimeout = incidentTimeout;
    }

    public RelationalProviderToken ProviderToken { get; }

    public DocumentCacheProjectionTargetKey TargetKey { get; }

    public DocumentCacheWriterPurpose Purpose { get; }

    public TimeSpan IncidentTimeout { get; }

    public string SanitizedProvider => LoggingSanitizer.SanitizeForLogging(ProviderToken.Value);

    public string SanitizedTargetKey =>
        LoggingSanitizer.SanitizeForLogging(
            $"{(TargetKey.TenantKey.Length == 0 ? "(default)" : TargetKey.TenantKey)}:{TargetKey.DataStoreId.Value}"
        );
}

internal enum DocumentCacheWriterCacheAheadIncidentAction
{
    ReturnLifecycleOrLatchFence = 1,
    ReturnCacheAheadDisappeared = 2,
    SetCacheAheadLatch = 3,
}

internal sealed record DocumentCacheWriterCacheAheadIncidentDecision
{
    private DocumentCacheWriterCacheAheadIncidentDecision(
        DocumentCacheWriterCacheAheadIncidentAction action,
        DocumentCacheWriterResult? terminalResult,
        long? sourceContentVersion,
        long? cacheContentVersion,
        DocumentCacheLifecycleState? lifecycleState
    )
    {
        Action = DocumentCacheMaterializerGuards.RequireDefined(
            action,
            nameof(action),
            "Unsupported cache-ahead incident action."
        );
        TerminalResult = terminalResult;
        SourceContentVersion = RequirePositiveWhenSupplied(
            sourceContentVersion,
            nameof(sourceContentVersion)
        );
        CacheContentVersion = RequirePositiveWhenSupplied(cacheContentVersion, nameof(cacheContentVersion));
        LifecycleState = lifecycleState;

        if (Action == DocumentCacheWriterCacheAheadIncidentAction.SetCacheAheadLatch)
        {
            if (TerminalResult is not null || SourceContentVersion is null || CacheContentVersion is null)
            {
                throw new ArgumentException(
                    "Cache-ahead latch decisions require current source/cache versions and no terminal result."
                );
            }

            _ = new DocumentCacheWriterResult.CacheAheadLatchSet(
                SourceContentVersion.Value,
                CacheContentVersion.Value
            );
            return;
        }

        if (TerminalResult is null)
        {
            throw new ArgumentException("Terminal cache-ahead decisions require a writer result.");
        }
    }

    public DocumentCacheWriterCacheAheadIncidentAction Action { get; }

    public DocumentCacheWriterResult? TerminalResult { get; }

    public long? SourceContentVersion { get; }

    public long? CacheContentVersion { get; }

    public DocumentCacheLifecycleState? LifecycleState { get; }

    public static DocumentCacheWriterCacheAheadIncidentDecision LifecycleOrLatchFence(
        DocumentCacheWriterResult.LifecycleOrLatchFenced result
    ) =>
        new(
            DocumentCacheWriterCacheAheadIncidentAction.ReturnLifecycleOrLatchFence,
            result ?? throw new ArgumentNullException(nameof(result)),
            sourceContentVersion: null,
            cacheContentVersion: null,
            result.LifecycleState
        );

    public static DocumentCacheWriterCacheAheadIncidentDecision CacheAheadDisappeared() =>
        new(
            DocumentCacheWriterCacheAheadIncidentAction.ReturnCacheAheadDisappeared,
            DocumentCacheWriterResult.CacheAheadDisappeared.Instance,
            sourceContentVersion: null,
            cacheContentVersion: null,
            lifecycleState: null
        );

    public static DocumentCacheWriterCacheAheadIncidentDecision SetCacheAheadLatch(
        long sourceContentVersion,
        long cacheContentVersion,
        DocumentCacheLifecycleState lifecycleState
    ) =>
        new(
            DocumentCacheWriterCacheAheadIncidentAction.SetCacheAheadLatch,
            terminalResult: null,
            sourceContentVersion,
            cacheContentVersion,
            lifecycleState
        );

    private static long? RequirePositiveWhenSupplied(long? value, string parameterName)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        }

        return value;
    }
}

internal static class DocumentCacheWriterCacheAheadIncidentFlow
{
    public static readonly TimeSpan DefaultIncidentTimeout = TimeSpan.FromSeconds(5);

    public static async Task<DocumentCacheWriterResult> ExecuteAsync(
        DocumentCacheWriterCacheAheadIncidentRequest request,
        Func<CancellationToken, Task<DocumentCacheWriterResult>> incidentTransaction,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(incidentTransaction);
        ArgumentNullException.ThrowIfNull(logger);

        using CancellationTokenSource incidentTimeout = new();
        incidentTimeout.CancelAfter(request.IncidentTimeout);

        try
        {
            return await incidentTransaction(incidentTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "DocumentCache cache-ahead incident flow was not confirmed. "
                    + "Provider: {Provider}, Target: {TargetKey}, Purpose: {Purpose}",
                request.SanitizedProvider,
                request.SanitizedTargetKey,
                request.Purpose
            );

            return DocumentCacheWriterResult.CacheAheadUnconfirmedCallerAbort.Instance;
        }
    }

    public static DocumentCacheWriterCacheAheadIncidentDecision SelectRecheckDecision(
        DocumentCacheWriterPurpose purpose,
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        DocumentCacheWriterCurrentStateObservation currentState,
        DocumentCacheWriterCandidateObservation candidateObservation
    )
    {
        DocumentCacheWriterClassificationSelection selection =
            DocumentCacheWriterClassificationSelector.Select(
                new DocumentCacheWriterClassificationRequest(
                    purpose,
                    lifecycleReadResult,
                    currentState,
                    candidateObservation
                )
            );

        if (selection.TerminalResult is DocumentCacheWriterResult.LifecycleOrLatchFenced lifecycleFence)
        {
            return DocumentCacheWriterCacheAheadIncidentDecision.LifecycleOrLatchFence(lifecycleFence);
        }

        if (!selection.RequestsCacheAheadLatchFlow)
        {
            return DocumentCacheWriterCacheAheadIncidentDecision.CacheAheadDisappeared();
        }

        return DocumentCacheWriterCacheAheadIncidentDecision.SetCacheAheadLatch(
            currentState.SourceContentVersion!.Value,
            currentState.CacheContentVersion!.Value,
            lifecycleReadResult.Lifecycle!.State
        );
    }

    public static DocumentCacheWriterResult CompleteLatchUpdate(
        DocumentCacheWriterCacheAheadIncidentDecision decision,
        int affectedRows
    )
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Action != DocumentCacheWriterCacheAheadIncidentAction.SetCacheAheadLatch)
        {
            throw new ArgumentException(
                "Only cache-ahead latch decisions can be completed with a latch update result.",
                nameof(decision)
            );
        }

        if (affectedRows == 1)
        {
            return new DocumentCacheWriterResult.CacheAheadLatchSet(
                decision.SourceContentVersion!.Value,
                decision.CacheContentVersion!.Value
            );
        }

        return new DocumentCacheWriterResult.LifecycleOrLatchFenced(
            DocumentCacheWriterFenceReason.CacheAheadRecoveryRequired,
            decision.LifecycleState,
            cacheAheadRecoveryRequired: true
        );
    }
}

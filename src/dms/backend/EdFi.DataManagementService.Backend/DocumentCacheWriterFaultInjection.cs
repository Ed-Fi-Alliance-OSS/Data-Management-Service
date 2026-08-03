// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend;

internal enum DocumentCacheWriterFaultInjectionHook
{
    AfterMainStateLockAndClassificationBeforeCacheDml = 1,
    AfterCacheDmlBeforeAcknowledgement = 2,
    AfterAcknowledgementBeforeCommit = 3,
    AfterCacheAheadLatchUpdateBeforeIncidentCommit = 4,
}

internal sealed record DocumentCacheWriterFaultInjectionContext
{
    private const int MaxLabelLength = 128;

    public DocumentCacheWriterFaultInjectionContext(
        DocumentCacheWriterFaultInjectionHook hook,
        RelationalProviderToken providerToken,
        DocumentCacheProjectionTargetKey targetKey,
        DocumentCacheWriterPurpose purpose,
        DocumentCacheLifecycleState? lifecycleState,
        bool? cacheAheadRecoveryRequired,
        DocumentCacheWriterOutcome outcome,
        int? cacheDmlRowCount = null,
        int? acknowledgementRowCount = null,
        int? cacheAheadLatchRowCount = null
    )
    {
        Hook = DocumentCacheMaterializerGuards.RequireDefined(
            hook,
            nameof(hook),
            "Unsupported DocumentCache writer fault-injection hook."
        );
        Provider = BoundLabel(
            LoggingSanitizer.SanitizeForLogging(
                (providerToken ?? throw new ArgumentNullException(nameof(providerToken))).Value
            )
        );
        TargetKey = FormatTargetKey(targetKey ?? throw new ArgumentNullException(nameof(targetKey)));
        Purpose = DocumentCacheMaterializerGuards.RequireDefined(
            purpose,
            nameof(purpose),
            "Unsupported cache-writer purpose."
        );
        if (lifecycleState is not null)
        {
            DocumentCacheMaterializerGuards.RequireDefined(
                lifecycleState.Value,
                nameof(lifecycleState),
                "Unsupported DocumentCache lifecycle state."
            );
        }

        LifecycleState = lifecycleState;
        CacheAheadRecoveryRequired = cacheAheadRecoveryRequired;
        Outcome = DocumentCacheMaterializerGuards.RequireDefined(
            outcome,
            nameof(outcome),
            "Unsupported cache-writer outcome."
        );
        CacheDmlRowCount = RequireNonNegativeWhenSupplied(cacheDmlRowCount, nameof(cacheDmlRowCount));
        AcknowledgementRowCount = RequireNonNegativeWhenSupplied(
            acknowledgementRowCount,
            nameof(acknowledgementRowCount)
        );
        CacheAheadLatchRowCount = RequireNonNegativeWhenSupplied(
            cacheAheadLatchRowCount,
            nameof(cacheAheadLatchRowCount)
        );
    }

    public DocumentCacheWriterFaultInjectionHook Hook { get; }

    public string Provider { get; }

    public string TargetKey { get; }

    public DocumentCacheWriterPurpose Purpose { get; }

    public DocumentCacheLifecycleState? LifecycleState { get; }

    public bool? CacheAheadRecoveryRequired { get; }

    public DocumentCacheWriterOutcome Outcome { get; }

    public int? CacheDmlRowCount { get; }

    public int? AcknowledgementRowCount { get; }

    public int? CacheAheadLatchRowCount { get; }

    private static string FormatTargetKey(DocumentCacheProjectionTargetKey targetKey)
    {
        string tenant = targetKey.TenantKey.Length == 0 ? "(default)" : targetKey.TenantKey;
        return BoundLabel(LoggingSanitizer.SanitizeForLogging($"{tenant}:{targetKey.DataStoreId.Value}"));
    }

    private static string BoundLabel(string value) =>
        value.Length <= MaxLabelLength ? value : value[..MaxLabelLength];

    private static int? RequireNonNegativeWhenSupplied(int? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{parameterName} must be nonnegative."
            );
        }

        return value;
    }
}

internal sealed class DocumentCacheWriterFaultInjectionControl
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;

    public DocumentCacheWriterFaultInjectionControl(DbConnection connection, DbTransaction transaction)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    public ValueTask CloseConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connection.Close();
        return ValueTask.CompletedTask;
    }

    public async ValueTask RollbackTransactionAsync(CancellationToken cancellationToken)
    {
        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal interface ITransactionFaultInjectionObserver
{
    ValueTask ObserveAsync(
        DocumentCacheWriterFaultInjectionContext context,
        DocumentCacheWriterFaultInjectionControl control,
        CancellationToken cancellationToken
    );
}

internal sealed class NoOpTransactionFaultInjectionObserver : ITransactionFaultInjectionObserver
{
    public static NoOpTransactionFaultInjectionObserver Instance { get; } = new();

    private NoOpTransactionFaultInjectionObserver() { }

    public ValueTask ObserveAsync(
        DocumentCacheWriterFaultInjectionContext context,
        DocumentCacheWriterFaultInjectionControl control,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(control);
        _ = cancellationToken;

        return ValueTask.CompletedTask;
    }
}

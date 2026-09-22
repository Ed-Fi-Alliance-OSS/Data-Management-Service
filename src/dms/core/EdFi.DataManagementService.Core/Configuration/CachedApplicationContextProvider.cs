// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Cached implementation of IApplicationContextProvider with stampede protection.
/// Uses HybridCache to ensure only one request fetches data on cache miss while others wait.
/// </summary>
public sealed class CachedApplicationContextProvider(
    IConfigurationServiceApplicationProvider configurationServiceApplicationProvider,
    HybridCache hybridCache,
    CacheSettings cacheSettings,
    ILogger<CachedApplicationContextProvider> logger
) : IApplicationContextProvider, IDisposable
{
    private const string CacheKeyPrefix = "ApplicationContext";
    private readonly HybridCacheEntryOptions _cacheEntryOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(cacheSettings.ApplicationContextCacheExpirationSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.ApplicationContextCacheExpirationSeconds),
    };

    // Keyed on clientId+tenant. Each entry is a Lazy<SharedFill> so only one fill is started per
    // key regardless of how many callers in this scope race to request it. The fill itself runs on
    // its own CancellationTokenSource (see SharedFill), decoupled from any individual caller's token,
    // so one caller cancelling never aborts a fill another caller still needs.
    private readonly ConcurrentDictionary<RequestLookupKey, Lazy<SharedFill>> _requestResults = [];

    /// <summary>
    /// Gets the request-scoped memoization key for a client ID and tenant.
    /// </summary>
    private static RequestLookupKey GetRequestLookupKey(string clientId, string? tenant) =>
        new(clientId, tenant?.ToLowerInvariant());

    /// <summary>
    /// Gets the cache key for a client ID and tenant.
    /// </summary>
    private static string GetCacheKey(string clientId, string? tenant) =>
        tenant is null
            ? $"{CacheKeyPrefix}:single:{clientId}"
            : $"{CacheKeyPrefix}:tenant:{tenant.ToLowerInvariant()}:{clientId}";

    /// <inheritdoc />
    public async Task<ApplicationContextResult> GetApplicationByClientIdAsync(
        string clientId,
        string? tenant,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("GetApplicationByClientIdAsync called with null or empty clientId");
            return new ApplicationContextResult.NotFound();
        }

        string cacheKey = GetCacheKey(clientId, tenant);
        return await AwaitSharedFillAsync(
            GetRequestLookupKey(clientId, tenant),
            fillCancellationToken =>
                GetOrCreateResultAsync(
                    cacheKey,
                    clientId,
                    innerCancellationToken =>
                        configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                            clientId,
                            tenant,
                            innerCancellationToken
                        ),
                    fillCancellationToken
                ),
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<ApplicationContextResult> ReloadApplicationByClientIdAsync(
        string clientId,
        string? tenant,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("ReloadApplicationByClientIdAsync called with null or empty clientId");
            return new ApplicationContextResult.NotFound();
        }

        RequestLookupKey key = GetRequestLookupKey(clientId, tenant);

        // Drop the request-scoped memo so a reload is not shadowed by an earlier lookup in this scope.
        _requestResults.TryRemove(key, out _);

        var cacheKey = GetCacheKey(clientId, tenant);
        await hybridCache.RemoveAsync(cacheKey, cancellationToken);

        return await AwaitSharedFillAsync(
            key,
            fillCancellationToken =>
                GetOrCreateResultAsync(
                    cacheKey,
                    clientId,
                    innerCancellationToken =>
                        configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                            clientId,
                            tenant,
                            innerCancellationToken
                        ),
                    fillCancellationToken
                ),
            cancellationToken
        );
    }

    /// <summary>
    /// Starts (or joins) the shared fill for a key and awaits it with the caller's own token.
    /// The fill itself keeps running on its own token as long as at least one caller is still
    /// waiting on it; a caller's own cancellation only ends that caller's wait.
    /// </summary>
    private async Task<ApplicationContextResult> AwaitSharedFillAsync(
        RequestLookupKey key,
        Func<CancellationToken, Task<ApplicationContextResult>> startFill,
        CancellationToken callerCancellationToken
    )
    {
        SharedFill fill;
        while (true)
        {
            Lazy<SharedFill> lazyFill = _requestResults.GetOrAdd(
                key,
                new Lazy<SharedFill>(
                    () => new SharedFill(startFill),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            );
            fill = lazyFill.Value;

            if (fill.TryAddWaiter())
            {
                break;
            }

            // The memoized fill was abandoned: its last waiter left before it finished, so its own
            // token was cancelled and its outcome is not this caller's. Evict exactly that entry and
            // start a fresh fill. Joining is decided here, synchronously, rather than by a continuation
            // on the abandoned task, so a caller can never observe a cancellation that was never theirs.
            ((ICollection<KeyValuePair<RequestLookupKey, Lazy<SharedFill>>>)_requestResults).Remove(
                new KeyValuePair<RequestLookupKey, Lazy<SharedFill>>(key, lazyFill)
            );
        }

        try
        {
            return await fill.Task.WaitAsync(callerCancellationToken);
        }
        finally
        {
            fill.RemoveWaiter();
        }
    }

    private async Task<ApplicationContextResult> GetOrCreateResultAsync(
        string cacheKey,
        string clientId,
        Func<CancellationToken, Task<ApplicationContextResult>> loadApplicationContext,
        CancellationToken fillCancellationToken
    )
    {
        try
        {
            ApplicationContext applicationContext = await hybridCache.GetOrCreateAsync(
                cacheKey,
                async cacheFillToken =>
                {
                    ApplicationContextResult result = await loadApplicationContext(cacheFillToken);
                    return result switch
                    {
                        ApplicationContextResult.Success success => success.ApplicationContext,
                        _ => throw new ApplicationContextNotCacheableException(result),
                    };
                },
                _cacheEntryOptions,
                cancellationToken: fillCancellationToken
            );

            return new ApplicationContextResult.Success(applicationContext);
        }
        catch (ApplicationContextNotCacheableException exception)
        {
            if (exception.Result is ApplicationContextResult.NotFound)
            {
                logger.LogWarning(
                    exception,
                    "Application context not found for clientId: {ClientId}",
                    LoggingSanitizer.SanitizeInternalValueForLogging(clientId)
                );
            }

            return exception.Result;
        }
    }

    public sealed class ApplicationContextNotCacheableException(ApplicationContextResult result) : Exception
    {
        public ApplicationContextResult Result { get; } = result;
    }

    /// <summary>
    /// Disposes every fill this request scope created. The provider is scoped, so the container
    /// disposes it when the request scope ends, which is when no caller can still be waiting on a
    /// fill and no fill can still need its token.
    /// </summary>
    public void Dispose()
    {
        foreach (Lazy<SharedFill> lazyFill in _requestResults.Values)
        {
            if (lazyFill.IsValueCreated)
            {
                lazyFill.Value.Dispose();
            }
        }

        _requestResults.Clear();
    }

    private readonly record struct RequestLookupKey(string ClientId, string? Tenant);

    /// <summary>
    /// A fill that is shared by every caller currently waiting on the same key. The fill runs on
    /// its own CancellationTokenSource, cancelled only when the last waiter leaves before the fill
    /// has finished, so a single caller's cancellation never aborts a fill another caller still
    /// needs, and a cancelled caller's own token is never used as the fill's cancellation source.
    /// Once abandoned that way, the fill admits no further waiters: a later caller evicts it and
    /// starts its own. A fill that completed while waiters were present stays admitted as the
    /// request-scoped memo, whatever its waiter count does afterwards.
    /// </summary>
    private sealed class SharedFill : IDisposable
    {
        private readonly CancellationTokenSource _fillCancellationSource = new();
        private readonly object _gate = new();
        private int _waiterCount;
        private bool _abandoned;
        private bool _disposed;

        public SharedFill(Func<CancellationToken, Task<ApplicationContextResult>> startFill)
        {
            Task = startFill(_fillCancellationSource.Token);
        }

        public Task<ApplicationContextResult> Task { get; }

        /// <summary>
        /// Registers a caller's interest. Returns false when the fill was abandoned (or ended
        /// cancelled), in which case the caller must not await it.
        /// </summary>
        public bool TryAddWaiter()
        {
            lock (_gate)
            {
                if (_abandoned || Task.IsCanceled)
                {
                    return false;
                }

                _waiterCount++;
                return true;
            }
        }

        /// <summary>
        /// Releases a caller's interest. When the last waiter leaves a fill that has not finished,
        /// the fill is marked abandoned and its own token is cancelled. The source is not disposed
        /// here: the fill and the cache it feeds may still hold registrations on that token, so
        /// disposal waits for the owning request scope to end (see the provider's Dispose).
        /// </summary>
        public void RemoveWaiter()
        {
            bool cancelFill = false;
            lock (_gate)
            {
                _waiterCount--;
                if (_waiterCount == 0 && !_abandoned && !_disposed && !Task.IsCompleted)
                {
                    _abandoned = true;
                    cancelFill = true;
                }
            }

            if (cancelFill)
            {
                _fillCancellationSource.Cancel();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _fillCancellationSource.Dispose();
        }
    }
}

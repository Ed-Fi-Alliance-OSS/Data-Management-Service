// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Threading;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static EdFi.DataManagementService.Backend.External.LogSanitizer;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Process-local cache of compiled mapping sets keyed by <see cref="MappingSetKey" />.
/// </summary>
public sealed class MappingSetCache(
    Func<MappingSetKey, CancellationToken, Task<MappingSet>> compileAsync,
    ILogger? logger = null,
    TimeSpan? failureCooldown = null
)
{
    private readonly Func<MappingSetKey, CancellationToken, Task<MappingSet>> _compileAsync =
        compileAsync ?? throw new ArgumentNullException(nameof(compileAsync));

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// How long a faulted entry stays in cache before eviction. Defaults to zero
    /// (immediate eviction) so the next call retries, per the design spec.
    /// Callers may pass a non-zero value to prevent retry storms on permanently-failing keys.
    /// </summary>
    private readonly TimeSpan _failureCooldown = failureCooldown ?? TimeSpan.Zero;

    private readonly ConcurrentDictionary<MappingSetKey, CacheEntry> _cache = new();

    /// <summary>
    /// Gets or creates a compiled mapping set for <paramref name="key" />.
    /// </summary>
    /// <remarks>
    /// <paramref name="cancellationToken"/> cancels only the wait for completion.
    /// Once compilation starts it runs to completion so other waiters are not affected.
    /// </remarks>
    public async Task<MappingSet> GetOrCreateAsync(
        MappingSetKey key,
        CancellationToken cancellationToken = default
    )
    {
        var createdEntry = new CacheEntry();
        var cacheEntry = _cache.GetOrAdd(key, createdEntry);

        if (ReferenceEquals(cacheEntry, createdEntry))
        {
            cacheEntry.StartCompilation(key, _compileAsync, _cache, _failureCooldown);
        }
        else
        {
            _logger.LogDebug(
                "Mapping set cache hit for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}",
                SanitizeForLog(key.EffectiveSchemaHash),
                key.Dialect
            );
        }

        return await cacheEntry.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class CacheEntry
    {
        private readonly TaskCompletionSource<MappingSet> _completionSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _compilationStarted;

        public Task<MappingSet> WaitAsync(CancellationToken cancellationToken) =>
            _completionSource.Task.WaitAsync(cancellationToken);

        public void StartCompilation(
            MappingSetKey key,
            Func<MappingSetKey, CancellationToken, Task<MappingSet>> compileAsync,
            ConcurrentDictionary<MappingSetKey, CacheEntry> cache,
            TimeSpan failureCooldown
        )
        {
            ArgumentNullException.ThrowIfNull(compileAsync);

            if (Interlocked.Exchange(ref _compilationStarted, 1) != 0)
            {
                throw new InvalidOperationException("Cache entry compilation was already started.");
            }

            _ = CompileAndPublishAsync(key, compileAsync, cache, failureCooldown);
        }

        /// <summary>
        /// Compiles a mapping set. On failure, the faulted entry is evicted after
        /// <paramref name="failureCooldown"/> (default zero = immediate) so a fresh
        /// retry can happen.
        /// </summary>
        private async Task CompileAndPublishAsync(
            MappingSetKey key,
            Func<MappingSetKey, CancellationToken, Task<MappingSet>> compileAsync,
            ConcurrentDictionary<MappingSetKey, CacheEntry> cache,
            TimeSpan failureCooldown
        )
        {
            try
            {
                // CancellationToken.None: compilation is shared across callers and must not
                // be cancelled by any single waiter.
                var mappingSet = await compileAsync(key, CancellationToken.None).ConfigureAwait(false);
                _completionSource.TrySetResult(mappingSet);
            }
            catch (OperationCanceledException ex)
            {
                // Cancellation is transient — evict immediately so retries can start fresh.
                cache.TryRemove(new KeyValuePair<MappingSetKey, CacheEntry>(key, this));
                _completionSource.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                // Keep the faulted entry in cache for the cooldown period so that
                // concurrent/subsequent requests see the cached failure immediately
                // instead of triggering redundant compilation attempts.
                _completionSource.TrySetException(ex);
                _ = EvictAfterCooldownAsync(key, cache, failureCooldown);
            }
        }

        private static async Task EvictAfterCooldownAsync(
            MappingSetKey key,
            ConcurrentDictionary<MappingSetKey, CacheEntry> cache,
            TimeSpan cooldown
        )
        {
            await Task.Delay(cooldown).ConfigureAwait(false);
            cache.TryRemove(key, out _);
        }
    }
}

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
    ILogger? logger = null
)
{
    private readonly Func<MappingSetKey, CancellationToken, Task<MappingSet>> _compileAsync =
        compileAsync ?? throw new ArgumentNullException(nameof(compileAsync));

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

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
            cacheEntry.StartCompilation(key, _compileAsync, _cache);
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
            ConcurrentDictionary<MappingSetKey, CacheEntry> cache
        )
        {
            ArgumentNullException.ThrowIfNull(compileAsync);

            if (Interlocked.Exchange(ref _compilationStarted, 1) != 0)
            {
                throw new InvalidOperationException("Cache entry compilation was already started.");
            }

            _ = CompileAndPublishAsync(key, compileAsync, cache);
        }

        /// <summary>
        /// Compiles a mapping set and evicts the cache entry if compilation fails so subsequent calls can retry.
        /// </summary>
        private async Task CompileAndPublishAsync(
            MappingSetKey key,
            Func<MappingSetKey, CancellationToken, Task<MappingSet>> compileAsync,
            ConcurrentDictionary<MappingSetKey, CacheEntry> cache
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
                cache.TryRemove(new KeyValuePair<MappingSetKey, CacheEntry>(key, this));
                _completionSource.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                cache.TryRemove(new KeyValuePair<MappingSetKey, CacheEntry>(key, this));
                _completionSource.TrySetException(ex);
            }
        }
    }
}

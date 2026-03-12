// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Threading;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Process-local cache of compiled mapping sets keyed by <see cref="MappingSetKey" />.
/// </summary>
public sealed class MappingSetCache(Func<MappingSetKey, Task<MappingSet>> compileAsync)
{
    private readonly Func<MappingSetKey, Task<MappingSet>> _compileAsync =
        compileAsync ?? throw new ArgumentNullException(nameof(compileAsync));

    private readonly ConcurrentDictionary<MappingSetKey, CacheEntry> _cache = new();

    /// <summary>
    /// Gets or creates a compiled mapping set for <paramref name="key" />.
    /// </summary>
    /// <remarks>
    /// Cancellation only cancels waiting for completion. Compilation continues once started.
    /// </remarks>
    public async Task<MappingSet> GetOrCreateAsync(
        MappingSetKey key,
        CancellationToken cancellationToken = default
    )
    {
        return (
            await GetOrCreateWithCacheStatusAsync(key, cancellationToken).ConfigureAwait(false)
        ).MappingSet;
    }

    /// <summary>
    /// Gets or creates a compiled mapping set for <paramref name="key" /> and indicates how this
    /// caller obtained the returned mapping set.
    /// </summary>
    public async Task<MappingSetCacheResult> GetOrCreateWithCacheStatusAsync(
        MappingSetKey key,
        CancellationToken cancellationToken = default
    )
    {
        var createdEntry = new CacheEntry();
        var cacheEntry = _cache.GetOrAdd(key, createdEntry);
        var cacheStatus =
            ReferenceEquals(cacheEntry, createdEntry) ? MappingSetCacheStatus.Compiled
            : cacheEntry.IsCompletedSuccessfully ? MappingSetCacheStatus.ReusedCompleted
            : MappingSetCacheStatus.JoinedInFlight;

        if (cacheStatus == MappingSetCacheStatus.Compiled)
        {
            cacheEntry.StartCompilation(key, _compileAsync, _cache);
        }

        var mappingSet = await cacheEntry.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new MappingSetCacheResult(MappingSet: mappingSet, CacheStatus: cacheStatus);
    }

    private sealed class CacheEntry
    {
        private readonly TaskCompletionSource<MappingSet> _completionSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _compilationStarted;

        public bool IsCompletedSuccessfully => _completionSource.Task.IsCompletedSuccessfully;

        public Task<MappingSet> WaitAsync(CancellationToken cancellationToken) =>
            _completionSource.Task.WaitAsync(cancellationToken);

        public void StartCompilation(
            MappingSetKey key,
            Func<MappingSetKey, Task<MappingSet>> compileAsync,
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
            Func<MappingSetKey, Task<MappingSet>> compileAsync,
            ConcurrentDictionary<MappingSetKey, CacheEntry> cache
        )
        {
            try
            {
                var mappingSet = await compileAsync(key).ConfigureAwait(false);
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

/// <summary>
/// Result returned from <see cref="MappingSetCache.GetOrCreateWithCacheStatusAsync" />.
/// </summary>
public readonly record struct MappingSetCacheResult(MappingSet MappingSet, MappingSetCacheStatus CacheStatus);

/// <summary>
/// Describes how a caller obtained a mapping set from <see cref="MappingSetCache" />.
/// </summary>
public enum MappingSetCacheStatus
{
    /// <summary>
    /// This caller won cache creation and started the compilation.
    /// </summary>
    Compiled,

    /// <summary>
    /// This caller found an existing cache entry whose compilation had not completed yet.
    /// </summary>
    JoinedInFlight,

    /// <summary>
    /// This caller found an existing cache entry whose compilation had already completed successfully.
    /// </summary>
    ReusedCompleted,
}

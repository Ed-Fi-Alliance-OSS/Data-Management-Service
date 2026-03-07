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

    private readonly ConcurrentDictionary<MappingSetKey, Lazy<Task<MappingSet>>> _cache = new();

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
    /// Gets or creates a compiled mapping set for <paramref name="key" /> and indicates whether
    /// the returned mapping set came from an existing cache entry.
    /// </summary>
    public async Task<MappingSetCacheResult> GetOrCreateWithCacheStatusAsync(
        MappingSetKey key,
        CancellationToken cancellationToken = default
    )
    {
        var createdEntry = CreateCacheEntry(key);
        var cacheEntry = _cache.GetOrAdd(key, createdEntry);
        var mappingSet = await cacheEntry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new MappingSetCacheResult(
            MappingSet: mappingSet,
            WasCacheHit: !ReferenceEquals(cacheEntry, createdEntry)
        );
    }

    /// <summary>
    /// Creates a lazy cache entry that performs at most one in-flight compilation and supports eviction on failure.
    /// </summary>
    private Lazy<Task<MappingSet>> CreateCacheEntry(MappingSetKey key)
    {
        Lazy<Task<MappingSet>> cacheEntry = null!;
        cacheEntry = new Lazy<Task<MappingSet>>(
            () => CompileAndEvictOnFailureAsync(key, cacheEntry),
            LazyThreadSafetyMode.ExecutionAndPublication
        );

        return cacheEntry;
    }

    /// <summary>
    /// Compiles a mapping set and evicts the cache entry if compilation fails so subsequent calls can retry.
    /// </summary>
    private async Task<MappingSet> CompileAndEvictOnFailureAsync(
        MappingSetKey key,
        Lazy<Task<MappingSet>> cacheEntry
    )
    {
        try
        {
            return await _compileAsync(key).ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<MappingSetKey, Lazy<Task<MappingSet>>>(key, cacheEntry));
            throw;
        }
    }
}

/// <summary>
/// Result returned from <see cref="MappingSetCache.GetOrCreateWithCacheStatusAsync" />.
/// </summary>
public readonly record struct MappingSetCacheResult(MappingSet MappingSet, bool WasCacheHit);

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Decorator that adds caching functionality with stampede protection to claim set retrieval.
/// Uses IMemoryCache to keep the full claim-set graph in process and avoid HybridCache payload serialization limits.
/// Supports tenant-keyed caching for multi-tenant scenarios.
/// </summary>
public class CachedClaimSetProvider(
    IConfigurationServiceClaimSetProvider claimSetProvider,
    IMemoryCache memoryCache,
    CacheSettings cacheSettings,
    ILogger<CachedClaimSetProvider> logger
) : IClaimSetProvider
{
    private const string CacheKeyPrefix = "ClaimSets";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheKeyLocks = new();

    /// <summary>
    /// Gets the cache key for a tenant, supporting both single and multi-tenant modes.
    /// </summary>
    internal static string GetCacheKey(string? tenant) =>
        string.IsNullOrEmpty(tenant) ? CacheKeyPrefix : $"{CacheKeyPrefix}:{tenant}";

    /// <summary>
    /// Retrieves claim sets from cache if available, otherwise fetches from the provider
    /// and caches the result for future requests. Uses tenant-specific cache keys in multi-tenant mode.
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant scenarios.</param>
    public async Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null)
    {
        var cacheKey = GetCacheKey(tenant);

        if (memoryCache.TryGetValue(cacheKey, out IList<ClaimSet>? cachedClaimSets))
        {
            return cachedClaimSets ?? [];
        }

        var cacheKeyLock = _cacheKeyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await cacheKeyLock.WaitAsync();
        try
        {
            if (memoryCache.TryGetValue(cacheKey, out cachedClaimSets))
            {
                return cachedClaimSets ?? [];
            }

            logger.LogDebug(
                "Cache miss for claim sets, fetching from CMS for tenant: {Tenant}",
                LoggingSanitizer.SanitizeForLogging(tenant)
            );

            var claimSets = await claimSetProvider.GetAllClaimSets(tenant);
            if (claimSets is null)
            {
                return [];
            }

            memoryCache.Set(
                cacheKey,
                claimSets,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(
                        cacheSettings.ClaimSetsCacheExpirationSeconds
                    ),
                }.RegisterPostEvictionCallback(
                    static (key, _, _, state) =>
                    {
                        var callbackState = (CacheKeyLockState)state!;
                        if (key is string cacheKey)
                        {
                            (
                                (ICollection<KeyValuePair<string, SemaphoreSlim>>)callbackState.CacheKeyLocks
                            ).Remove(new KeyValuePair<string, SemaphoreSlim>(cacheKey, callbackState.Lock));
                        }
                    },
                    new CacheKeyLockState(_cacheKeyLocks, cacheKeyLock)
                )
            );

            return claimSets;
        }
        finally
        {
            cacheKeyLock.Release();
        }
    }

    /// <summary>
    /// Invalidates the claim sets cache for a specific tenant.
    /// Called during manual reload operations.
    /// </summary>
    /// <param name="tenant">Optional tenant identifier. When null, invalidates default cache.</param>
    public Task InvalidateCacheAsync(string? tenant = null)
    {
        var cacheKey = GetCacheKey(tenant);
        memoryCache.Remove(cacheKey);
        _cacheKeyLocks.TryRemove(cacheKey, out _);
        logger.LogInformation(
            "Invalidated claim sets cache for tenant: {Tenant}",
            LoggingSanitizer.SanitizeForLogging(tenant)
        );
        return Task.CompletedTask;
    }

    private sealed record CacheKeyLockState(
        ConcurrentDictionary<string, SemaphoreSlim> CacheKeyLocks,
        SemaphoreSlim Lock
    );
}

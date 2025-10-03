// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Caching.Memory;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Memory cache wrapper for storing and retrieving application contexts with configurable expiration.
/// </summary>
public class ApplicationContextCache(IMemoryCache memoryCache, TimeSpan expiration)
{
    // Cache key prefix for application context entries
    private const string CacheKeyPrefix = "ApplicationContext_";

    /// <summary>
    /// Caches an application context by client ID
    /// </summary>
    /// <param name="clientId">The client identifier to use as cache key</param>
    /// <param name="applicationContext">The application context to cache</param>
    public void CacheApplicationContext(string clientId, ApplicationContext applicationContext)
    {
        string cacheKey = $"{CacheKeyPrefix}{clientId}";
        memoryCache.Set(cacheKey, applicationContext, expiration);
    }

    /// <summary>
    /// Retrieves a cached application context by client ID
    /// </summary>
    /// <param name="clientId">The client identifier to lookup</param>
    /// <returns>ApplicationContext if found in cache, null otherwise</returns>
    public ApplicationContext? GetCachedApplicationContext(string clientId)
    {
        string cacheKey = $"{CacheKeyPrefix}{clientId}";
        memoryCache.TryGetValue(cacheKey, out ApplicationContext? applicationContext);
        return applicationContext;
    }

    /// <summary>
    /// Removes a specific application context from the cache
    /// </summary>
    /// <param name="clientId">The client identifier to remove from cache</param>
    public void ClearCacheForClient(string clientId)
    {
        string cacheKey = $"{CacheKeyPrefix}{clientId}";
        memoryCache.Remove(cacheKey);
    }

    /// <summary>
    /// Clears all application context entries from the cache
    /// </summary>
    public void ClearAllCache()
    {
        // Note: IMemoryCache doesn't provide a way to enumerate keys
        // This would require tracking all cached keys separately if needed
        // For now, entries will expire based on TTL
    }
}

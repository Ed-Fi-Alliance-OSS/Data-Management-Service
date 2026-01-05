// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Decorator that adds caching functionality with stampede protection to claim set retrieval.
/// Uses HybridCache to ensure only one request fetches data on cache miss while others wait.
/// Supports tenant-keyed caching for multi-tenant scenarios.
/// </summary>
public class CachedClaimSetProvider(
    IConfigurationServiceClaimSetProvider claimSetProvider,
    HybridCache hybridCache,
    CacheSettings cacheSettings,
    ILogger<CachedClaimSetProvider> logger
) : IClaimSetProvider
{
    private const string CacheKeyPrefix = "ClaimSets";

    /// <summary>
    /// Gets the cache key for a tenant, supporting both single and multi-tenant modes.
    /// </summary>
    internal static string GetCacheKey(string? tenant) =>
        string.IsNullOrEmpty(tenant) ? CacheKeyPrefix : $"{CacheKeyPrefix}:{tenant}";

    /// <summary>
    /// Retrieves claim sets from cache if available, otherwise fetches from the provider
    /// and caches the result for future requests. Uses tenant-specific cache keys in multi-tenant mode.
    /// HybridCache provides stampede protection - only one caller executes the factory on cache miss.
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant scenarios.</param>
    public async Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null)
    {
        var cacheKey = GetCacheKey(tenant);

        // HybridCache.GetOrCreateAsync provides stampede protection:
        // Only one concurrent caller executes the factory; others wait for the result
        var claimSets = await hybridCache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                logger.LogDebug(
                    "Cache miss for claim sets, fetching from CMS for tenant: {Tenant}",
                    LoggingSanitizer.SanitizeForLogging(tenant)
                );
                return await claimSetProvider.GetAllClaimSets(tenant);
            },
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(cacheSettings.ClaimSetsCacheExpirationSeconds),
                LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.ClaimSetsCacheExpirationSeconds),
            }
        );

        return claimSets ?? [];
    }

    /// <summary>
    /// Invalidates the claim sets cache for a specific tenant.
    /// Called during manual reload operations.
    /// </summary>
    /// <param name="tenant">Optional tenant identifier. When null, invalidates default cache.</param>
    public async Task InvalidateCacheAsync(string? tenant = null)
    {
        var cacheKey = GetCacheKey(tenant);
        await hybridCache.RemoveAsync(cacheKey);
        logger.LogInformation(
            "Invalidated claim sets cache for tenant: {Tenant}",
            LoggingSanitizer.SanitizeForLogging(tenant)
        );
    }
}

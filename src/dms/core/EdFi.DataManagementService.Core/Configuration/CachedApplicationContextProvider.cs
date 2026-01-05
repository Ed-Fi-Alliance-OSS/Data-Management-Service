// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Cached implementation of IApplicationContextProvider with stampede protection.
/// Uses HybridCache to ensure only one request fetches data on cache miss while others wait.
/// </summary>
public class CachedApplicationContextProvider(
    IConfigurationServiceApplicationProvider configurationServiceApplicationProvider,
    HybridCache hybridCache,
    CacheSettings cacheSettings,
    ILogger<CachedApplicationContextProvider> logger
) : IApplicationContextProvider
{
    private const string CacheKeyPrefix = "ApplicationContext";

    /// <summary>
    /// Gets the cache key for a client ID.
    /// </summary>
    private static string GetCacheKey(string clientId) => $"{CacheKeyPrefix}:{clientId}";

    /// <inheritdoc />
    public async Task<ApplicationContext?> GetApplicationByClientIdAsync(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("GetApplicationByClientIdAsync called with null or empty clientId");
            return null;
        }

        var cacheKey = GetCacheKey(clientId);

        // HybridCache.GetOrCreateAsync provides stampede protection:
        // Only one concurrent caller executes the factory; others wait for the result
        return await hybridCache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                // First attempt
                var context = await configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    clientId
                );

                if (context != null)
                {
                    return context;
                }

                // Not found on first try - try reloading (it might be a new client)
                context = await configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    clientId
                );

                if (context == null)
                {
                    logger.LogWarning(
                        "Application context not found for clientId: {ClientId}",
                        LoggingSanitizer.SanitizeForLogging(clientId)
                    );
                }

                return context;
            },
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(cacheSettings.ApplicationContextCacheExpirationMinutes),
                LocalCacheExpiration = TimeSpan.FromMinutes(
                    cacheSettings.ApplicationContextCacheExpirationMinutes
                ),
            }
        );
    }

    /// <inheritdoc />
    public async Task<ApplicationContext?> ReloadApplicationByClientIdAsync(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("ReloadApplicationByClientIdAsync called with null or empty clientId");
            return null;
        }

        // Clear cache first, then fetch fresh
        var cacheKey = GetCacheKey(clientId);
        await hybridCache.RemoveAsync(cacheKey);

        return await hybridCache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                return await configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    clientId
                );
            },
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(cacheSettings.ApplicationContextCacheExpirationMinutes),
                LocalCacheExpiration = TimeSpan.FromMinutes(
                    cacheSettings.ApplicationContextCacheExpirationMinutes
                ),
            }
        );
    }
}

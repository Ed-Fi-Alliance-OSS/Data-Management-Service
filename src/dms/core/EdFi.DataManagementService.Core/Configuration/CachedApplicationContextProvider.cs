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
public class CachedApplicationContextProvider(
    IConfigurationServiceApplicationProvider configurationServiceApplicationProvider,
    HybridCache hybridCache,
    CacheSettings cacheSettings,
    ILogger<CachedApplicationContextProvider> logger
) : IApplicationContextProvider
{
    private const string CacheKeyPrefix = "ApplicationContext";
    private readonly HybridCacheEntryOptions _cacheEntryOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(cacheSettings.ApplicationContextCacheExpirationSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.ApplicationContextCacheExpirationSeconds),
    };
    private readonly ConcurrentDictionary<
        RequestLookupKey,
        Lazy<Task<ApplicationContextResult>>
    > _requestResults = [];

    /// <summary>
    /// Gets the cache key for a client ID and tenant.
    /// </summary>
    private static string GetCacheKey(string clientId, string? tenant) =>
        tenant is null
            ? $"{CacheKeyPrefix}:single:{clientId}"
            : $"{CacheKeyPrefix}:tenant:{tenant.ToLowerInvariant()}:{clientId}";

    /// <inheritdoc />
    public Task<ApplicationContextResult> GetApplicationByClientIdAsync(string clientId, string? tenant)
    {
        var lookup = new Lazy<Task<ApplicationContextResult>>(
            () => GetFirstApplicationContextAsync(clientId, tenant),
            LazyThreadSafetyMode.ExecutionAndPublication
        );
        RequestLookupKey key = new(clientId, tenant?.ToLowerInvariant());
        Lazy<Task<ApplicationContextResult>> requestResult = _requestResults.GetOrAdd(key, lookup);

        return requestResult.Value;
    }

    private async Task<ApplicationContextResult> GetFirstApplicationContextAsync(
        string clientId,
        string? tenant
    )
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("GetApplicationByClientIdAsync called with null or empty clientId");
            return new ApplicationContextResult.Unavailable();
        }

        return await GetOrCreateResultAsync(
            GetCacheKey(clientId, tenant),
            clientId,
            () => configurationServiceApplicationProvider.GetApplicationByClientIdAsync(clientId, tenant)
        );
    }

    /// <inheritdoc />
    public async Task<ApplicationContextResult> ReloadApplicationByClientIdAsync(
        string clientId,
        string? tenant
    )
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("ReloadApplicationByClientIdAsync called with null or empty clientId");
            return new ApplicationContextResult.Unavailable();
        }

        var cacheKey = GetCacheKey(clientId, tenant);
        await hybridCache.RemoveAsync(cacheKey);

        return await GetOrCreateResultAsync(
            cacheKey,
            clientId,
            () => configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(clientId, tenant)
        );
    }

    private async Task<ApplicationContextResult> GetOrCreateResultAsync(
        string cacheKey,
        string clientId,
        Func<Task<ApplicationContextResult>> loadApplicationContext
    )
    {
        try
        {
            ApplicationContext applicationContext = await hybridCache.GetOrCreateAsync(
                cacheKey,
                async _ =>
                {
                    ApplicationContextResult result = await loadApplicationContext();
                    return result switch
                    {
                        ApplicationContextResult.Success success => success.ApplicationContext,
                        _ => throw new ApplicationContextNotCacheableException(result),
                    };
                },
                _cacheEntryOptions
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
                    LoggingSanitizer.SanitizeForLogging(clientId)
                );
            }

            return exception.Result;
        }
    }

    public sealed class ApplicationContextNotCacheableException(ApplicationContextResult result) : Exception
    {
        public ApplicationContextResult Result { get; } = result;
    }

    private readonly record struct RequestLookupKey(string ClientId, string? Tenant);
}

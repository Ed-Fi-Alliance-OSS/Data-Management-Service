// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
) : IApplicationContextProvider
{
    private const string CacheKeyPrefix = "ApplicationContext";
    private readonly HybridCacheEntryOptions _cacheEntryOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(cacheSettings.ApplicationContextCacheExpirationSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.ApplicationContextCacheExpirationSeconds),
    };

    // Memoizes completed results only, keyed on clientId+tenant, for the life of this request scope.
    // A caller's own OperationCanceledException is never stored here: it propagates before this
    // dictionary is touched, so one caller cancelling never poisons what a later caller in the same
    // scope sees.
    private readonly ConcurrentDictionary<RequestLookupKey, ApplicationContextResult> _requestResults = [];

    // Reload generation per cache key, shared by every provider instance over the same HybridCache.
    // A reload advances the generation and writes under the new key, so a fetch that was already in
    // flight when the reload began finishes into the previous generation's key, which no later
    // lookup reads, instead of overwriting the reload's result. Generation 0 is the plain key.
    private static readonly ConditionalWeakTable<
        HybridCache,
        ConcurrentDictionary<string, long>
    > _reloadGenerationsByCache = [];

    private ConcurrentDictionary<string, long> ReloadGenerations =>
        _reloadGenerationsByCache.GetValue(hybridCache, static _ => new ConcurrentDictionary<string, long>());

    private string GetCurrentCacheKey(string clientId, string? tenant)
    {
        string baseKey = GetCacheKey(clientId, tenant);
        return ReloadGenerations.TryGetValue(baseKey, out long generation)
            ? GetGenerationCacheKey(baseKey, generation)
            : baseKey;
    }

    private static string GetGenerationCacheKey(string baseKey, long generation) =>
        generation == 0 ? baseKey : $"{baseKey}:reload:{generation}";

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
        // A pre-cancelled caller must never join (and thereby keep alive) a HybridCache fetch: there
        // is no result it could ever observe, so it should not pay for - or cause - one.
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("GetApplicationByClientIdAsync called with null or empty clientId");
            return new ApplicationContextResult.NotFound();
        }

        RequestLookupKey key = GetRequestLookupKey(clientId, tenant);
        if (_requestResults.TryGetValue(key, out ApplicationContextResult? memoized))
        {
            return memoized;
        }

        string cacheKey = GetCurrentCacheKey(clientId, tenant);
        ApplicationContextResult result = await GetOrCreateResultAsync(
            cacheKey,
            clientId,
            innerCancellationToken =>
                configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    clientId,
                    tenant,
                    innerCancellationToken
                ),
            cancellationToken
        );

        // GetOrAdd rather than an unconditional write, so two concurrent same-scope callers that both
        // missed the memo settle on one shared result instead of each overwriting the other's.
        return _requestResults.GetOrAdd(key, result);
    }

    /// <inheritdoc />
    public async Task<ApplicationContextResult> ReloadApplicationByClientIdAsync(
        string clientId,
        string? tenant,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("ReloadApplicationByClientIdAsync called with null or empty clientId");
            return new ApplicationContextResult.NotFound();
        }

        RequestLookupKey key = GetRequestLookupKey(clientId, tenant);

        // Drop the request-scoped memo so a concurrent Get in this scope does not reuse a pre-reload
        // result once the reload has started.
        _requestResults.TryRemove(key, out _);

        string baseKey = GetCacheKey(clientId, tenant);
        string previousCacheKey = GetCurrentCacheKey(clientId, tenant);
        long generation = ReloadGenerations.AddOrUpdate(baseKey, 1, static (_, current) => current + 1);
        string cacheKey = GetGenerationCacheKey(baseKey, generation);

        // The pre-reload entry is removed before the Configuration Service is asked, and neither
        // cache write takes the caller's token: a reload that has started must never leave the old
        // entry behind, and one the Configuration Service has answered must record that answer even
        // if its caller has since gone.
        await hybridCache.RemoveAsync(previousCacheKey, CancellationToken.None);

        // The provider is called directly rather than through HybridCache.GetOrCreateAsync: joining
        // an in-flight Get's factory here could hand the reload back pre-reload data instead of
        // actually reloading it.
        ApplicationContextResult result =
            await configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                clientId,
                tenant,
                cancellationToken
            );

        if (result is ApplicationContextResult.Success success)
        {
            await hybridCache.SetAsync(
                cacheKey,
                success.ApplicationContext,
                _cacheEntryOptions,
                cancellationToken: CancellationToken.None
            );
        }
        else if (result is ApplicationContextResult.NotFound)
        {
            logger.LogWarning(
                "Application context not found for clientId: {ClientId}",
                LoggingSanitizer.SanitizeInternalValueForLogging(clientId)
            );
        }

        _requestResults[key] = result;
        return result;
    }

    private async Task<ApplicationContextResult> GetOrCreateResultAsync(
        string cacheKey,
        string clientId,
        Func<CancellationToken, Task<ApplicationContextResult>> loadApplicationContext,
        CancellationToken cancellationToken
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
                cancellationToken: cancellationToken
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

    private readonly record struct RequestLookupKey(string ClientId, string? Tenant);
}

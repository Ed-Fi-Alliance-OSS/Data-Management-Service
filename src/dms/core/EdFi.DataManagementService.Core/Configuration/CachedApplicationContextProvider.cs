// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Cached implementation of IApplicationContextProvider that wraps ConfigurationServiceApplicationProvider
/// with a caching layer and retry logic
/// </summary>
public class CachedApplicationContextProvider(
    ConfigurationServiceApplicationProvider configurationServiceApplicationProvider,
    ApplicationContextCache applicationContextCache,
    ILogger<CachedApplicationContextProvider> logger
) : IApplicationContextProvider
{
    /// <inheritdoc />
    public async Task<ApplicationContext?> GetApplicationByClientIdAsync(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("GetApplicationByClientIdAsync called with null or empty clientId");
            return null;
        }

        // Try to get from cache first
        ApplicationContext? cachedContext = applicationContextCache.GetCachedApplicationContext(clientId);
        if (cachedContext != null)
        {
            logger.LogDebug("Application context found in cache for clientId: {ClientId}", clientId);
            return cachedContext;
        }

        logger.LogDebug(
            "Application context not in cache for clientId: {ClientId}, fetching from CMS",
            clientId
        );

        // Not in cache, fetch from CMS
        ApplicationContext? applicationContext =
            await configurationServiceApplicationProvider.GetApplicationByClientIdAsync(clientId);

        if (applicationContext != null)
        {
            // Cache the result
            applicationContextCache.CacheApplicationContext(clientId, applicationContext);
            logger.LogInformation(
                "Application context fetched and cached for clientId: {ClientId}, ApplicationId: {ApplicationId}",
                clientId,
                applicationContext.ApplicationId
            );
            return applicationContext;
        }

        // Not found in CMS on first try - try reloading (it might be a new client)
        logger.LogInformation(
            "Application not found for clientId: {ClientId}, attempting reload from CMS",
            clientId
        );

        applicationContext = await configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
            clientId
        );

        if (applicationContext != null)
        {
            // Cache the result
            applicationContextCache.CacheApplicationContext(clientId, applicationContext);
            logger.LogInformation(
                "Application context fetched on reload and cached for clientId: {ClientId}, ApplicationId: {ApplicationId}",
                clientId,
                applicationContext.ApplicationId
            );
            return applicationContext;
        }

        // Still not found after reload
        logger.LogWarning(
            "Application context not found for clientId: {ClientId} even after reload attempt",
            clientId
        );
        return null;
    }

    /// <inheritdoc />
    public async Task<ApplicationContext?> ReloadApplicationByClientIdAsync(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("ReloadApplicationByClientIdAsync called with null or empty clientId");
            return null;
        }

        logger.LogInformation("Force reloading application context for clientId: {ClientId}", clientId);

        // Clear cache first
        applicationContextCache.ClearCacheForClient(clientId);

        // Fetch fresh from CMS
        ApplicationContext? applicationContext =
            await configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(clientId);

        if (applicationContext != null)
        {
            // Cache the fresh result
            applicationContextCache.CacheApplicationContext(clientId, applicationContext);
            logger.LogInformation(
                "Application context reloaded and cached for clientId: {ClientId}, ApplicationId: {ApplicationId}",
                clientId,
                applicationContext.ApplicationId
            );
        }
        else
        {
            logger.LogWarning(
                "Application context not found on forced reload for clientId: {ClientId}",
                clientId
            );
        }

        return applicationContext;
    }
}

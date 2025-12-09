// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

/// <summary>
/// Validates that a tenant exists in the system.
/// </summary>
public interface ITenantValidator
{
    /// <summary>
    /// Validates that a tenant exists. Returns true if valid, false if not found.
    /// Handles cache-miss by attempting to reload from Configuration Service.
    /// </summary>
    /// <param name="tenant">The tenant identifier to validate</param>
    /// <returns>True if the tenant exists, false otherwise</returns>
    Task<bool> ValidateTenantAsync(string tenant);
}

/// <summary>
/// Validates tenants against the Configuration Service.
/// Checks local cache first, then reloads from Configuration Service on cache-miss.
/// </summary>
public class TenantValidator(IDmsInstanceProvider dmsInstanceProvider, ILogger<TenantValidator> logger)
    : ITenantValidator
{
    /// <inheritdoc />
    public async Task<bool> ValidateTenantAsync(string tenant)
    {
        if (string.IsNullOrEmpty(tenant))
        {
            return false;
        }

        // Check if tenant exists in cache
        if (dmsInstanceProvider.TenantExists(tenant))
        {
            logger.LogDebug("Tenant {Tenant} found in cache", LoggingSanitizer.SanitizeForLogging(tenant));
            return true;
        }

        // Cache miss - attempt to load instances for this tenant from Configuration Service
        // LoadDmsInstances populates _instancesByTenant, which is what TenantExists checks
        logger.LogDebug(
            "Tenant {Tenant} not found in cache, attempting to load from Configuration Service",
            LoggingSanitizer.SanitizeForLogging(tenant)
        );

        try
        {
            // Try to load instances for this specific tenant
            // If the tenant doesn't exist, this will return an empty list or throw
            var instances = await dmsInstanceProvider.LoadDmsInstances(tenant);

            // If we got here without an exception, the tenant exists
            // The instances are now cached, so TenantExists will return true
            logger.LogDebug(
                "Tenant {Tenant} loaded successfully with {InstanceCount} instances",
                LoggingSanitizer.SanitizeForLogging(tenant),
                instances.Count
            );
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Tenant doesn't exist - 404 from Configuration Service (expected, not an error)
            logger.LogDebug(
                ex,
                "Tenant {Tenant} not found in Configuration Service",
                LoggingSanitizer.SanitizeForLogging(tenant)
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to load tenant {Tenant} from Configuration Service",
                LoggingSanitizer.SanitizeForLogging(tenant)
            );
        }

        logger.LogDebug("Tenant {Tenant} validation failed", LoggingSanitizer.SanitizeForLogging(tenant));
        return false;
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Provides database connection strings using data store configurations
/// </summary>
public class DmsConnectionStringProvider(
    IDataStoreProvider dataStoreProvider,
    ILogger<DmsConnectionStringProvider> logger
) : IConnectionStringProvider
{
    /// <inheritdoc />
    public string? GetConnectionString(long dataStoreId, string? tenant = null)
    {
        logger.LogDebug("Retrieving connection string for data store ID {DataStoreId}", dataStoreId);

        DataStore? instance = dataStoreProvider.GetById(dataStoreId, tenant);

        if (instance == null)
        {
            logger.LogWarning(
                "Data store with ID {DataStoreId} not found. Available data stores: {DataStoreIds}",
                dataStoreId,
                string.Join(", ", dataStoreProvider.GetAll(tenant).Select(i => i.Id))
            );

            return null;
        }

        if (string.IsNullOrWhiteSpace(instance.ConnectionString))
        {
            logger.LogWarning(
                "Data store '{Name}' (ID: {DataStoreId}) exists but has no connection string configured",
                instance.Name,
                dataStoreId
            );

            return null;
        }

        logger.LogDebug(
            "Successfully retrieved connection string for data store '{Name}' (ID: {DataStoreId})",
            instance.Name,
            dataStoreId
        );

        return instance.ConnectionString;
    }

    /// <inheritdoc />
    public string? GetHealthCheckConnectionString()
    {
        logger.LogDebug("Retrieving connection string for health check purposes");

        // Called on every health probe, so the routine outcomes log at Debug. Only one state is a
        // genuine misconfiguration worth a repeating Warning: data stores exist, but none of them
        // has a connection string. "No tenant caches" and "tenant caches with no data stores" are
        // the same condition for the health check - nothing to probe yet - and both are normal
        // during multi-tenant onboarding (a tenant can exist before its data store does).
        var loadedTenants = dataStoreProvider.GetLoadedTenantKeys();
        int instancesSeen = 0;

        foreach (var tenantKey in loadedTenants)
        {
            var instances = dataStoreProvider.GetAll(string.IsNullOrEmpty(tenantKey) ? null : tenantKey);

            // Every instance in Id order, not just the lowest-Id one: a tenant whose first data
            // store lacks a connection string may still have a later one that can be probed.
            foreach (var instance in instances.OrderBy(x => x.Id))
            {
                instancesSeen++;

                if (!string.IsNullOrWhiteSpace(instance.ConnectionString))
                {
                    logger.LogDebug(
                        "Selected data store for health check: '{Name}' (ID: {DataStoreId}) from tenant '{Tenant}' ({TotalCount} instances in tenant)",
                        LoggingSanitizer.SanitizeInternalValueForLogging(instance.Name),
                        instance.Id,
                        string.IsNullOrEmpty(tenantKey)
                            ? "(default)"
                            : LoggingSanitizer.SanitizeInternalValueForLogging(tenantKey),
                        instances.Count
                    );

                    return instance.ConnectionString;
                }

                logger.LogDebug(
                    "Health check: Skipping data store '{Name}' (ID: {DataStoreId}) - no connection string configured",
                    LoggingSanitizer.SanitizeInternalValueForLogging(instance.Name),
                    instance.Id
                );
            }
        }

        if (instancesSeen == 0)
        {
            logger.LogDebug(
                "No data store instances are loaded ({TenantCount} tenant caches); there is no database to check yet",
                loadedTenants.Count
            );

            return null;
        }

        logger.LogWarning(
            "No data stores with valid connection strings found among {InstanceCount} data stores across {TenantCount} loaded tenant caches",
            instancesSeen,
            loadedTenants.Count
        );

        return null;
    }
}

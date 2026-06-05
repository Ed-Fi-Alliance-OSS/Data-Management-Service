// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
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

        // Search across all loaded tenant caches to find the first available instance
        var loadedTenants = dataStoreProvider.GetLoadedTenantKeys();

        if (loadedTenants.Count == 0)
        {
            logger.LogWarning(
                "No tenant caches are loaded. The data store provider has no instances. "
                    + "Check that instances were successfully loaded from the Configuration Service."
            );

            return null;
        }

        // Find the first instance with a valid connection string across all tenants
        foreach (var tenantKey in loadedTenants)
        {
            var instances = dataStoreProvider.GetAll(string.IsNullOrEmpty(tenantKey) ? null : tenantKey);

            if (instances.Count == 0)
            {
                continue;
            }

            var firstInstance = instances.OrderBy(x => x.Id).First();

            if (!string.IsNullOrWhiteSpace(firstInstance.ConnectionString))
            {
                logger.LogInformation(
                    "Selected data store for health check: '{Name}' (ID: {DataStoreId}) from tenant '{Tenant}' ({TotalCount} instances in tenant)",
                    firstInstance.Name,
                    firstInstance.Id,
                    string.IsNullOrEmpty(tenantKey) ? "(default)" : tenantKey,
                    instances.Count
                );

                return firstInstance.ConnectionString;
            }

            logger.LogDebug(
                "Health check: Skipping data store '{Name}' (ID: {DataStoreId}) - no connection string configured",
                firstInstance.Name,
                firstInstance.Id
            );
        }

        logger.LogWarning(
            "No data stores with valid connection strings found across {TenantCount} loaded tenant caches",
            loadedTenants.Count
        );

        return null;
    }
}

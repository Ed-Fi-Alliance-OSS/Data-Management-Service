// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Provides database connection strings using DMS instance configurations
/// </summary>
public class DmsConnectionStringProvider(
    IDmsInstanceProvider dmsInstanceProvider,
    ILogger<DmsConnectionStringProvider> logger
) : IConnectionStringProvider
{
    /// <inheritdoc />
    public string? GetConnectionString(long dmsInstanceId, string? tenant = null)
    {
        logger.LogDebug("Retrieving connection string for DMS instance ID {InstanceId}", dmsInstanceId);

        DmsInstance? instance = dmsInstanceProvider.GetById(dmsInstanceId, tenant);

        if (instance == null)
        {
            logger.LogWarning(
                "DMS instance with ID {InstanceId} not found. Available instances: {InstanceIds}",
                dmsInstanceId,
                string.Join(", ", dmsInstanceProvider.GetAll(tenant).Select(i => i.Id))
            );

            return null;
        }

        if (string.IsNullOrWhiteSpace(instance.ConnectionString))
        {
            logger.LogWarning(
                "DMS instance '{InstanceName}' (ID: {InstanceId}) exists but has no connection string configured",
                instance.InstanceName,
                dmsInstanceId
            );

            return null;
        }

        logger.LogDebug(
            "Successfully retrieved connection string for DMS instance '{InstanceName}' (ID: {InstanceId})",
            instance.InstanceName,
            dmsInstanceId
        );

        return instance.ConnectionString;
    }

    /// <inheritdoc />
    public string? GetHealthCheckConnectionString()
    {
        logger.LogDebug("Retrieving connection string for health check purposes");

        // Search across all loaded tenant caches to find the first available instance
        var loadedTenants = dmsInstanceProvider.GetLoadedTenantKeys();

        if (loadedTenants.Count == 0)
        {
            logger.LogWarning(
                "No tenant caches are loaded. The DMS instance provider has no instances. "
                    + "Check that instances were successfully loaded from the Configuration Service."
            );

            return null;
        }

        // Find the first instance with a valid connection string across all tenants
        foreach (var tenantKey in loadedTenants)
        {
            var instances = dmsInstanceProvider.GetAll(string.IsNullOrEmpty(tenantKey) ? null : tenantKey);

            if (instances.Count == 0)
            {
                continue;
            }

            var firstInstance = instances.OrderBy(x => x.Id).First();

            if (!string.IsNullOrWhiteSpace(firstInstance.ConnectionString))
            {
                logger.LogInformation(
                    "Selected DMS instance for health check: '{InstanceName}' (ID: {InstanceId}) from tenant '{Tenant}' ({TotalCount} instances in tenant)",
                    firstInstance.InstanceName,
                    firstInstance.Id,
                    string.IsNullOrEmpty(tenantKey) ? "(default)" : tenantKey,
                    instances.Count
                );

                return firstInstance.ConnectionString;
            }

            logger.LogDebug(
                "Health check: Skipping DMS instance '{InstanceName}' (ID: {InstanceId}) - no connection string configured",
                firstInstance.InstanceName,
                firstInstance.Id
            );
        }

        logger.LogWarning(
            "No DMS instances with valid connection strings found across {TenantCount} loaded tenant caches",
            loadedTenants.Count
        );

        return null;
    }
}

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
    public string? GetConnectionString(long dmsInstanceId)
    {
        logger.LogDebug("Retrieving connection string for DMS instance ID {InstanceId}", dmsInstanceId);

        DmsInstance? instance = dmsInstanceProvider.GetById(dmsInstanceId);

        if (instance == null)
        {
            logger.LogWarning(
                "DMS instance with ID {InstanceId} not found. Available instances: {InstanceIds}",
                dmsInstanceId,
                string.Join(", ", dmsInstanceProvider.GetAll().Select(i => i.Id))
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
    public string? GetDefaultConnectionString()
    {
        logger.LogDebug("Retrieving connection string for default DMS instance");

        // Use the first available DMS instance (ordered by ID for deterministic selection)
        var allInstances = dmsInstanceProvider.GetAll();

        if (allInstances.Count == 0)
        {
            logger.LogWarning(
                "No DMS instances are configured. The DMS instance provider returned an empty list. "
                    + "Check that instances were successfully loaded from the Configuration Service."
            );

            return null;
        }

        var firstInstance = allInstances.OrderBy(x => x.Id).First();

        logger.LogInformation(
            "Selected default DMS instance: '{InstanceName}' (ID: {InstanceId}) from {TotalCount} available instances",
            firstInstance.InstanceName,
            firstInstance.Id,
            allInstances.Count
        );

        if (string.IsNullOrWhiteSpace(firstInstance.ConnectionString))
        {
            logger.LogWarning(
                "Default DMS instance '{InstanceName}' (ID: {InstanceId}) has no connection string configured",
                firstInstance.InstanceName,
                firstInstance.Id
            );

            return null;
        }

        logger.LogDebug("Successfully retrieved connection string for default DMS instance");

        return firstInstance.ConnectionString;
    }
}
